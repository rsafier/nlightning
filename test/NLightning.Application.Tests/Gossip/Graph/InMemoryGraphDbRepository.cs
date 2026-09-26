using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Gossip.Graph;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Persistence;

/// <summary>
/// An <see cref="IGraphDbRepository"/> over dictionaries: writes are staged and applied by <see cref="Commit"/> (the
/// unit of work's save), list reads read what is committed, like the EF repository. <see cref="FailNextSave"/> makes
/// the next commit throw and drop what was staged, <see cref="FailAtAttempt"/> a given one. The bulk members are the
/// interface's defaults (one single-row call each).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryGraphDbRepository : IGraphDbRepository
{
    private readonly Lock _lock = new();
    private readonly List<Action> _staged = [];

    public Dictionary<CompactPubKey, GraphNodeRecord> Nodes { get; } = new();
    public Dictionary<ShortChannelId, GraphChannelRecord> Channels { get; } = new();
    public Dictionary<(ShortChannelId, byte), GraphPolicyRecord> Policies { get; } = new();
    public Dictionary<CompactPubKey, GraphBannedNodeRecord> Bans { get; } = new();

    public int Saves { get; private set; }
    public bool FailNextSave { get; set; }

    /// <summary>How many commits were attempted, failed ones included.</summary>
    public int CommitAttempts { get; private set; }

    /// <summary>The 1-based commit attempt that fails (0: none).</summary>
    public int FailAtAttempt { get; set; }

    public void Commit()
    {
        lock (_lock)
        {
            CommitAttempts++;
            if (FailNextSave || CommitAttempts == FailAtAttempt)
            {
                FailNextSave = false;
                _staged.Clear();
                throw new InvalidOperationException("Simulated save failure");
            }

            foreach (var action in _staged)
                action();
            _staged.Clear();
            Saves++;
        }
    }

    public Task<GraphNodeRecord?> GetNodeAsync(CompactPubKey nodeId)
    {
        lock (_lock)
            return Task.FromResult(Nodes.GetValueOrDefault(nodeId));
    }

    public Task<IReadOnlyList<GraphNodeRecord>> GetNodesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<GraphNodeRecord>>(Nodes.Values.ToList());
    }

    public Task UpsertNodeAsync(GraphNodeRecord node) => Stage(() => Nodes[node.NodeId] = node);

    public Task<bool> DeleteNodeAsync(CompactPubKey nodeId)
    {
        lock (_lock)
        {
            var exists = Nodes.ContainsKey(nodeId);
            _staged.Add(() => Nodes.Remove(nodeId));
            return Task.FromResult(exists);
        }
    }

    public Task<GraphChannelRecord?> GetChannelAsync(ShortChannelId shortChannelId)
    {
        lock (_lock)
            return Task.FromResult(Channels.GetValueOrDefault(shortChannelId));
    }

    public Task<IReadOnlyList<GraphChannelRecord>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<GraphChannelRecord>>(Channels.Values.ToList());
    }

    public Task UpsertChannelAsync(GraphChannelRecord channel) =>
        Stage(() => Channels[channel.ShortChannelId] = channel);

    public Task<bool> MarkChannelSpentAsync(ShortChannelId shortChannelId, uint height)
    {
        lock (_lock)
        {
            var exists = Channels.ContainsKey(shortChannelId);
            _staged.Add(() =>
            {
                if (Channels.TryGetValue(shortChannelId, out var channel))
                    Channels[shortChannelId] = channel with { SpentAtHeight = height };
            });
            return Task.FromResult(exists);
        }
    }

    public Task<int> ClearSpentAboveAsync(uint height)
    {
        lock (_lock)
        {
            var reorged = Channels.Values.Where(c => c.SpentAtHeight > height).ToList();
            _staged.Add(() =>
            {
                foreach (var channel in reorged)
                    Channels[channel.ShortChannelId] = channel with { SpentAtHeight = null };
            });
            return Task.FromResult(reorged.Count);
        }
    }

    public Task<IReadOnlyList<ShortChannelId>> GetChannelsSpentAtOrBelowAsync(uint height)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ShortChannelId>>(
                Channels.Values.Where(c => c.SpentAtHeight <= height).Select(c => c.ShortChannelId).ToList());
    }

    public Task<bool> DeleteChannelAsync(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            var exists = Channels.ContainsKey(shortChannelId);
            _staged.Add(() =>
            {
                Channels.Remove(shortChannelId);
                Policies.Remove((shortChannelId, 0));
                Policies.Remove((shortChannelId, 1));
            });
            return Task.FromResult(exists);
        }
    }

    public Task<IReadOnlyList<GraphPolicyRecord>> GetPoliciesAsync(ShortChannelId shortChannelId)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<GraphPolicyRecord>>(
                Policies.Values.Where(p => p.ShortChannelId == shortChannelId).ToList());
    }

    public Task<IReadOnlyList<GraphPolicyRecord>> GetAllPoliciesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<GraphPolicyRecord>>(Policies.Values.ToList());
    }

    public Task UpsertPolicyAsync(GraphPolicyRecord policy) =>
        Stage(() =>
        {
            if (!Channels.ContainsKey(policy.ShortChannelId))
                throw new InvalidOperationException("Foreign key: the policy's channel is not stored");

            Policies[(policy.ShortChannelId, policy.Direction)] = policy;
        });

    public Task<GraphBannedNodeRecord?> GetBanAsync(CompactPubKey nodeId)
    {
        lock (_lock)
            return Task.FromResult(Bans.GetValueOrDefault(nodeId));
    }

    public Task<IReadOnlyList<GraphBannedNodeRecord>> GetActiveBansAsync(DateTimeOffset now)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<GraphBannedNodeRecord>>(
                Bans.Values.Where(b => b.Until > now).ToList());
    }

    public Task UpsertBanAsync(GraphBannedNodeRecord ban) => Stage(() => Bans[ban.NodeId] = ban);

    public Task<int> DeleteExpiredBansAsync(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = Bans.Values.Where(b => b.Until <= now).Select(b => b.NodeId).ToList();
            _staged.Add(() =>
            {
                foreach (var nodeId in expired)
                    Bans.Remove(nodeId);
            });
            return Task.FromResult(expired.Count);
        }
    }

    private Task Stage(Action action)
    {
        lock (_lock)
            _staged.Add(action);
        return Task.CompletedTask;
    }
}