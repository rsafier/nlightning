using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Application.Payments.Invoices;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Fixtures;
using Utils;
using BlindedPath = Domain.Protocol.Onion.Models.BlindedPath;
using BlindedPaymentPath = Domain.Protocol.Onion.Models.BlindedPaymentPath;

/// <summary>
/// ONION M5 against LND 0.21 (lane rf1-m5, proof 1): LND david makes a BOLT 11 invoice with a blinded path whose
/// introduction node is our node (<c>AddInvoice</c> with <c>is_blinded</c>, one real hop, the incoming channel pinned to
/// our public channel to david), and LND alice, whose public channel to us carries the pushed balance, pays it. We read
/// the introduction node's <c>current_path_key</c> and <c>encrypted_recipient_data</c>, forward by the recipient's
/// short_channel_id with its <c>payment_relay</c>, and hand david the next path_key in <c>update_add_htlc</c>.
/// </summary>
/// <remarks>
/// <para>LND picks introduction nodes from its graph among nodes that announce <c>option_route_blinding</c> (bit 25):
/// our node advertises it Optional here (the default since the M5 lane; set explicitly here), so the test waits until david's graph
/// has our <c>node_announcement</c> with the bit and our channel with both policies.</para>
/// <para>Public channels change the LND graph for good, so this proof runs in the gossip collection: use
/// <c>scripts/run-cluster.sh -n 1 --suite gossip --class
/// NLightning.Integration.Tests.Docker.Gossip.RouteBlindingFlowTests</c>.
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

    [Fact]
    public async Task Given_DavidsBlindedInvoiceWithHimAsIntroduction_When_WePay_Then_DavidSettles()
    {
        // Arrange: a public channel of ours to david; david's blinded path has no real hop before him (he is the
        // introduction node and the recipient)
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        await using var node = await CreateNodeAsync("blinded-send-direct", ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(PublicTopology.Capacity.Satoshi * 2), AddressType.P2Wpkh,
                                   ct);
        var davidAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(
                         GossipTestNodes.MarkPublic(new OpenChannelClientRequest(davidAddress,
                                                                                 PublicTopology.Capacity)), ct);
        await PublicTopology.WaitForShortChannelIdAsync(node, opened.ChannelId, ct);
        var amountMsat = PublicTopology.UniqueAmountMsat(20_000_000);
        var invoice = await AddBlindedInvoiceAsync(david, amountMsat, "blinded to david", 0, 0, null, ct);
        var paths = await DecodeBlindedPathsAsync(david, invoice.PaymentRequest, ct);
        var path = Assert.Single(paths);
        Assert.Equal(david.LocalNodePubKey.ToLowerInvariant(), path.Path.FirstNodeId.ToString());

        // Act
        var result = await PayBlindedAsync(node, invoice, amountMsat, paths, ct);

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        var settled = await LndTestHelpers.WaitForInvoiceStateAsync(david, invoice.RHash.ToByteArray(),
                                                                     Invoice.Types.InvoiceState.Settled, s_timeout,
                                                                     ct);
        Assert.Equal((long)amountMsat, settled.AmtPaidMsat);
        Assert.Equal(Convert.ToHexStringLower(invoice.RHash.ToByteArray()),
                     Convert.ToHexStringLower(SHA256.HashData((byte[])result.Payment.Preimage!.Value)));
    }

    [Fact]
    public async Task Given_CarolsBlindedInvoiceThroughBob_When_WePay_Then_WeRouteToBobAndCarolSettles()
    {
        // Arrange: our public channel to alice (the graph synced), carol's blinded path with bob as the introduction
        // node (one real hop, her channel from bob as the incoming channel)
        var ct = TestContext.Current.CancellationToken;
        var bob = _fixture.GetLndNode("bob");
        var carol = _fixture.GetLndNode("carol");
        await using var node = await CreateNodeAsync("blinded-send-graph", ct);
        await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, null, PublicTopology.LndNodes(_fixture), ct);
        var fixtureChannels = await PublicTopology.GetFixtureChannelsAsync(_fixture, ct);
        var bobCarol = PublicTopology.FixtureChannelBetween(fixtureChannels, bob, carol);
        var amountMsat = PublicTopology.UniqueAmountMsat(15_000_000);
        var invoice = await AddBlindedInvoiceAsync(carol, amountMsat, "blinded to carol", 1, 1, bobCarol, ct);
        var paths = await DecodeBlindedPathsAsync(carol, invoice.PaymentRequest, ct);
        var path = Assert.Single(paths);
        Assert.Equal(bob.LocalNodePubKey.ToLowerInvariant(), path.Path.FirstNodeId.ToString());
        Assert.Equal(2, path.Path.Hops.Count);

        // Act
        var result = await PayBlindedAsync(node, invoice, amountMsat, paths, ct);

        // Assert: paid through alice to bob, at least the path's fee on top of alice's
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        var settled = await LndTestHelpers.WaitForInvoiceStateAsync(carol, invoice.RHash.ToByteArray(),
                                                                     Invoice.Types.InvoiceState.Settled, s_timeout,
                                                                     ct);
        Assert.Equal((long)amountMsat, settled.AmtPaidMsat);
        Assert.True(result.Payment.Fee.MilliSatoshi >= path.PayInfo.ComputeFeeMsat(amountMsat));
        Console.WriteLine($"Paid {amountMsat} msat to carol's blinded path with {result.Payment.Fee.MilliSatoshi} msat "
                        + $"of fees over {result.Payment.Route.Count} hops");
    }

    [Fact]
    public async Task Given_OurBlindedPathThroughAlice_When_BobPaysIt_Then_WeSettleOurInvoice()
    {
        // Arrange: our public channel to alice with a push (alice can send to us), our blinded path through her
        var ct = TestContext.Current.CancellationToken;
        var bob = _fixture.GetLndNode("bob");
        var alice = _fixture.GetLndNode("alice");
        await using var node = await CreateNodeAsync("blinded-receive", ct);
        await PublicTopology.OpenPublicChannelToAliceAsync(_fixture, node, s_push, PublicTopology.LndNodes(_fixture),
                                                           ct, syncGraph: false);
        var amount = LightningMoney.MilliSatoshis(PublicTopology.UniqueAmountMsat(12_000_000));
        var invoice = await node.Services.GetRequiredService<IInvoiceService>()
                                .CreateInvoiceAsync(amount, "our blinded path", null, ct);
        var builder = node.Services.GetRequiredService<BlindedPathBuilder>();
        var paths = await Poll.ForAsync(async () =>
        {
            var built = await builder.BuildAsync(
                            new BlindedPathRequest(invoice.Preimage, amount, invoice.MinFinalCltvExpiry,
                                                   node.BlockchainMonitor.LastProcessedBlockHeight), ct);
            return built.Count > 0 ? built : null;
        }, s_timeout, "a blinded path through alice (her channel_update received)", ct, TimeSpan.FromSeconds(2));
        var path = Assert.Single(paths);
        Assert.Equal(alice.LocalNodePubKey.ToLowerInvariant(), path.Path.FirstNodeId.ToString());

        // Act: bob asks LND's router for a route to the path and sends to it
        var route = await Poll.ForAsync(async () =>
        {
            try
            {
                var request = new QueryRoutesRequest { AmtMsat = (long)amount.MilliSatoshi };
                request.BlindedPaymentPaths.Add(ToLnd(path));
                var routes = await bob.LightningClient.QueryRoutesAsync(request, cancellationToken: ct);
                return routes.Routes.FirstOrDefault();
            }
            catch (RpcException e)
            {
                Console.WriteLine($"bob's QueryRoutes: {e.Status.Detail}");
                return null;
            }
        }, s_timeout, "bob's route to our blinded path", ct, TimeSpan.FromSeconds(5));
        Console.WriteLine($"bob's route: {route.Hops.Count} hops, {route.TotalFeesMsat} msat fees, "
                        + $"timelock {route.TotalTimeLock}");
        var attempt = await bob.RouterClient.SendToRouteV2Async(new Testing.Lnd.Routerrpc.SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])invoice.PaymentHash),
            Route = route
        }, cancellationToken: ct);
        Console.WriteLine($"SendToRouteV2: {attempt.Status}, {attempt.Failure?.Code}, index "
                        + $"{attempt.Failure?.FailureSourceIndex}");

        // Assert
        foreach (var line in node.NodeLog.Where(l => l.Contains("blinded", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(line);
        Assert.Equal(Testing.Lnd.Lnrpc.HTLCAttempt.Types.HTLCStatus.Succeeded, attempt.Status);
        Assert.Equal(Convert.ToHexStringLower((byte[])invoice.Preimage),
                     Convert.ToHexStringLower(attempt.Preimage.ToByteArray()));
        var stored = await Poll.ForAsync(async () => await node.GetInvoiceAsync(invoice.PaymentHash, ct) is
        { Status: InvoiceStatus.Settled } settled
                                             ? settled
                                             : null, s_timeout, "our invoice settled", ct, TimeSpan.FromSeconds(1));
        Assert.Equal(amount, stored.Amount);
    }

    private async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, null, options =>
        {
            options.Features.OptionRouteBlinding = FeatureSupport.Optional;
        });
        node.ExtraConfiguration["Gossip:Enabled"] = "true";
        node.ExtraConfiguration["Gossip:AcceptPublicChannels"] = "true";
        node.ExtraConfiguration["Node:Alias"] = "nltg-" + name;
        node.ExtraConfiguration["Node:Color"] = GossipTestNodes.Color;
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// An LND blinded invoice: <paramref name="minRealHops"/> real hops before the recipient (0: the recipient is the
    /// introduction node), padded to <paramref name="numHops"/>, optionally entering over one channel.
    /// </summary>
    private static async Task<AddInvoiceResponse> AddBlindedInvoiceAsync(LndNodeConnection lnd, ulong amountMsat,
                                                                         string memo, uint minRealHops, uint numHops,
                                                                         ulong? incomingChannel, CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var request = new Invoice
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
                if (incomingChannel is { } scid)
                    request.BlindedPathConfig.IncomingChannelList.Add(scid);
                return await lnd.LightningClient.AddInvoiceAsync(request, cancellationToken: ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"{lnd.LocalAlias}'s blinded invoice: {e.Status.Detail}");
                return null;
            }
        }, s_timeout, $"{lnd.LocalAlias}'s blinded invoice", ct, TimeSpan.FromSeconds(5));
    }

    /// <summary>The invoice's blinded paths as LND decodes them (our BOLT 11 decoder reads no blinded paths).</summary>
    private static async Task<IReadOnlyList<BlindedPaymentPath>> DecodeBlindedPathsAsync(
        LndNodeConnection lnd, string paymentRequest, CancellationToken ct)
    {
        var decoded = await lnd.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = paymentRequest },
                                                                  cancellationToken: ct);
        return decoded.BlindedPaths.Select(FromLnd).ToList();
    }

    private static async Task<PayInvoiceResult> PayBlindedAsync(NLightningTestNode node, AddInvoiceResponse invoice,
                                                                ulong amountMsat,
                                                                IReadOnlyList<BlindedPaymentPath> paths,
                                                                CancellationToken ct)
    {
        var payments = node.Services.GetRequiredService<IPaymentService>();
        // Tried again while no route to the introduction node exists yet (such a try offers no HTLC)
        return await Poll.ForAsync(async () =>
        {
            var result = await payments.PayBlindedAsync(
                             new PayBlindedRequest(new Hash(invoice.RHash.ToByteArray()),
                                                   LightningMoney.MilliSatoshis(amountMsat), paths,
                                                   invoice.PaymentRequest),
                             new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(60) }, ct);
            Console.WriteLine($"PayBlindedAsync: {result.Payment.Status} after {result.Attempts} HTLC(s): "
                            + $"{result.Payment.FailureReason}");
            return result.Payment.Status == PaymentStatus.Succeeded || result.Attempts > 0 ? result : null;
        }, s_timeout, "our blinded payment offered", ct, TimeSpan.FromSeconds(5));
    }

    private static BlindedPaymentPath FromLnd(Testing.Lnd.Lnrpc.BlindedPaymentPath lnd)
    {
        var hops = lnd.BlindedPath.BlindedHops
                      .Select(h => new BlindedPathHop(new CompactPubKey(h.BlindedNode.ToByteArray()),
                                                      h.EncryptedData.ToByteArray()))
                      .ToList();
        var path = new BlindedPath(new CompactPubKey(lnd.BlindedPath.IntroductionNode.ToByteArray()),
                                   new CompactPubKey(lnd.BlindedPath.BlindingPoint.ToByteArray()), hops);
        return new BlindedPaymentPath(path, new BlindedPayInfo((uint)lnd.BaseFeeMsat, (uint)lnd.ProportionalFeeRate,
                                                               (ushort)lnd.TotalCltvDelta, lnd.HtlcMinMsat,
                                                               lnd.HtlcMaxMsat));
    }

    private static Testing.Lnd.Lnrpc.BlindedPaymentPath ToLnd(BlindedPaymentPath path)
    {
        var blinded = new Testing.Lnd.Lnrpc.BlindedPath
        {
            IntroductionNode = ByteString.CopyFrom((byte[])path.Path.FirstNodeId),
            BlindingPoint = ByteString.CopyFrom((byte[])path.Path.FirstPathKey)
        };
        foreach (var hop in path.Path.Hops)
            blinded.BlindedHops.Add(new BlindedHop
            {
                BlindedNode = ByteString.CopyFrom((byte[])hop.BlindedNodeId),
                EncryptedData = ByteString.CopyFrom(hop.EncryptedRecipientData.Span)
            });

        return new Testing.Lnd.Lnrpc.BlindedPaymentPath
        {
            BlindedPath = blinded,
            BaseFeeMsat = path.PayInfo.FeeBaseMsat,
            ProportionalFeeRate = path.PayInfo.FeeProportionalMillionths,
            TotalCltvDelta = path.PayInfo.CltvExpiryDelta,
            HtlcMinMsat = path.PayInfo.HtlcMinimumMsat,
            HtlcMaxMsat = path.PayInfo.HtlcMaximumMsat
        };
    }
}