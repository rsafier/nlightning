using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Keysend;

using Application.Payments.Keysend;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
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

        // NL-602: each side recorded its payment and its receipt once, with the keysend records' types
        var aliceEvents = await AccountingEventsAsync(harness.Alice);
        var sent = Assert.Single(aliceEvents, e => e.Kind == AccountingEventKind.PaymentSucceeded);
        Assert.Equal(AccountingEventKeys.PaymentSucceeded(first.Payment.PaymentHash), sent.EventKey);
        Assert.Equal(-(long)aliceToBob.MilliSatoshi, sent.AmountMsat);
        Assert.Equal(0, sent.FeeMsat);
        Assert.Equal(harness.Bob.NodeId, sent.Counterparty);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, sent.ChannelId);
        Assert.Equal(first.Payment.PaymentHash, sent.PaymentHash);
        Assert.Equal(AccountingFinality.Final, sent.Finality);
        Assert.Equal("keysend", sent.Details["kind"]);
        Assert.Equal("1", sent.Details["parts"]);
        Assert.Equal("7629169,133773310", sent.Details["customRecords"]);
        Assert.False(sent.Details.ContainsKey("selfPayment"));
        Assert.Equal(stored.CompletedAt, sent.OccurredAt);
        var aliceReceived = Assert.Single(aliceEvents, e => e.Kind == AccountingEventKind.InvoiceSettled);
        Assert.Equal((long)bobToAlice.MilliSatoshi, aliceReceived.AmountMsat);
        Assert.Equal(harness.Bob.NodeId, aliceReceived.Counterparty);
        Assert.Equal(2, aliceEvents.Count);

        var bobEvents = await AccountingEventsAsync(harness.Bob);
        Assert.Equal(2, bobEvents.Count);
        var received = Assert.Single(bobEvents, e => e.Kind == AccountingEventKind.InvoiceSettled);
        Assert.Equal(AccountingEventKeys.InvoiceSettled(first.Payment.PaymentHash), received.EventKey);
        Assert.Equal((long)aliceToBob.MilliSatoshi, received.AmountMsat);
        Assert.Equal(harness.Alice.NodeId, received.Counterparty);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, received.ChannelId);
        Assert.Equal("keysend", received.Details["kind"]);
        Assert.Equal("7629169,133773310", received.Details["customRecords"]);
        var bobSent = Assert.Single(bobEvents, e => e.Kind == AccountingEventKind.PaymentSucceeded);
        Assert.Equal(-(long)bobToAlice.MilliSatoshi, bobSent.AmountMsat);
        Assert.False(bobSent.Details.ContainsKey("customRecords"));
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

        // NL-602: the final failure is recorded once at Alice (no money moved), nothing at Bob
        var failed = Assert.Single(await AccountingEventsAsync(harness.Alice));
        Assert.Equal(AccountingEventKind.PaymentFailed, failed.Kind);
        Assert.Equal(AccountingEventKeys.PaymentFailed(result.Payment.PaymentHash, result.Payment.CreatedAt.UtcTicks),
                     failed.EventKey);
        Assert.Equal(0, failed.AmountMsat);
        Assert.Equal(0, failed.FeeMsat);
        Assert.Equal(harness.Bob.NodeId, failed.Counterparty);
        Assert.Equal(result.Payment.FailureReason, failed.Details["reason"]);
        Assert.Equal(nameof(FailureCode.IncorrectOrUnknownPaymentDetails), failed.Details["failureCode"]);
        Assert.Equal("keysend", failed.Details["kind"]);
        Assert.Empty(await AccountingEventsAsync(harness.Bob));
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

    [Fact]
    public async Task Given_AKeysendThroughBob_When_CarolReceivesIt_Then_BobForwardsAndOnlyCarolSeesTheRecords()
    {
        // Arrange (lane lh1-l3 review): Alice -> Bob -> Carol with an even custom record; Bob's layer carries a
        // short_channel_id and nothing of the keysend, Carol's the preimage and the records
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var amount = LightningMoney.MilliSatoshis(3_000_000);
        var keysend = CreateKeysend(0x61, [new CustomRecord(133773310, [0x0a, 0x0b]), new CustomRecord(7629169, "hi"u8)]);
        var route = harness.RouteToCarol(amount, HashOf(keysend), new Secret(new byte[32]));

        // Act
        await OfferAsync(harness, route, keysend);
        await harness.PumpAsync();

        // Assert: Alice learnt her own preimage back, Bob forwarded (a circuit, no record), Carol settled a record
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(keysend.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Null(await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(route.PaymentHash)));
        var circuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository
                                                            .GetByIncomingAsync(ThreeNodeHarness.AliceBobChannelId, 0));
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit!.Status);
        var carolRecord = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                                 .GetByPaymentHashAsync(route.PaymentHash));
        Assert.Equal((InvoiceKind.Keysend, InvoiceStatus.Settled), (carolRecord!.Kind, carolRecord.Status));
        Assert.Equal(amount, carolRecord.AmountReceived);
        Assert.Equal(keysend.CustomRecords, carolRecord.Keysend!.CustomRecords);
    }

    [Fact]
    public async Task Given_CarolStopsAfterSavingTheKeysendRecord_When_Restarted_Then_TheReplayedHtlcSettlesItOnce()
    {
        // Arrange: the HTLC is locked in at Carol and her switch stops right after SaveKeysendRecordAsync (the Open
        // record saved, the set not fulfilled yet)
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var amount = LightningMoney.MilliSatoshis(2_500_000);
        var keysend = CreateKeysend(0x62, [new CustomRecord(65537, [0x01])]);
        var route = harness.RouteToCarol(amount, HashOf(keysend), new Secret(new byte[32]));
        harness.Carol.SwitchSuspended = true;
        await OfferAsync(harness, route, keysend);
        await harness.PumpAsync();
        var finalPayload = Application.Payments.Routing.PaymentOnionFactory.CreatePayload(route.Hops[^1], route,
                                                                                            keysend);
        var record = new KeysendReceiver(new KeysendOptions()).TryCreateInvoice(route.PaymentHash, finalPayload,
                                                                                out var refusal);
        Assert.Null(refusal);
        await harness.Carol.InScopeAsync(async u =>
        {
            await u.InvoiceDbRepository.AddAsync(record!);
            await u.SaveChangesAsync();
            return 0;
        });

        // Act
        harness.Carol.SwitchSuspended = false;
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert: fulfilled once, one record, Settled with the amount
        Assert.Equal(keysend.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var records = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository.ListAsync(0, 10));
        var stored = Assert.Single(records);
        Assert.Equal((InvoiceKind.Keysend, InvoiceStatus.Settled), (stored.Kind, stored.Status));
        Assert.Equal(amount, stored.AmountReceived);
        var settled = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal(AccountingEventKind.InvoiceSettled, settled.Kind);
        Assert.Equal((long)amount.MilliSatoshi, settled.AmountMsat);
        Assert.Equal("65537", settled.Details["customRecords"]);
    }

    [Fact]
    public async Task Given_CustomRecordsLargerThanTheOnion_When_Keysending_Then_RefusedUpFrontAndNothingStored()
    {
        // Arrange: 1,300 bytes of records cannot fit even a direct payee's layer
        await using var harness = await CreateAsync();
        var payments = PaymentsOf(harness.Alice);

        // Act
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => payments.PayKeysendAsync(new PayKeysendRequest(harness.Bob.NodeId, LightningMoney.Satoshis(1))
            {
                CustomRecords = [new CustomRecord(65537, new byte[1_300])]
            }, new PayInvoiceOptions { Timeout = s_timeout }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("do not fit the onion", error.Message);
        Assert.Empty(await payments.ListPaymentsAsync(0, 10, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.Htlcs);
    }

    private static KeysendFinalRecords CreateKeysend(byte seed, IReadOnlyList<CustomRecord> records) =>
        new(new Secret(Enumerable.Repeat(seed, 32).ToArray()), CustomRecordCodec.Validate(records));

    private static Hash HashOf(KeysendFinalRecords keysend) => new(SHA256.HashData(keysend.Preimage));

    private static async Task OfferAsync(ThreeNodeHarness harness, Application.Payments.Routing.PaymentRoute route,
                                         KeysendFinalRecords keysend)
    {
        var onion = await harness.Alice.Services.GetRequiredService<Application.Payments.Routing.PaymentOnionFactory>()
                                 .CreateAsync(route, keysend);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet, null,
                                                      HtlcOrigin.Local(route.PaymentHash));
    }

    private static IPaymentService PaymentsOf(SwitchNode node) => node.Services.GetRequiredService<IPaymentService>();

    /// <summary>The accounting events <paramref name="node"/> saved (NL-602; none is sealed in these tests).</summary>
    private static Task<IReadOnlyList<AccountingEventModel>> AccountingEventsAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000));

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