using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments;

using Application.Payments.FinalHop;
using Application.Payments.Onion;
using Application.Payments.Routing;
using Bolt11.Models;
using Domain.Channels.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.ValueObjects;

/// <summary>
/// ABCD W1-B proof, in process with real crypto: Alice builds a 3-hop onion (Bob → Carol → David, the ABCD hint
/// route) with <see cref="HintRouteBuilder"/> + <see cref="PaymentOnionFactory"/>; Bob, Carol and David each peel their
/// layer with their own <see cref="IncomingOnionProcessor"/> (real Sphinx, node-key ECDH); Bob and Carol run their
/// forwarding policy; David's <see cref="FinalHopProcessor"/> accepts against the invoice his
/// <c>InvoiceService</c> issued, or fails and the error onion travels back to Alice.
/// </summary>
public class ThreeHopPaymentTests : IDisposable
{
    private const uint Height = 500;
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly ShortChannelId s_scidBobCarol = new(400, 1, 0);
    private static readonly ShortChannelId s_scidCarolDavid = new(401, 2, 1);

    private readonly PaymentsTestNode _alice = new("alice", 0x0a);

    private readonly PaymentsTestNode _bob = new("bob", 0x0b, new RoutingOptions
    {
        FeeBaseMsat = 1_000,
        FeeProportionalMillionths = 100,
        CltvExpiryDelta = 40
    });

    private readonly PaymentsTestNode _carol = new("carol", 0x0c, new RoutingOptions
    {
        FeeBaseMsat = 2_000,
        FeeProportionalMillionths = 500,
        CltvExpiryDelta = 40
    });

    private readonly PaymentsTestNode _david = new("david", 0x0d);

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _carol.Dispose();
        _david.Dispose();
    }

    [Fact]
    public async Task Given_AbcdHintRoute_When_EachHopPeels_Then_ForwardsMatchBolt7FeesAndDavidAcceptsWithPreimage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _david.InvoiceService.CreateInvoiceAsync(s_amount, "abcd", null, ct);
        var onion = await BuildAliceOnionAsync(invoice);
        var route = onion.Route;

        var feeCarol = 2_000 + 50_000_123UL * 500 / 1_000_000;
        var amountBobCarol = 50_000_123UL + feeCarol;
        var feeBob = 1_000 + amountBobCarol * 100 / 1_000_000;

        // Act: Bob
        var atBob = await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash);
        var bobForward = Assert.IsType<IncomingOnionForward>(atBob);
        var bobDecision = _bob.ForwardingPolicy.Evaluate(ForwardRequest(route.FirstHopAmount, route.FirstHopCltvExpiry,
                                                                         bobForward));

        // Act: Carol
        var atCarol = await _carol.OnionProcessor.ProcessAsync(bobForward.NextPacket, invoice.PaymentHash);
        var carolForward = Assert.IsType<IncomingOnionForward>(atCarol);
        var carolDecision = _carol.ForwardingPolicy.Evaluate(ForwardRequest(bobForward.AmountToForward,
                                                                             bobForward.OutgoingCltvValue,
                                                                             carolForward));

        // Act: David
        var atDavid = await _david.OnionProcessor.ProcessAsync(carolForward.NextPacket, invoice.PaymentHash);
        var davidFinal = Assert.IsType<IncomingOnionFinal>(atDavid);
        var davidResult = await _david.FinalHopProcessor.ProcessAsync(
            _david.Invoices, invoice.PaymentHash, carolForward.AmountToForward, carolForward.OutgoingCltvValue,
            davidFinal.Payload, Height);

        // Assert: the route (BOLT 7 fee formula, BOLT 4 CLTVs, final = height + c + 3)
        Assert.Equal(3, route.Hops.Count);
        Assert.Equal(_bob.NodeId, route.FirstHopNodeId);
        Assert.Equal(amountBobCarol + feeBob, route.FirstHopAmount.MilliSatoshi);
        Assert.Equal(feeBob + feeCarol, route.Fee.MilliSatoshi);
        Assert.Equal(Height + 40 + 3 + 40 + 40, route.FirstHopCltvExpiry);

        // Assert: Bob forwards amt_BC over B-C, and his policy agrees
        Assert.Equal(s_scidBobCarol, bobForward.OutgoingShortChannelId);
        Assert.Equal(amountBobCarol, bobForward.AmountToForward.MilliSatoshi);
        Assert.Equal(Height + 40 + 3 + 40, bobForward.OutgoingCltvValue);
        Assert.True(bobDecision.IsForward);

        // Assert: Carol forwards X over C-D, and her policy agrees
        Assert.Equal(s_scidCarolDavid, carolForward.OutgoingShortChannelId);
        Assert.Equal(s_amount, carolForward.AmountToForward);
        Assert.Equal(Height + 40 + 3, carolForward.OutgoingCltvValue);
        Assert.True(carolDecision.IsForward);

        // Assert: David is the final hop, the invoice is paid, and the shared secrets match Alice's
        Assert.True(davidResult.IsAccepted, davidResult.Reason);
        Assert.Equal(s_amount, davidResult.AmountReceived);
        Assert.Equal(invoice.PaymentHash, (Domain.Crypto.ValueObjects.Hash)SHA256.HashData(davidResult.Preimage!.Value));
        Assert.Equal(onion.SharedSecrets[0], bobForward.SharedSecret);
        Assert.Equal(onion.SharedSecrets[1], carolForward.SharedSecret);
        Assert.Equal(onion.SharedSecrets[2], davidFinal.SharedSecret);
    }

    [Fact]
    public async Task Given_CanceledInvoice_When_DavidFails_Then_AliceDecrypts0x400FFromHop2WithAmountAndHeight()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _david.InvoiceService.CreateInvoiceAsync(s_amount, "abcd variant a", null, ct);
        Assert.True(await _david.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct));
        var onion = await BuildAliceOnionAsync(invoice);

        var bobForward = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash));
        var carolForward = Assert.IsType<IncomingOnionForward>(
            await _carol.OnionProcessor.ProcessAsync(bobForward.NextPacket, invoice.PaymentHash));
        var davidFinal = Assert.IsType<IncomingOnionFinal>(
            await _david.OnionProcessor.ProcessAsync(carolForward.NextPacket, invoice.PaymentHash));

        // Act: David fails, Carol and Bob wrap, Alice decrypts
        var davidResult = await _david.FinalHopProcessor.ProcessAsync(
            _david.Invoices, invoice.PaymentHash, carolForward.AmountToForward, carolForward.OutgoingCltvValue,
            davidFinal.Payload, Height);
        var errorPacket = _david.FailureOnion.CreateErrorPacket(davidFinal.SharedSecret, davidResult.Failure!);
        errorPacket = _carol.FailureOnion.WrapErrorPacket(carolForward.SharedSecret, errorPacket);
        errorPacket = _bob.FailureOnion.WrapErrorPacket(bobForward.SharedSecret, errorPacket);
        var decrypted = _alice.FailureOnion.DecryptErrorPacket(onion.SharedSecrets, errorPacket);

        // Assert
        Assert.False(davidResult.IsAccepted);
        Assert.NotNull(decrypted);
        Assert.Equal(2, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Equal(0x400F, (ushort)decrypted.Code!);
        Assert.Equal(s_amount, decrypted.Message!.HtlcAmount);
        Assert.Equal(Height, decrypted.Message.Height);
    }

    [Fact]
    public async Task Given_HintUnderpricesCarol_When_CarolEvaluates_Then_FeeInsufficientWithIncomingAmount()
    {
        // Arrange: Carol now charges 3000 msat base, the hint still says 2000
        var ct = TestContext.Current.CancellationToken;
        _carol.Options.Routing.FeeBaseMsat = 3_000;
        var invoice = await _david.InvoiceService.CreateInvoiceAsync(s_amount, "abcd variant a2", null, ct);
        var onion = await BuildAliceOnionAsync(invoice);
        var bobForward = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash));
        var carolForward = Assert.IsType<IncomingOnionForward>(
            await _carol.OnionProcessor.ProcessAsync(bobForward.NextPacket, invoice.PaymentHash));

        // Act
        var decision = _carol.ForwardingPolicy.Evaluate(ForwardRequest(bobForward.AmountToForward,
                                                                        bobForward.OutgoingCltvValue,
                                                                        carolForward));

        // Assert
        Assert.Equal(FailureCode.FeeInsufficient, decision.FailureCode);
        Assert.Equal(bobForward.AmountToForward.MilliSatoshi, decision.HtlcMsat);
    }

    [Fact]
    public async Task Given_OnionAlreadyProcessed_When_BobSeesItAgain_Then_ReplayFailsWithTemporaryNodeFailure()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _david.InvoiceService.CreateInvoiceAsync(s_amount, "replay", null, ct);
        var onion = await BuildAliceOnionAsync(invoice);
        var first = await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash);

        // Act
        var replay = await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash);
        var reprocessed = await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash,
                                                                 checkReplay: false);

        // Assert
        Assert.IsType<IncomingOnionForward>(first);
        var failed = Assert.IsType<IncomingOnionFailed>(replay);
        Assert.Equal(FailureCode.TemporaryNodeFailure, failed.Failure.Code);
        Assert.Equal(onion.SharedSecrets[0], failed.SharedSecret);
        Assert.IsType<IncomingOnionForward>(reprocessed);
    }

    [Fact]
    public async Task Given_OnionForCarol_When_BobPeelsIt_Then_MalformedInvalidOnionHmacWithSha256OfOnion()
    {
        // Arrange: Bob receives the packet meant for Carol (encrypted to Carol's key)
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _david.InvoiceService.CreateInvoiceAsync(s_amount, "wrong key", null, ct);
        var onion = await BuildAliceOnionAsync(invoice);
        var bobForward = Assert.IsType<IncomingOnionForward>(
            await _bob.OnionProcessor.ProcessAsync(onion.Packet, invoice.PaymentHash));
        var carolPacket = bobForward.NextPacket.ToBytes();

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(carolPacket, invoice.PaymentHash);

        // Assert
        var malformed = Assert.IsType<IncomingOnionMalformed>(result);
        Assert.Equal(FailureCode.InvalidOnionHmac, malformed.FailureCode);
        Assert.Equal(SHA256.HashData(carolPacket), malformed.Sha256OfOnion.ToArray());
        Assert.Null(malformed.SharedSecretOrNull);
    }

    private async Task<PaymentOnion> BuildAliceOnionAsync(InvoiceModel invoice)
    {
        // Alice decodes David's BOLT 11 and adds the ABCD hints (roadmap §3: [{Bob, B-C}, {Carol, C-D}])
        var decoded = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);
        var target = PaymentTarget.FromInvoice(decoded) with
        {
            RouteHints =
            [
                [
                    new RoutingInfo(_bob.NodeId, s_scidBobCarol, 1_000, 100, 40),
                    new RoutingInfo(_carol.NodeId, s_scidCarolDavid, 2_000, 500, 40)
                ]
            ]
        };

        var route = _alice.RouteBuilder.Build(target, s_amount, Height, _alice.NodeId,
                                              peer => peer == _bob.NodeId);
        return await _alice.OnionFactory.CreateAsync(route);
    }

    private static ForwardingRequest ForwardRequest(LightningMoney incomingAmount, uint incomingCltvExpiry,
                                                    IncomingOnionForward forward) =>
        new(incomingAmount, incomingCltvExpiry, forward.AmountToForward, forward.OutgoingCltvValue, Height,
            new OutgoingChannelInfo(ChannelId.Zero, true, LightningMoney.MilliSatoshis(1_000),
                                    LightningMoney.Satoshis(1_000_000)));
}