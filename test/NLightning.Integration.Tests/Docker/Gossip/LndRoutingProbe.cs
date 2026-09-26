using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Utils;

/// <summary>
/// The LND side of the payment proofs over public channels (BOLT 7 goal proofs and Proof G4): the policies LND
/// announced (<c>GetChanInfo</c>), the BOLT 7 fee they imply, the forwards LND recorded (<c>ForwardingHistory</c>),
/// routes LND computes (<c>QueryRoutes</c>), invoices without route hints (<c>DecodePayReq</c>) and policy changes
/// (<c>UpdateChannelPolicy</c>). Every value read is logged.
/// </summary>
public static class LndRoutingProbe
{
    private static readonly TimeSpan s_forwardLogTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One forward an LND node recorded.
    /// </summary>
    public sealed record Forward(string NodeAlias, string NodeIdHex, ulong ChannelIn, ulong ChannelOut,
                                 ulong AmountInMsat, ulong AmountOutMsat, ulong FeeMsat);

    /// <summary>
    /// The policy <paramref name="fromNodeHex"/> announced for <paramref name="shortChannelId"/>, as
    /// <paramref name="lnd"/>'s graph has it.
    /// </summary>
    /// <exception cref="InvalidOperationException">LND does not know the channel or that end has no policy.</exception>
    public static async Task<RoutingPolicy> GetPolicyAsync(LNDNodeConnection lnd, ulong shortChannelId,
                                                           string fromNodeHex, CancellationToken cancellationToken)
    {
        var edge = await GossipGraphProbe.TryGetChanInfoAsync(lnd, shortChannelId, cancellationToken)
                ?? throw new InvalidOperationException(
                       $"{lnd.LocalAlias} does not know {new ShortChannelId(shortChannelId)}");
        var policy = string.Equals(edge.Node1Pub, fromNodeHex, StringComparison.OrdinalIgnoreCase)
                         ? edge.Node1Policy
                         : string.Equals(edge.Node2Pub, fromNodeHex, StringComparison.OrdinalIgnoreCase)
                             ? edge.Node2Policy
                             : throw new InvalidOperationException(
                                   $"{fromNodeHex[..16]}… is not an end of {new ShortChannelId(shortChannelId)}");
        return policy ?? throw new InvalidOperationException(
                             $"{lnd.LocalAlias} has no policy of {fromNodeHex[..16]}… for {shortChannelId}");
    }

    /// <summary>
    /// BOLT 7 "HTLC Fees": <c>fee_base_msat + amount_to_forward * fee_proportional_millionths / 1000000</c>, rounded
    /// down (LND's <c>fee_rate_milli_msat</c> is the proportional fee in millionths).
    /// </summary>
    public static ulong FeeFor(RoutingPolicy policy, ulong amountToForwardMsat) =>
        (ulong)policy.FeeBaseMsat + amountToForwardMsat * (ulong)policy.FeeRateMilliMsat / 1_000_000;

    /// <summary>
    /// BOLT 7 fee of a policy given as numbers (e.g. CLN's <c>listchannels</c>).
    /// </summary>
    public static ulong FeeFor(ulong feeBaseMsat, ulong feeProportionalMillionths, ulong amountToForwardMsat) =>
        feeBaseMsat + amountToForwardMsat * feeProportionalMillionths / 1_000_000;

    /// <summary>
    /// The forwards <paramref name="lndNodes"/> recorded since <paramref name="since"/>.
    /// </summary>
    public static async Task<IReadOnlyList<Forward>> GetForwardsAsync(IEnumerable<LNDNodeConnection> lndNodes,
                                                                      DateTimeOffset since,
                                                                      CancellationToken cancellationToken)
    {
        var forwards = new List<Forward>();
        foreach (var lnd in lndNodes)
        {
            var history = await lnd.LightningClient.ForwardingHistoryAsync(new ForwardingHistoryRequest
            {
                StartTime = (ulong)Math.Max(0, since.ToUnixTimeSeconds() - 5),
                NumMaxEvents = 10_000
            }, cancellationToken: cancellationToken);
            foreach (var e in history.ForwardingEvents)
            {
                var forward = new Forward(lnd.LocalAlias, lnd.LocalNodePubKey.ToLowerInvariant(), e.ChanIdIn,
                                          e.ChanIdOut, e.AmtInMsat, e.AmtOutMsat, e.FeeMsat);
                Console.WriteLine($"{lnd.LocalAlias} forward {new ShortChannelId(e.ChanIdIn)} -> "
                                + $"{new ShortChannelId(e.ChanIdOut)}: in {e.AmtInMsat}, out {e.AmtOutMsat}, "
                                + $"fee {e.FeeMsat} msat");
                forwards.Add(forward);
            }
        }

        return forwards;
    }

    /// <summary>
    /// The forwards that carried a payment which reached the payee with <paramref name="amountAtPayeeMsat"/>, payee
    /// side first: the forward whose outgoing amount is that amount, then the one whose outgoing amount is that
    /// forward's incoming amount, and so on until the incoming channel is <paramref name="firstChannel"/> (the payer's
    /// channel). Null while the chain is incomplete; fails the test when an amount matches more than one forward.
    /// </summary>
    public static IReadOnlyList<Forward>? TryTraceForwards(IReadOnlyList<Forward> forwards, ulong amountAtPayeeMsat,
                                                           ulong firstChannel)
    {
        var chain = new List<Forward>();
        var amount = amountAtPayeeMsat;
        while (chain.Count < 20)
        {
            var hop = forwards.Where(f => f.AmountOutMsat == amount && !chain.Contains(f)).ToList();
            if (hop.Count == 0)
                return null;

            Assert.True(hop.Count == 1,
                        $"Expected exactly one forward with {amount} msat out, found {hop.Count} "
                      + $"(after {chain.Count} traced)");
            chain.Add(hop[0]);
            if (hop[0].ChannelIn == firstChannel)
                return chain;

            amount = hop[0].AmountInMsat;
        }

        throw new InvalidOperationException("The forward chain did not reach the payer's channel within 20 hops");
    }

    /// <summary>
    /// Polls <paramref name="lndNodes"/>' forwarding logs (LND writes forwarding events in batches, every 15 s) until
    /// <see cref="TryTraceForwards"/> finds the whole chain of the payment; returns it, payee side first.
    /// </summary>
    public static Task<IReadOnlyList<Forward>> WaitForForwardChainAsync(IReadOnlyList<LNDNodeConnection> lndNodes,
                                                                         DateTimeOffset since,
                                                                         ulong amountAtPayeeMsat, ulong firstChannel,
                                                                         CancellationToken cancellationToken) =>
        Poll.ForAsync(async () => TryTraceForwards(await GetForwardsAsync(lndNodes, since, cancellationToken),
                                                   amountAtPayeeMsat, firstChannel),
                      s_forwardLogTimeout, $"LND's forwards of {amountAtPayeeMsat} msat logged", cancellationToken,
                      TimeSpan.FromSeconds(3));

    /// <summary>
    /// <paramref name="lnd"/> has no channel with <paramref name="nodeIdHex"/> (so a payment between them has to
    /// travel through other nodes).
    /// </summary>
    public static async Task AssertNoChannelWithAsync(LNDNodeConnection lnd, string nodeIdHex,
                                                      CancellationToken cancellationToken)
    {
        var channels = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                   cancellationToken: cancellationToken);
        var pending = await lnd.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                     cancellationToken: cancellationToken);
        Assert.DoesNotContain(channels.Channels, c => string.Equals(c.RemotePubkey, nodeIdHex,
                                                                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pending.PendingOpenChannels, c => string.Equals(c.Channel.RemoteNodePub, nodeIdHex,
                                                                             StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every forward in <paramref name="chain"/> charged exactly the fee its node announced for the outgoing channel
    /// (read from <paramref name="observer"/>'s graph), and its in/out amounts differ by that fee. Returns the total.
    /// </summary>
    public static async Task<ulong> AssertForwardFeesMatchPoliciesAsync(LNDNodeConnection observer,
                                                                        IReadOnlyList<Forward> chain,
                                                                        CancellationToken cancellationToken)
    {
        ulong total = 0;
        foreach (var forward in chain)
        {
            var policy = await GetPolicyAsync(observer, forward.ChannelOut, forward.NodeIdHex, cancellationToken);
            var expected = FeeFor(policy, forward.AmountOutMsat);
            Console.WriteLine($"{forward.NodeAlias} policy on {new ShortChannelId(forward.ChannelOut)}: base "
                            + $"{policy.FeeBaseMsat}, ppm {policy.FeeRateMilliMsat}: fee for {forward.AmountOutMsat} = "
                            + $"{expected}, charged {forward.FeeMsat}");
            Assert.Equal(expected, forward.FeeMsat);
            Assert.Equal(expected, forward.AmountInMsat - forward.AmountOutMsat);
            total += forward.FeeMsat;
        }

        return total;
    }

    /// <summary>
    /// Every forwarding node of <paramref name="route"/> charged exactly the fee it announced for its outgoing channel
    /// (read from <paramref name="observer"/>'s graph); the route's total fee is their sum. Returns the total.
    /// </summary>
    /// <remarks>
    /// LND's route layout: hop i reaches <c>Hops[i].PubKey</c> over <c>Hops[i].ChanId</c>; its
    /// <c>AmtToForwardMsat</c> is what that node sends on (over <c>Hops[i + 1].ChanId</c>, or receives for the last
    /// hop) and its <c>FeeMsat</c> what that node keeps, so channel i carries
    /// <c>Hops[i].AmtToForwardMsat + Hops[i].FeeMsat</c>.
    /// </remarks>
    public static async Task<ulong> AssertRouteFeesMatchPoliciesAsync(LNDNodeConnection observer, Route route,
                                                                      CancellationToken cancellationToken)
    {
        ulong total = 0;
        for (var i = 0; i < route.Hops.Count - 1; i++)
        {
            var forwarder = route.Hops[i];
            var next = route.Hops[i + 1];
            var policy = await GetPolicyAsync(observer, next.ChanId, forwarder.PubKey, cancellationToken);
            var expected = FeeFor(policy, (ulong)forwarder.AmtToForwardMsat);
            Console.WriteLine($"Hop {i} {forwarder.PubKey[..16]}… out on {new ShortChannelId(next.ChanId)}: base "
                            + $"{policy.FeeBaseMsat}, ppm {policy.FeeRateMilliMsat}: fee for "
                            + $"{forwarder.AmtToForwardMsat} = {expected}, route {forwarder.FeeMsat}");
            Assert.Equal(expected, (ulong)forwarder.FeeMsat);
            Assert.Equal(forwarder.AmtToForwardMsat, next.AmtToForwardMsat + next.FeeMsat);
            total += expected;
        }

        Assert.Equal(0, route.Hops[^1].FeeMsat);
        Assert.Equal(total, (ulong)route.TotalFeesMsat);
        return total;
    }

    /// <summary>
    /// What each channel of <paramref name="route"/> carries, first channel first (see
    /// <see cref="AssertRouteFeesMatchPoliciesAsync"/> for LND's layout).
    /// </summary>
    public static IReadOnlyList<ulong> ChannelAmounts(Route route) =>
        route.Hops.Select(h => (ulong)(h.AmtToForwardMsat + h.FeeMsat)).ToList();

    /// <summary>
    /// The routes of <paramref name="lnd"/>'s pathfinding from <paramref name="sourceNodeHex"/> (any node of its
    /// graph) to <paramref name="destinationHex"/> for <paramref name="amountMsat"/>, without mission control.
    /// </summary>
    public static async Task<Route> QueryRouteAsync(LNDNodeConnection lnd, string sourceNodeHex, string destinationHex,
                                                    long amountMsat, CancellationToken cancellationToken)
    {
        var response = await lnd.LightningClient.QueryRoutesAsync(new QueryRoutesRequest
        {
            PubKey = destinationHex.ToLowerInvariant(),
            SourcePubKey = sourceNodeHex.ToLowerInvariant(),
            AmtMsat = amountMsat,
            UseMissionControl = false
        }, cancellationToken: cancellationToken);
        var route = Assert.Single(response.Routes);
        Console.WriteLine($"{lnd.LocalAlias} QueryRoutes {sourceNodeHex[..16]}… -> {destinationHex[..16]}… "
                        + $"{amountMsat} msat: {Describe(route)}");
        return route;
    }

    /// <summary>
    /// <paramref name="bolt11"/> as <paramref name="lnd"/> decodes it; asserts it carries no route hint (the payer has
    /// to find the payee through the graph).
    /// </summary>
    public static async Task<PayReq> AssertNoRouteHintsAsync(LNDNodeConnection lnd, string bolt11,
                                                             CancellationToken cancellationToken)
    {
        var decoded = await lnd.LightningClient.DecodePayReqAsync(new PayReqString { PayReq = bolt11 },
                                                                  cancellationToken: cancellationToken);
        Console.WriteLine($"{lnd.LocalAlias} DecodePayReq: destination {decoded.Destination}, {decoded.NumMsat} msat, "
                        + $"{decoded.RouteHints.Count} route hints");
        Assert.Empty(decoded.RouteHints);
        return decoded;
    }

    /// <summary>
    /// Sets <paramref name="lnd"/>'s policy on the channel at <paramref name="channelPoint"/> (<c>txid:index</c>),
    /// keeping the HTLC limits of <paramref name="current"/>.
    /// </summary>
    public static async Task UpdatePolicyAsync(LNDNodeConnection lnd, string channelPoint, RoutingPolicy current,
                                               long feeBaseMsat, uint feePpm, CancellationToken cancellationToken)
    {
        var parts = channelPoint.Split(':');
        var request = new PolicyUpdateRequest
        {
            ChanPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
            BaseFeeMsat = feeBaseMsat,
            FeeRatePpm = feePpm,
            TimeLockDelta = current.TimeLockDelta,
            MaxHtlcMsat = current.MaxHtlcMsat,
            MinHtlcMsat = (ulong)current.MinHtlc,
            MinHtlcMsatSpecified = true
        };
        var response = await lnd.LightningClient.UpdateChannelPolicyAsync(request,
                                                                          cancellationToken: cancellationToken);
        Console.WriteLine($"{lnd.LocalAlias} UpdateChannelPolicy {channelPoint}: base {feeBaseMsat}, ppm {feePpm}, "
                        + $"delta {current.TimeLockDelta}; {response.FailedUpdates.Count} failed");
        Assert.Empty(response.FailedUpdates);
    }

    /// <summary>
    /// The channel point (<c>txid:index</c>) of <paramref name="shortChannelId"/> in <paramref name="lnd"/>'s own
    /// channels.
    /// </summary>
    public static async Task<string> GetChannelPointAsync(LNDNodeConnection lnd, ulong shortChannelId,
                                                          CancellationToken cancellationToken)
    {
        var channels = await lnd.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                   cancellationToken: cancellationToken);
        return channels.Channels.Single(c => c.ChanId == shortChannelId).ChannelPoint;
    }

    public static string Describe(Route route) =>
        $"{route.Hops.Count} hops ["
      + string.Join(" -> ", route.Hops.Select(h => $"{new ShortChannelId(h.ChanId)}:{h.PubKey[..16]}… amt "
                                                 + $"{h.AmtToForwardMsat} fee {h.FeeMsat}"))
      + $"], total amt {route.TotalAmtMsat}, total fee {route.TotalFeesMsat}";
}