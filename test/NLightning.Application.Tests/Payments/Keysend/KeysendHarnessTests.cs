using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Keysend;

using Application.Payments.Send;
using Channels.Harness;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// Keysend between two NLightning nodes (lane lh1-l3 proof), in process with the production switch, payment service,
/// Sphinx, commitments and SQLite (<see cref="ThreeNodeHarness"/>): Alice keysends Bob and Bob keysends Alice, the
/// payee's switch accepts the HTLC with the onion's preimage, settles a <c>Keysend</c> record with the custom records,
/// and the payer's row keeps its custom records.
/// </summary>
public class KeysendHarnessTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Given_AliceAndBob_When_TheyKeysendEachOther_Then_BothSettleWithCustomRecords()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var aliceToBob = LightningMoney.MilliSatoshis(12_345_678);
        var bobToAlice = LightningMoney.MilliSatoshis(2_000_001);
        CustomRecord[] boost = [new(7629169, "{\"action\":\"boost\"}"u8), new(133773310, [0x01, 0x02])];
        var aliceBefore = harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).LocalBalance;

        // Act
        var first = await KeysendAsync(harness, harness.Alice, harness.Bob, aliceToBob, boost);
        var second = await KeysendAsync(harness, harness.Bob, harness.Alice, bobToAlice, []);

        // Assert: both payments succeeded with a preimage that hashes to their hash, no fee (direct)
        foreach (var result in new[] { first, second })
        {
            Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
            Assert.Equal(result.Payment.PaymentHash, new Hash(SHA256.HashData(result.Payment.Preimage!.Value)));
            Assert.Equal(LightningMoney.Zero, result.Payment.Fee);
            Assert.Null(result.Payment.Bolt11);
            Assert.Equal(1, result.Attempts);
        }

        // The payer's row (SQLite) keeps the custom records it sent
        var stored = await PaymentsOf(harness.Alice).GetPaymentAsync(first.Payment.PaymentHash,
                                                                     TestContext.Current.CancellationToken);
        Assert.NotNull(stored!.Keysend);
        Assert.Equal(boost.OrderBy(r => r.Type), stored.Keysend.CustomRecords);
        var storedSecond = await PaymentsOf(harness.Bob).GetPaymentAsync(second.Payment.PaymentHash,
                                                                         TestContext.Current.CancellationToken);
        Assert.Empty(storedSecond!.Keysend!.CustomRecords);

        // The payee settled a keysend record with the amount and the payer's records
        var bobRecord = await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(first.Payment.PaymentHash));
        Assert.Equal(InvoiceKind.Keysend, bobRecord!.Kind);
        Assert.Equal(InvoiceStatus.Settled, bobRecord.Status);
        Assert.Equal(aliceToBob, bobRecord.AmountReceived);
        Assert.Equal(first.Payment.Preimage, bobRecord.Preimage);
        Assert.Equal(boost.OrderBy(r => r.Type), bobRecord.Keysend!.CustomRecords);
        var aliceRecord = await harness.Alice.InScopeAsync(u => u.InvoiceDbRepository
                                                                 .GetByPaymentHashAsync(second.Payment.PaymentHash));
        Assert.Equal((InvoiceKind.Keysend, InvoiceStatus.Settled), (aliceRecord!.Kind, aliceRecord.Status));
        Assert.Empty(aliceRecord.Keysend!.CustomRecords);

        // Balances moved by the two amounts, no HTLC left
        Assert.Equal(aliceBefore - aliceToBob + bobToAlice,
                     harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).LocalBalance);
        Assert.Empty(harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.Htlcs);
    }

    [Fact]
    public async Task Given_BobRefusesKeysend_When_AliceKeysends_Then_FailedWithUnknownPaymentDetailsAndNoRecord()
    {
        // Arrange
        await using var harness = await CreateAsync(bobAccepts: false);

        // Act
        var result = await KeysendAsync(harness, harness.Alice, harness.Bob, LightningMoney.Satoshis(1_000), []);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        Assert.Null(await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository
                                                          .GetByPaymentHashAsync(result.Payment.PaymentHash)));
    }

    [Fact]
    public async Task Given_InvalidCustomRecord_When_Keysending_Then_ArgumentExceptionAndNothingStored()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var payments = PaymentsOf(harness.Alice);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => payments.PayKeysendAsync(new PayKeysendRequest(harness.Bob.NodeId, LightningMoney.Satoshis(1))
            {
                CustomRecords = [new CustomRecord(CustomRecordCodec.KeysendPreimageType, new byte[32])]
            }, new PayInvoiceOptions { Timeout = s_timeout }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => payments.PayKeysendAsync(new PayKeysendRequest(harness.Alice.NodeId, LightningMoney.Satoshis(1)),
                                           new PayInvoiceOptions { Timeout = s_timeout },
                                           TestContext.Current.CancellationToken));
        Assert.Empty(await payments.ListPaymentsAsync(0, 10, TestContext.Current.CancellationToken));
    }

    private static IPaymentService PaymentsOf(SwitchNode node) => node.Services.GetRequiredService<IPaymentService>();

    private static async Task<PayInvoiceResult> KeysendAsync(ThreeNodeHarness harness, SwitchNode from, SwitchNode to,
                                                             LightningMoney amount, IReadOnlyList<CustomRecord> records)
    {
        var paying = PaymentsOf(from).PayKeysendAsync(new PayKeysendRequest(to.NodeId, amount)
        {
            CustomRecords = records
        }, new PayInvoiceOptions { Timeout = s_timeout }, TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        return await paying;
    }

    private static Task<ThreeNodeHarness> CreateAsync(bool bobAccepts = true) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
            h.Bob.ConfigureServices = services => services.AddPaymentSendServices();
            h.Bob.Options.Keysend.Accept = bobAccepts;
        });
}