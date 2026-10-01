using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Services;

using Application.Node.Services;

/// <summary>
/// Waits for a node that is draining for its shutdown to go idle (NL-592): a <see cref="NodeBusyState"/> with no
/// HTLCs in flight and no mid-flight negotiation, held for a settle period so the last resolution (a fail we sent
/// back, an HTLC the peer added after the drain began) is irrevocably committed and revoked on both sides, not merely
/// sent (<see cref="Domain.Channels.Commitments.HtlcStateTable.IsFinal"/>).
/// </summary>
/// <remarks>
/// The snapshots are read without the channel locks (see <see cref="NodeBusyState"/>'s remarks); the settle period
/// covers a read taken while a transition lands. The caller owns the drain gate (it began the drain with
/// <see cref="INodeDrainState.TryBeginDrain"/> and ends it again unless the wait drained and it stops the node).
/// Used by the IPC <c>shutdown --wait</c> and by the signal drain (<c>Node:Shutdown:DrainOnSignalSeconds</c>).
/// </remarks>
public sealed class ShutdownDrainWaiter
{
    /// <summary>How long the idle state must hold before the node counts as drained.</summary>
    internal TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the busy state is re-read.</summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

    private readonly INodeBusyStateMonitor _busyStateMonitor;
    private readonly ILogger _logger;

    public ShutdownDrainWaiter(INodeBusyStateMonitor busyStateMonitor, ILogger<ShutdownDrainWaiter> logger)
    {
        _busyStateMonitor = busyStateMonitor;
        _logger = logger;
    }

    /// <summary>The outcome of a drain wait.</summary>
    /// <param name="Drained">Whether the node was idle for the settle period (the drain holds).</param>
    /// <param name="Snapshot">The idle state when drained; otherwise the last busy state seen (the wait is over and
    /// the gate is open again, the node goes on).</param>
    public readonly record struct Result(bool Drained, NodeBusyState Snapshot)
    {
        /// <summary>The state to report: busy counts when not drained, zeros when drained.</summary>
        public NodeBusyState Reported => Drained
            ? new NodeBusyState(Snapshot.ChannelCount, 0, 0, [], 0, -1)
            : Snapshot;
    }

    /// <summary>
    /// Waits until the node has been idle for the settle period (the drain gate stays closed; the caller stops the
    /// node). On timeout the result carries the last busy state and the caller ends the drain again (the gate opens;
    /// the node goes on). On cancellation <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public async Task<Result> WaitUntilIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var drained = false;
        try
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            var idleSince = default(DateTimeOffset?);
            var last = _busyStateMonitor.Snapshot();

            while (true)
            {
                if (last.IsBusy)
                {
                    idleSince = null;
                }
                else
                {
                    idleSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - idleSince >= Settle)
                    {
                        _logger.LogInformation("The node is drained: nothing was in flight for {Seconds}s",
                                               Settle.TotalSeconds);
                        drained = true;
                        return new Result(true, last);
                    }
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    _logger.LogWarning("The shutdown drain ran out of time with {Htlcs} HTLC(s) in flight on "
                                     + "{Channels} channel(s) and {Negotiations} negotiation(s) mid-flight; the drain "
                                     + "is over and the node goes on", last.HtlcsInFlight, last.Channels.Count,
                                       last.NegotiationCount);
                    return new Result(false, last);
                }

                await Task.Delay(Poll, cancellationToken);

                var snapshot = _busyStateMonitor.Snapshot();
                if (snapshot.IsBusy)
                    last = snapshot;
            }
        }
        finally
        {
            if (!drained)
                _logger.LogDebug("The shutdown drain did not reach idle; the caller ends the drain");
        }
    }
}