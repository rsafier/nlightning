using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Acceptance;

using Domain.Channels.Acceptance;

/// <summary>
/// <see cref="IChannelOpenDecisionGate"/> over the registered deciders (NL-1180): none accepts at once; otherwise each
/// decider is asked in registration order, the first rejection wins, and the acceptances are merged by
/// <see cref="ChannelOpenDecisionRules.TryMerge"/> (a disagreement rejects), as LND's <c>ChainedAcceptor</c> does.
/// A decider that throws rejects the open.
/// </summary>
public sealed class ChannelOpenDecisionGate : IChannelOpenDecisionGate
{
    private readonly ConcurrentDictionary<long, IChannelOpenDecider> _deciders = new();
    private readonly ILogger<ChannelOpenDecisionGate> _logger;
    private long _nextId;

    public ChannelOpenDecisionGate(ILogger<ChannelOpenDecisionGate> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool HasDeciders => !_deciders.IsEmpty;

    /// <inheritdoc />
    public IDisposable Register(IChannelOpenDecider decider)
    {
        ArgumentNullException.ThrowIfNull(decider);
        var id = Interlocked.Increment(ref _nextId);
        _deciders[id] = decider;
        return new Registration(this, id);
    }

    /// <inheritdoc />
    public async Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request,
                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deciders = _deciders.OrderBy(d => d.Key).Select(d => d.Value).ToList();
        if (deciders.Count == 0)
            return ChannelOpenDecision.Accepted;

        var merged = new ChannelOpenDecision { Accept = true };
        foreach (var decider in deciders)
        {
            ChannelOpenDecision decision;
            try
            {
                decision = await decider.DecideAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "A channel open decider failed on {PendingChannelId} of {Peer}: rejecting",
                                   request.PendingChannelId, request.NodeId);
                return ChannelOpenDecision.Rejected();
            }

            if (!decision.Accept)
                return decision with { Error = ChannelOpenDecisionRules.Truncate(decision.Error) };

            if (ChannelOpenDecisionRules.TryMerge(merged, decision, out merged) is { } conflict)
            {
                _logger.LogWarning("Channel open deciders gave inconsistent answers for {PendingChannelId}: {Conflict}",
                                   request.PendingChannelId, conflict);
                return ChannelOpenDecision.Rejected();
            }
        }

        return merged;
    }

    private sealed class Registration(ChannelOpenDecisionGate gate, long id) : IDisposable
    {
        public void Dispose() => gate._deciders.TryRemove(id, out _);
    }
}