using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Routing.Pathfinding;
using Send;

/// <summary>
/// What our payments learnt about the network, shared by every later payment (BOLT 7 plan G4-T2, decisions D6 and D8):
/// liquidity bounds per channel direction (<see cref="LiquidityEstimates"/>, fading with
/// <see cref="PaymentSendOptions.MissionControlHalfLife"/>) and the nodes that recently sent a NODE failure.
/// </summary>
/// <remarks>
/// <para>From an attempt along a route (our peer is hop 0, the payee the last hop; the edge of hop <c>i</c> is the
/// channel from hop <c>i</c> to hop <c>i + 1</c>, carrying hop <c>i</c>'s <c>amt_to_forward</c>):</para>
/// <list type="bullet">
///   <item>success: every edge carried its amount (a lower bound on each);</item>
///   <item>a failure from hop <c>i</c>: the edges before it carried their amounts (lower bounds); then, for an
///   intermediate hop, <c>temporary_channel_failure</c> is an upper bound below the amount of edge <c>i</c>; a failure
///   that says the channel cannot forward at all (a PERM channel failure, <c>unknown_next_peer</c>,
///   <c>channel_disabled</c>, a BADONION from downstream) bounds edge <c>i</c> at 0 (unusable until it fades); the
///   policy failures (<c>fee_insufficient</c>, <c>incorrect_cltv_expiry</c>, <c>amount_below_minimum</c>,
///   <c>expiry_too_soon</c>) teach nothing about liquidity; a NODE failure avoids hop <c>i</c>'s node for
///   <see cref="PaymentSendOptions.NodeFailurePenalty"/>. A failure from the payee (every edge reached it) only records
///   the lower bounds.</item>
/// </list>
/// <para>Our own first channel is never recorded: the planner reads its live state. What the failure's
/// <c>channel_update</c> says is never recorded here either (BOLT 4: it applies only to the failing payment, D9).</para>
/// <para>In memory only (D8): a restart forgets everything. Singleton; thread-safe (one lock; readers get a copy).</para>
/// </remarks>
public sealed class MissionControl
{
    private readonly Lock _sync = new();
    private readonly LiquidityEstimates _liquidity;
    private readonly Dictionary<CompactPubKey, DateTimeOffset> _nodeFailures = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _nodeFailurePenalty;

    public MissionControl(IOptions<PaymentSendOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
        var halfLife = options.Value.MissionControlHalfLife;
        _liquidity = new LiquidityEstimates(halfLife > TimeSpan.Zero ? halfLife : TimeSpan.FromHours(1));
        _nodeFailurePenalty = options.Value.NodeFailurePenalty;
    }

    /// <summary>The number of channel directions with a liquidity record.</summary>
    public int ChannelRecordCount
    {
        get
        {
            lock (_sync)
                return _liquidity.Count;
        }
    }

    /// <summary>
    /// Records that an HTLC along <paramref name="route"/> reached the payee (a fulfill): each edge carried its amount.
    /// </summary>
    public void RecordSuccess(PaymentRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var now = NowUnixSeconds();
        lock (_sync)
            RecordCarried(route, route.Hops.Count - 1, now);
    }

    /// <summary>
    /// Records what the failure of an HTLC along <paramref name="route"/> from hop <paramref name="erringHopIndex"/>
    /// says (see the class remarks).
    /// </summary>
    /// <param name="route">The failed part's route.</param>
    /// <param name="erringHopIndex">The hop that sent the error (0 is our peer).</param>
    /// <param name="code">The failure code; null for an unreadable failure (only the lower bounds are recorded).</param>
    public void RecordFailure(PaymentRoute route, int erringHopIndex, FailureCode? code)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentOutOfRangeException.ThrowIfNegative(erringHopIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(erringHopIndex, route.Hops.Count);

        var now = NowUnixSeconds();
        var isFinal = erringHopIndex == route.Hops.Count - 1;
        lock (_sync)
        {
            // The HTLC reached the erring hop: every edge before it carried its amount
            RecordCarried(route, erringHopIndex, now);
            if (isFinal || code is not { } failureCode)
                return;

            var flags = (FailureCodeFlags)((ushort)failureCode & 0xF000);
            if (flags.HasFlag(FailureCodeFlags.Node))
            {
                _nodeFailures[route.Hops[erringHopIndex].NodeId] = _timeProvider.GetUtcNow();
                return;
            }

            var edge = Edge(route, erringHopIndex);
            var amount = route.Hops[erringHopIndex].AmountToForward.MilliSatoshi;
            switch (failureCode)
            {
                case FailureCode.TemporaryChannelFailure:
                    _liquidity.RecordFailure(edge, amount, now);
                    break;
                case FailureCode.FeeInsufficient or FailureCode.IncorrectCltvExpiry or FailureCode.AmountBelowMinimum
                  or FailureCode.ExpiryTooSoon:
                    // A policy mismatch: the gossip refresh and the failure's channel_update handle it
                    break;
                default:
                    _liquidity.RecordFailure(edge, 0, now);
                    break;
            }
        }
    }

    /// <summary>
    /// A copy of what was learnt, for one pathfinding run: the liquidity estimates and the nodes to avoid now.
    /// </summary>
    public MissionControlSnapshot GetSnapshot()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            var penalized = new HashSet<CompactPubKey>();
            foreach (var (node, failedAt) in _nodeFailures.ToList())
            {
                if (now - failedAt < _nodeFailurePenalty)
                    penalized.Add(node);
                else
                    _nodeFailures.Remove(node);
            }

            return new MissionControlSnapshot(_liquidity.Clone(), penalized);
        }
    }

    /// <summary>
    /// The bounds recorded for the direction <paramref name="from"/> → <paramref name="to"/> of
    /// <paramref name="shortChannelId"/> (not decayed), for tests and diagnostics.
    /// </summary>
    public bool TryGetBounds(ShortChannelId shortChannelId, CompactPubKey from, CompactPubKey to, out ulong minMsat,
                             out ulong? maxMsat)
    {
        lock (_sync)
            return _liquidity.TryGetBounds(DirectedChannel.Between(shortChannelId, from, to), out minMsat, out maxMsat,
                                           out _);
    }

    private void RecordCarried(PaymentRoute route, int reachedHopIndex, ulong now)
    {
        for (var i = 0; i < reachedHopIndex; i++)
            _liquidity.RecordSuccess(Edge(route, i), route.Hops[i].AmountToForward.MilliSatoshi, now);
    }

    private static DirectedChannel Edge(PaymentRoute route, int hopIndex) =>
        DirectedChannel.Between(route.Hops[hopIndex].OutgoingShortChannelId!.Value, route.Hops[hopIndex].NodeId,
                                route.Hops[hopIndex + 1].NodeId);

    private ulong NowUnixSeconds() => (ulong)Math.Max(0, _timeProvider.GetUtcNow().ToUnixTimeSeconds());
}

/// <summary>
/// A copy of <see cref="MissionControl"/>'s state for one pathfinding run.
/// </summary>
/// <param name="Liquidity">The liquidity estimates (the caller's own copy).</param>
/// <param name="PenalizedNodes">Nodes not to route through now.</param>
public sealed record MissionControlSnapshot(LiquidityEstimates Liquidity, IReadOnlySet<CompactPubKey> PenalizedNodes);