using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Switch;

using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;

/// <summary>
/// ONION M5 in the production switch: Alice pays Carol through a blinded path Carol made over Bob (Bob the introduction
/// node, Carol the blinded recipient), on three in-process nodes with real Sphinx and blinding crypto, real commitment
/// signatures and SQLite persistence (<see cref="ThreeNodeHarness"/>).
/// </summary>
public class BlindedThreeNodeSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly BlindedPaymentRelay s_bobRelay = new(40, 100, 1_000);
    private const uint FinalCltv = ThreeNodeHarness.BlockHeight + 43;

    [Fact]
    public async Task Given_BlindedPathThroughBob_When_AlicePays_Then_BobForwardsWithTheNextPathKeyAndCarolSettles()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "blinded", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice);

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert: Bob's add carries the next path_key; Carol settled the invoice from her blinded final hop
        var forwarded = Assert.IsType<UpdateAddHtlcMessage>(
            Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.Equal(payment.NextPathKey, forwarded.BlindedPathTlv!.PathKey);
        Assert.True(s_bobRelay.TryComputeAmountToForward(payment.FirstHopAmount.MilliSatoshi, out var expected));
        Assert.Equal(expected, forwarded.Payload.Amount.MilliSatoshi);
        Assert.Equal(FinalCltv, forwarded.Payload.CltvExpiry);
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage!.Value, fulfilled.PaymentPreimage);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_BlindedPathNamingCarolByNodeId_When_AlicePays_Then_BobFindsTheChannelAndCarolSettles()
    {
        // Arrange: next_node_id instead of short_channel_id in Bob's data
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "by node id", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice, byNodeId: true);

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_WrongPathId_When_CarolReceives_Then_MalformedToBobAndBobFailsAliceWithInvalidOnionBlinding()
    {
        // Arrange: a path whose path_id is not the invoice's (a probe of whether Carol is the recipient)
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "probe", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice, pathId: new byte[32]);

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert: Carol (path_key in update_add_htlc) fails malformed with invalid_onion_blinding
        var malformed = Assert.IsType<UpdateFailMalformedHtlcMessage>(
            Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                          && s.Message is UpdateFailMalformedHtlcMessage).Message);
        Assert.Equal((ushort)FailureCode.InvalidOnionBlinding, malformed.Payload.FailureCode);

        // Bob, the introduction node, sends his own invalid_onion_blinding, never Carol's error
        var decrypted = Decrypt(harness, payment, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Equal(SHA256.HashData(payment.Packet.ToBytes()), decrypted.Message!.Sha256OfOnion!.Value.ToArray());
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, stored!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ExpiryAboveBobsMaxCltvExpiry_When_BobProcesses_Then_AliceGetsInvalidOnionBlindingFromBob()
    {
        // Arrange: Bob's payment_constraints end below the HTLC's expiry
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "expired path", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice, bobMaxCltvExpiry: FinalCltv);

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert: nothing reached Carol
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        var decrypted = Decrypt(harness, payment, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_RelayFeeBelowBobsPolicy_When_BobForwards_Then_InvalidOnionBlindingInsteadOfFeeInsufficient()
    {
        // Arrange: Carol's path charges less than Bob's advertised fee
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "cheap path", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice, relay: s_bobRelay with { FeeBaseMsat = 999 });

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert: the introduction node hides which check failed
        var decrypted = Decrypt(harness, payment, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
    }

    [Fact]
    public async Task Given_RouteBlindingNotAdvertised_When_AlicePaysThroughBob_Then_BobRefusesWithInvalidOnionBlinding()
    {
        // Arrange
        await using var harness = await CreateAsync(enableBlinding: false);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "off", null,
                                                                      TestContext.Current.CancellationToken);
        var payment = BuildPayment(harness, invoice);

        // Act
        await OfferAsync(harness, payment);
        await harness.PumpAsync();

        // Assert
        var decrypted = Decrypt(harness, payment, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
    }

    private static async Task<ThreeNodeHarness> CreateAsync(bool enableBlinding = true) =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features.OptionRouteBlinding = enableBlinding ? FeatureSupport.Optional : FeatureSupport.No;
        });

    private sealed record BlindedPayment(OnionPacket Packet, IReadOnlyList<Secret> SharedSecrets,
                                         LightningMoney FirstHopAmount, uint FirstHopCltvExpiry, Hash PaymentHash,
                                         CompactPubKey NextPathKey);

    /// <summary>
    /// Carol's blinded path Bob → Carol and Alice's onion to it: an unblinded hop to Bob (the introduction node) with
    /// the path's first path_key as <c>current_path_key</c>, then Carol's blinded hop.
    /// </summary>
    private static BlindedPayment BuildPayment(ThreeNodeHarness harness, InvoiceModel invoice,
                                               byte[]? pathId = null, uint? bobMaxCltvExpiry = null,
                                               BlindedPaymentRelay? relay = null, bool byNodeId = false)
    {
        relay ??= s_bobRelay;
        var blinding = harness.Carol.Services.GetRequiredService<IRouteBlindingService>();
        var bobData = new BlindedRecipientData
        {
            ShortChannelId = byNodeId ? null : (Domain.Channels.ValueObjects.ShortChannelId?)ThreeNodeHarness.BobCarolScid,
            NextNodeId = byNodeId ? (CompactPubKey?)harness.Carol.NodeId : null,
            PaymentRelay = relay,
            PaymentConstraints = new BlindedPaymentConstraints(bobMaxCltvExpiry ?? FinalCltv + 1_000, 1)
        };
        var carolData = new BlindedRecipientData
        {
            PathId = pathId ?? BlindedPathId.Compute(invoice.Preimage!.Value),
            PaymentConstraints = new BlindedPaymentConstraints(FinalCltv + 1_000, 1)
        };
        var path = blinding.CreateBlindedPath([harness.Bob.NodeId, harness.Carol.NodeId],
                                              [blinding.EncodeRecipientData(bobData),
                                               blinding.EncodeRecipientData(carolData)],
                                              new PrivKey(RandomNumberGenerator.GetBytes(32)));

        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var bobPayload = Serialize(serializer, new HopPayload(new EncryptedRecipientDataTlv(path.Hops[0]
                                                                  .EncryptedRecipientData.Span),
                                                              new CurrentPathKeyTlv(path.FirstPathKey)));
        var carolPayload = Serialize(serializer, new HopPayload(new AmtToForwardTlv(s_amount),
                                                                new OutgoingCltvValueTlv(FinalCltv),
                                                                new EncryptedRecipientDataTlv(path.Hops[1]
                                                                    .EncryptedRecipientData.Span),
                                                                new TotalAmountMsatTlv(s_amount)));
        var sphinx = harness.Alice.Services.GetRequiredService<ISphinxService>();
        var constructed = sphinx.ConstructWithSharedSecrets([new OnionHop(harness.Bob.NodeId, bobPayload),
                                                             new OnionHop(path.Hops[1].BlindedNodeId, carolPayload)],
                                                            new PrivKey(RandomNumberGenerator.GetBytes(32)),
                                                            invoice.PaymentHash);

        // Bob gets amount + his relay fee (rounded up) and the final expiry + his relay delta
        var fee = relay.FeeBaseMsat + (s_amount.MilliSatoshi * relay.FeeProportionalMillionths + 999_999) / 1_000_000;
        // The path_key Bob derives for Carol
        var nextPathKey = harness.Bob.Services.GetRequiredService<IRouteBlindingService>()
                                 .UnblindAsLocalNode(path.FirstPathKey, path.Hops[0].EncryptedRecipientData)
                                 .NextPathKey;
        return new BlindedPayment(constructed.Packet, constructed.SharedSecrets,
                                  s_amount + LightningMoney.MilliSatoshis(fee), FinalCltv + relay.CltvExpiryDelta,
                                  invoice.PaymentHash, nextPathKey);
    }

    private static byte[] Serialize(IHopPayloadSerializer serializer, HopPayload payload)
    {
        using var stream = new MemoryStream();
        serializer.SerializeAsync(payload, stream).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static async Task OfferAsync(ThreeNodeHarness harness, BlindedPayment payment)
    {
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, payment.FirstHopAmount,
                                                      payment.PaymentHash, payment.FirstHopCltvExpiry,
                                                      payment.Packet, null, HtlcOrigin.Local(payment.PaymentHash));
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, BlindedPayment payment,
                                            OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        var decrypted = service.DecryptErrorPacket(payment.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}