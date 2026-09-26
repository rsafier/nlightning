using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Routing.Pathfinding;
using Gossip.Graph;
using Gossip.Graph.Interfaces;
using Send;

/// <summary>
/// Bridges the gossip graph (<see cref="IGraphStore"/>) and <see cref="MissionControl"/> to
/// <see cref="PaymentRoutePlanner"/> (BOLT 7 plan §3.1 <c>Routing/GraphPathSource</c>, G4-T3): one
/// <see cref="GraphRoutingContext"/> per round.
/// </summary>
/// <remarks>
/// No context (the planner then routes only over our direct channels and the route hints) when the node has no graph
/// store, the graph is disabled for the network (<see cref="GossipGraphOptions.IsEnabledFor"/>) or
/// <see cref="PaymentSendOptions.UseGraph"/> is off. Singleton; thread-safe (snapshots are immutable, mission control
/// hands out copies).
/// </remarks>
public sealed class GraphPathSource
{
    private readonly MissionControl _missionControl;
    private readonly IOptions<PaymentSendOptions> _sendOptions;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IGraphStore? _graphStore;
    private readonly IOptions<GossipGraphOptions>? _graphOptions;

    public GraphPathSource(MissionControl missionControl, IOptions<PaymentSendOptions> sendOptions,
                           IOptions<NodeOptions> nodeOptions, TimeProvider timeProvider,
                           IGraphStore? graphStore = null, IOptions<GossipGraphOptions>? graphOptions = null)
    {
        _missionControl = missionControl;
        _sendOptions = sendOptions;
        _nodeOptions = nodeOptions;
        _timeProvider = timeProvider;
        _graphStore = graphStore;
        _graphOptions = graphOptions;
    }

    /// <summary>Mission control, shared with the retry policy.</summary>
    public MissionControl MissionControl => _missionControl;

    /// <summary>True when payments may route over the graph.</summary>
    public bool IsAvailable =>
        _graphStore is not null && _sendOptions.Value.UseGraph
                                && (_graphOptions?.Value.IsEnabledFor(_nodeOptions.Value.BitcoinNetwork) ?? true);

    /// <summary>
    /// The context of one round, or null when the graph is not available.
    /// </summary>
    /// <param name="shadowCltvOffset">The payment's shadow CLTV offset (<see cref="ComputeShadowCltvOffset"/>).</param>
    public GraphRoutingContext? CreateContext(uint shadowCltvOffset)
    {
        if (!IsAvailable)
            return null;

        var snapshot = _missionControl.GetSnapshot();
        var options = _sendOptions.Value;
        return new GraphRoutingContext(_graphStore!.GetSnapshot(), snapshot.Liquidity, snapshot.PenalizedNodes,
                                       NowUnixSeconds())
        {
            StaleAfter = _graphOptions?.Value.StaleAfter,
            PathsPerAmount = Math.Max(1, options.GraphPathsPerRound),
            ShadowCltvOffset = shadowCltvOffset
        };
    }

    /// <summary>
    /// A shadow CLTV offset for a payment to <paramref name="payee"/> (BOLT 7 "limited random walk", B7-RT-01), at most
    /// <see cref="PaymentSendOptions.ShadowCltvMaxOffset"/>; 0 when that is 0 or the graph is not available.
    /// </summary>
    public uint ComputeShadowCltvOffset(CompactPubKey payee)
    {
        var max = _sendOptions.Value.ShadowCltvMaxOffset;
        if (max == 0 || !IsAvailable)
            return 0;

        return ShadowCltv.ComputeOffset(_graphStore!.GetSnapshot(), payee, Random.Shared, max);
    }

    private ulong NowUnixSeconds() => (ulong)Math.Max(0, _timeProvider.GetUtcNow().ToUnixTimeSeconds());
}