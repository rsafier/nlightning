using System.Collections.Concurrent;
using Grpc.Core;
using Lnrpc;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Messages;
using Fixtures;
using Utils;

/// <summary>
/// ONION M5 against LND 0.20 (lane rf1-m5, proof 1): LND david makes a BOLT 11 invoice with a blinded path whose
/// introduction node is our node (<c>AddInvoice</c> with <c>is_blinded</c>, one real hop, the incoming channel pinned to
/// our public channel to david), and LND alice, whose public channel to us carries the pushed balance, pays it. We read
/// the introduction node's <c>current_path_key</c> and <c>encrypted_recipient_data</c>, forward by the recipient's
/// short_channel_id with its <c>payment_relay</c>, and hand david the next path_key in <c>update_add_htlc</c>.
/// </summary>
/// <remarks>
/// <para>LND picks introduction nodes from its graph among nodes that announce <c>option_route_blinding</c> (bit 25):
/// our node advertises it Optional here (experimental until the M5 lane closes), so the test waits until david's graph
/// has our <c>node_announcement</c> with the bit and our channel with both policies.</para>
/// <para>Public channels change the LND graph for good, so this proof runs in the gossip collection: use
/// <c>scripts/run-gossip.sh 1 Release -class NLightning.Integration.Tests.Docker.Gossip.RouteBlindingFlowTests</c>.
/// </para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public class RouteBlindingFlowTests
{
    private const uint RouteBlindingOptionalBit = 25;
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);

    private readonly LightningRegtestNetworkFixture _fixture;

    public RouteBlindingFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    [Fact]
    public async Task Given_DavidsBlindedInvoiceThroughUs_When_AlicePays_Then_WeForwardAsTheIntroductionNode()
    {
        // Arrange: our node with route blinding advertised, a public channel to alice (pushed, so alice can pay over
        // it) and one to david
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        Console.WriteLine($"LND version: {await LndTestHelpers.GetVersionAsync(david, ct)}");
        await using var node = await NLightningTestNode.CreateAsync(_fixture, "blinded-intro", null, options =>
        {
            options.Features.AllowExperimentalFeatures = true;
            options.Features.OptionRouteBlinding = FeatureSupport.Optional;
        });
        node.ExtraConfiguration["Gossip:Enabled"] = "true";
        node.ExtraConfiguration["Gossip:AcceptPublicChannels"] = "true";
        node.ExtraConfiguration["Node:Alias"] = "nltg-blinded";
        node.ExtraConfiguration["Node:Color"] = GossipTestNodes.Color;
        await node.StartAsync(ct);

        var sentAdds = new ConcurrentQueue<UpdateAddHtlcMessage>();
        node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            if (args.ResponseMessage is UpdateAddHtlcMessage add)
                sentAdds.Enqueue(add);
        };

        var toAlice = await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, s_push, [alice], ct,
                                                                         syncGraph: false);
        await node.FundWalletAsync(LightningMoney.Satoshis(PublicTopology.Capacity.Satoshi * 2), AddressType.P2Wpkh,
                                   ct);
        var davidAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(
                         GossipTestNodes.MarkPublic(new OpenChannelClientRequest(davidAddress,
                                                                                 PublicTopology.Capacity)), ct);
        var davidScid = await PublicTopology.WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        Console.WriteLine($"Our public channel to david: {davidScid}, to alice: {toAlice.ShortChannelId}");
        await PublicTopology.WaitLndHasChannelAsync(_fixture, node, david, davidScid, ct);
        await Poll.UntilAsync(async () =>
        {
            var info = await GossipGraphProbe.TryGetNodeInfoAsync(david, node.NodeIdHex, ct);
            return info is not null && info.Features.ContainsKey(RouteBlindingOptionalBit);
        }, s_timeout, "david has our node_announcement with option_route_blinding", ct, GossipGraphProbe.PollInterval);

        // david's blinded invoice: one real hop (us) and our channel to him as the incoming channel
        var amountMsat = PublicTopology.UniqueAmountMsat(30_000_000);
        var invoice = await Poll.ForAsync(async () =>
        {
            try
            {
                var request = new Invoice
                {
                    ValueMsat = (long)amountMsat,
                    Memo = "blinded through nltg",
                    IsBlinded = true,
                    BlindedPathConfig = new BlindedPathConfig { MinNumRealHops = 1, NumHops = 1, MaxNumPaths = 1 }
                };
                request.BlindedPathConfig.IncomingChannelList.Add(davidScid);
                return await david.LightningClient.AddInvoiceAsync(request, cancellationToken: ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"david's blinded invoice: {e.Status.Detail}");
                return null;
            }
        }, s_timeout, "david's blinded invoice", ct, TimeSpan.FromSeconds(5));
        var decoded = await alice.LightningClient.DecodePayReqAsync(new PayReqString
        {
            PayReq = invoice.PaymentRequest
        }, cancellationToken: ct);
        var blindedPath = Assert.Single(decoded.BlindedPaths).BlindedPath;
        Console.WriteLine($"Blinded path: introduction {Convert.ToHexStringLower(blindedPath.IntroductionNode.Span)}, "
                        + $"{blindedPath.BlindedHops.Count} hops");
        Assert.Equal(node.NodeIdHex, Convert.ToHexStringLower(blindedPath.IntroductionNode.Span));

        // Act: alice pays over her channel to us (retried while her router lacks the fresh edge, NL-319)
        var payment = await Poll.ForAsync(async () =>
        {
            var result = await LndTestHelpers.SendPaymentV2Async(
                             alice, LndTestHelpers.PinnedPayment(invoice.PaymentRequest, [toAlice.ShortChannelId]),
                             ct);
            Console.WriteLine($"alice's payment: {result.Status} {result.FailureReason}");
            return result.Status == Payment.Types.PaymentStatus.Succeeded
                || result.FailureReason != PaymentFailureReason.FailureReasonNoRoute
                       ? result
                       : null;
        }, s_timeout, "alice's payment final (not no_route)", ct, TimeSpan.FromSeconds(5));

        // Assert: paid, david settled, and our add to david carried the next path_key
        foreach (var line in node.NodeLog.Where(l => l.Contains("blinded", StringComparison.OrdinalIgnoreCase)
                                                  || l.Contains("Forward", StringComparison.Ordinal)))
            Console.WriteLine(line);
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        var settled = await LndTestHelpers.WaitForInvoiceStateAsync(david, invoice.RHash.ToByteArray(),
                                                                     Invoice.Types.InvoiceState.Settled, s_timeout,
                                                                     ct);
        Assert.Equal((long)amountMsat, settled.AmtPaidMsat);
        var forwarded = Assert.Single(sentAdds, a => a.Payload.ChannelId == opened.ChannelId);
        Assert.NotNull(forwarded.BlindedPathTlv);
    }
}