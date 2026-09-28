using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Bolt11.Models;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.ValueObjects;
using Fixtures;
using Utils;

/// <summary>
/// NL-440 against LND 0.20: bLIP 39 BOLT 11 invoices with blinded paths (tagged field 20). (a) We decode LND's field 20
/// ourselves (no <c>DecodePayReq</c> in the payment: the paths come from our decoder and are checked against LND's
/// decode field by field) and pay david's blinded invoice with dummy hops through <c>payinvoice</c>; (b) LND bob pays
/// our own blinded invoice (<c>Node:Invoices:BlindedPaths</c>), whose path runs through alice as introduction node to
/// us and ends with our dummy hop.
/// </summary>
/// <remarks>
/// Public channels change the LND graph for good, so these proofs run in the gossip collection:
/// <c>scripts/run-gossip.sh 1 Release -class NLightning.Integration.Tests.Docker.Gossip.Bolt11BlindedPathFlowTests</c>.
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class Bolt11BlindedPathFlowTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);

    private readonly LightningRegtestNetworkFixture _fixture;

    public Bolt11BlindedPathFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact]
    public async Task Given_DavidsBlindedInvoiceWithDummyHops_When_WePayTheBolt11_Then_OurField20DecodeMatchesLnd()
    {
        // Arrange: a public channel of ours to david; david's blinded invoice has him as introduction node and pads
        // the path with dummy hops (min_num_real_hops 0, num_hops 2)
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(david, ct)}");
        await using var node = await CreateNodeAsync("bolt11-blinded-send", ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(PublicTopology.Capacity.Satoshi * 2), AddressType.P2Wpkh,
                                   ct);
        var davidAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(
                         GossipTestNodes.MarkPublic(new OpenChannelClientRequest(davidAddress,
                                                                                 PublicTopology.Capacity)), ct);
        await PublicTopology.WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        var amountMsat = PublicTopology.UniqueAmountMsat(21_000_000);
        var invoice = await AddBlindedInvoiceAsync(david, amountMsat, "bolt11 blinded to david", 0, 2, ct);
        Console.WriteLine($"david's invoice: {invoice.PaymentRequest}");

        // Our decode of field 20 against LND's own decode of the same string
        var ours = Invoice.Decode(invoice.PaymentRequest, BitcoinNetwork.Regtest);
        var lnd = await david.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = invoice.PaymentRequest },
                                                                cancellationToken: ct);
        Assert.Null(ours.PaymentSecret);
        Assert.Empty(ours.RouteHints);
        Assert.Equal(lnd.BlindedPaths.Count, ours.BlindedPaymentPaths.Count);
        for (var i = 0; i < lnd.BlindedPaths.Count; i++)
        {
            var theirs = lnd.BlindedPaths[i];
            var path = ours.BlindedPaymentPaths[i];
            Assert.Equal(Convert.ToHexStringLower(theirs.BlindedPath.IntroductionNode.Span),
                         path.Path.FirstNodeId.ToString());
            Assert.Equal(Convert.ToHexStringLower(theirs.BlindedPath.BlindingPoint.Span),
                         path.Path.FirstPathKey.ToString());
            Assert.Equal(theirs.BlindedPath.BlindedHops.Count, path.Path.Hops.Count);
            for (var h = 0; h < path.Path.Hops.Count; h++)
            {
                Assert.Equal(Convert.ToHexStringLower(theirs.BlindedPath.BlindedHops[h].BlindedNode.Span),
                             path.Path.Hops[h].BlindedNodeId.ToString());
                Assert.Equal(Convert.ToHexStringLower(theirs.BlindedPath.BlindedHops[h].EncryptedData.Span),
                             Convert.ToHexStringLower(path.Path.Hops[h].EncryptedRecipientData.Span));
            }

            Assert.Equal(theirs.BaseFeeMsat, path.PayInfo.FeeBaseMsat);
            Assert.Equal(theirs.ProportionalFeeRate, path.PayInfo.FeeProportionalMillionths);
            Assert.Equal(theirs.TotalCltvDelta, path.PayInfo.CltvExpiryDelta);
            Assert.Equal(theirs.HtlcMinMsat, path.PayInfo.HtlcMinimumMsat);
            Assert.Equal(theirs.HtlcMaxMsat, path.PayInfo.HtlcMaximumMsat);
            Console.WriteLine($"Path {i}: introduction {path.Path.FirstNodeId}, {path.Path.Hops.Count} hops, fee "
                            + $"{path.PayInfo.FeeBaseMsat} msat + {path.PayInfo.FeeProportionalMillionths} ppm, "
                            + $"delta {path.PayInfo.CltvExpiryDelta}");
        }

        var onlyPath = Assert.Single(ours.BlindedPaymentPaths);
        Assert.Equal(david.LocalNodePubKey.ToLowerInvariant(), onlyPath.Path.FirstNodeId.ToString());
        Assert.True(onlyPath.Path.Hops.Count >= 2, "david's path carries his dummy hops");

        // Act: payinvoice with the string alone (retried while our route to the introduction node is not ready)
        var payment = await Poll.ForAsync(async () =>
        {
            var result = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);
            Console.WriteLine($"payinvoice: {result.Status} {result.FailureReason}");
            return result.Status == PaymentStatus.Succeeded ? result : null;
        }, s_timeout, "our payment of david's blinded bolt11 invoice", ct, TimeSpan.FromSeconds(5));

        // Assert
        var settled = await LndTestHelpers.WaitForInvoiceStateAsync(david, invoice.RHash.ToByteArray(),
                                                                     Lnrpc.Invoice.Types.InvoiceState.Settled,
                                                                     s_timeout, ct);
        // david keeps what his own dummy hops charge: he is paid the amount plus at most the path's fee
        Assert.InRange(settled.AmtPaidMsat, (long)amountMsat,
                       (long)(amountMsat + onlyPath.PayInfo.ComputeFeeMsat(amountMsat)));
        Assert.Equal(Convert.ToHexStringLower(invoice.RHash.ToByteArray()), payment.PaymentHash.ToString());
    }

    [Fact]
    public async Task Given_OurBlindedBolt11Invoice_When_BobPaysItThroughAlice_Then_WeSettleAfterOurDummyHop()
    {
        // Arrange: our public channel to alice with a push (alice can send to us); our invoices carry bLIP 39 paths
        // (alice the introduction node, then us and one dummy hop), no route hint, no payment secret
        var ct = TestContext.Current.CancellationToken;
        var bob = _fixture.GetLndNode("bob");
        var alice = _fixture.GetLndNode("alice");
        await using var node = await CreateNodeAsync("bolt11-blinded-receive", ct,
                                                     n => n.ExtraConfiguration["Node:Invoices:BlindedPaths"] = "true");
        await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, s_push, PublicTopology.LndNodes(_fixture),
                                                           ct, syncGraph: false);
        var amount = LightningMoney.MilliSatoshis(PublicTopology.UniqueAmountMsat(11_000_000));
        var invoice = await Poll.ForAsync(async () =>
        {
            try
            {
                return await node.CreateInvoiceAsync(amount, "our blinded bolt11", ct);
            }
            catch (Exception e)
            {
                // No path until alice's channel_update reached us
                Console.WriteLine($"createinvoice: {e.Message}");
                return null;
            }
        }, s_timeout, "our blinded bolt11 invoice", ct, TimeSpan.FromSeconds(2));
        var decoded = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);
        Assert.Null(decoded.PaymentSecret);
        Assert.Empty(decoded.RouteHints);
        var path = Assert.Single(decoded.BlindedPaymentPaths);
        Assert.Equal(alice.LocalNodePubKey.ToLowerInvariant(), path.Path.FirstNodeId.ToString());
        Assert.Equal(3, path.Path.Hops.Count);
        Assert.NotEqual(node.NodeIdHex, Convert.ToHexStringLower(decoded.PayeePubKey!.ToBytes()));
        var lndDecode = await bob.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = invoice.Bolt11 },
                                                                    cancellationToken: ct);
        Assert.Single(lndDecode.BlindedPaths);
        Console.WriteLine($"LND decodes our invoice: {lndDecode.BlindedPaths.Count} path(s), introduction "
                        + $"{Convert.ToHexStringLower(lndDecode.BlindedPaths[0].BlindedPath.IntroductionNode.Span)}");

        // Act: bob pays the string with SendPaymentV2 (retried while its router lacks a route)
        var payment = await Poll.ForAsync(async () =>
        {
            try
            {
                var result = await LndTestHelpers.SendPaymentV2Async(
                                 bob, new SendPaymentRequest
                                 {
                                     PaymentRequest = invoice.Bolt11,
                                     FeeLimitMsat = 1_000_000,
                                     TimeoutSeconds = 60,
                                     NoInflightUpdates = true
                                 }, ct);
                Console.WriteLine($"bob's payment: {result.Status} {result.FailureReason}");
                return result.Status == Payment.Types.PaymentStatus.Succeeded
                    || result.FailureReason != PaymentFailureReason.FailureReasonNoRoute
                           ? result
                           : null;
            }
            catch (RpcException e)
            {
                Console.WriteLine($"bob's payment: {e.Status.Detail}");
                return null;
            }
        }, s_timeout, "bob's payment final (not no_route)", ct, TimeSpan.FromSeconds(5));

        // Assert
        foreach (var line in node.NodeLog.Where(l => l.Contains("blinded", StringComparison.OrdinalIgnoreCase)
                                                  || l.Contains("dummy", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(line);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        var stored = await Poll.ForAsync(async () => await node.GetInvoiceAsync(invoice.PaymentHash, ct) is
        { Status: InvoiceStatus.Settled } settled
                                             ? settled
                                             : null, s_timeout, "our invoice settled", ct, TimeSpan.FromSeconds(1));
        Assert.Equal(amount, stored.Amount);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct,
                                                           Action<NLightningTestNode>? configure = null)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, null, options =>
        {
            options.Features.OptionRouteBlinding = FeatureSupport.Optional;
        });
        node.ExtraConfiguration["Gossip:Enabled"] = "true";
        node.ExtraConfiguration["Gossip:AcceptPublicChannels"] = "true";
        node.ExtraConfiguration["Node:Alias"] = "nltg-" + name;
        node.ExtraConfiguration["Node:Color"] = GossipTestNodes.Color;
        configure?.Invoke(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// An LND blinded invoice: <paramref name="minRealHops"/> real hops before the recipient (0: the recipient is the
    /// introduction node), padded with dummy hops to <paramref name="numHops"/>.
    /// </summary>
    private static async Task<AddInvoiceResponse> AddBlindedInvoiceAsync(LNDNodeConnection lnd, ulong amountMsat,
                                                                         string memo, uint minRealHops, uint numHops,
                                                                         CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var request = new Lnrpc.Invoice
                {
                    ValueMsat = (long)amountMsat,
                    Memo = memo,
                    IsBlinded = true,
                    BlindedPathConfig = new BlindedPathConfig
                    {
                        MinNumRealHops = minRealHops,
                        NumHops = numHops,
                        MaxNumPaths = 1
                    }
                };
                return await lnd.LightningClient.AddInvoiceAsync(request, cancellationToken: ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"{lnd.LocalAlias}'s blinded invoice: {e.Status.Detail}");
                return null;
            }
        }, s_timeout, $"{lnd.LocalAlias}'s blinded invoice", ct, TimeSpan.FromSeconds(5));
    }
}