using Grpc.Core;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

using Testing.Lnd.Lnrpc;

/// <summary>
/// What an LND node's graph says about a channel: LND lists a fresh channel active before its router has the edge
/// (NL-319), so readiness waits read the graph too. <see cref="HasOwnChannelEdgeAsync"/> is the Docker suites'
/// <c>LndTestHelpers.HasOwnChannelEdgeAsync</c>; <see cref="EdgeProblem"/> is the stricter check of a public channel
/// another node must route over.
/// </summary>
public static class LndGraph
{
    /// <summary>The edge of <paramref name="chanId"/> in <paramref name="lnd"/>'s graph, or null when LND does not know it.</summary>
    public static async Task<ChannelEdge?> GetEdgeAsync(LndNode lnd, ulong chanId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lnd);
        try
        {
            return await lnd.Lightning.GetChanInfoAsync(new ChanInfoRequest { ChanId = chanId },
                                                        deadline: DateTime.UtcNow.AddSeconds(10),
                                                        cancellationToken: cancellationToken)
                            .ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound
                                  || e.Status.Detail.Contains("edge not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="lnd"/>'s graph has the channel with <paramref name="lnd"/>'s own policy: its router can
    /// use it as a first hop (a private channel's edge only ever has that one side in the peer's graph).
    /// </summary>
    public static async Task<bool> HasOwnChannelEdgeAsync(LndNode lnd, ulong chanId,
                                                          CancellationToken cancellationToken)
    {
        var edge = await GetEdgeAsync(lnd, chanId, cancellationToken).ConfigureAwait(false);
        if (edge is null)
            return false;

        var ownId = await lnd.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
        return OwnPolicy(edge, ownId) is not null;
    }

    /// <summary>The policy <paramref name="nodeId"/> set on <paramref name="edge"/>, or null (not an end, not known yet).</summary>
    public static RoutingPolicy? OwnPolicy(ChannelEdge edge, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (string.Equals(edge.Node1Pub, nodeId, StringComparison.OrdinalIgnoreCase))
            return edge.Node1Policy;
        return string.Equals(edge.Node2Pub, nodeId, StringComparison.OrdinalIgnoreCase) ? edge.Node2Policy : null;
    }

    /// <summary>
    /// Why <paramref name="edge"/> is not yet routable both ways for any node of the network, or null when it is: it
    /// is known, both ends' policies are there and enabled, and the funder's side carries
    /// <paramref name="funderPolicy"/> (when given).
    /// </summary>
    public static string? EdgeProblem(ChannelEdge? edge, string funderId, long? baseFeeMsat = null,
                                      long? feeRatePpm = null, uint? timeLockDelta = null)
    {
        if (edge is null)
            return "not in the graph";
        if (edge.Node1Policy is null)
            return $"no policy of {Short(edge.Node1Pub)}";
        if (edge.Node2Policy is null)
            return $"no policy of {Short(edge.Node2Pub)}";
        if (edge.Node1Policy.Disabled)
            return $"{Short(edge.Node1Pub)}'s side disabled";
        if (edge.Node2Policy.Disabled)
            return $"{Short(edge.Node2Pub)}'s side disabled";

        var funder = OwnPolicy(edge, funderId);
        if (funder is null)
            return $"{Short(funderId)} is not an end of the edge";
        if ((baseFeeMsat is { } baseFee && funder.FeeBaseMsat != baseFee)
         || (feeRatePpm is { } rate && funder.FeeRateMilliMsat != rate)
         || (timeLockDelta is { } delta && funder.TimeLockDelta != delta))
            return $"the funder's policy is {funder.FeeBaseMsat} msat + {funder.FeeRateMilliMsat} ppm, delta "
                 + $"{funder.TimeLockDelta}, not yet {baseFeeMsat} msat + {feeRatePpm} ppm, delta {timeLockDelta}";

        return null;
    }

    private static string Short(string nodeId) => nodeId.Length > 8 ? nodeId[..8] : nodeId;
}