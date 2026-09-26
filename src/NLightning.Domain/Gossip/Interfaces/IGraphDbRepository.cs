using System.Runtime.CompilerServices;

namespace NLightning.Domain.Gossip.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Persistence;

/// <summary>
/// The persisted BOLT 7 graph (migration <c>AddGossipGraph</c>): announced nodes, channels, their per-direction
/// policies and banned nodes, each with the raw signed bytes. Reached through <c>IUnitOfWork.GraphDbRepository</c>.
/// </summary>
/// <remarks>
/// Writes are staged and commit with <c>IUnitOfWork.SaveChangesAsync</c> (the in-memory graph persists write-behind,
/// BOLT 7 plan D2). Single-key reads go through the change tracker and see what this unit of work staged; list reads
/// read what is saved. Upserts replace every column of an existing row. The bulk members (default implementations
/// over the single-row ones) are what the store's flush and startup load use; the EF repository implements them with
/// one read per batch and a streamed read.
/// </remarks>
public interface IGraphDbRepository
{
    /// <summary>The stored announcement of <paramref name="nodeId"/>, or null.</summary>
    Task<GraphNodeRecord?> GetNodeAsync(CompactPubKey nodeId);

    /// <summary>Every stored node announcement (startup load).</summary>
    Task<IReadOnlyList<GraphNodeRecord>> GetNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages the node's announcement, replacing a stored one.</summary>
    Task UpsertNodeAsync(GraphNodeRecord node);

    /// <summary>Stages the deletion of a node's announcement; false when there is none.</summary>
    Task<bool> DeleteNodeAsync(CompactPubKey nodeId);

    /// <summary>The stored channel, or null.</summary>
    Task<GraphChannelRecord?> GetChannelAsync(ShortChannelId shortChannelId);

    /// <summary>Every stored channel, spent ones included (startup load).</summary>
    Task<IReadOnlyList<GraphChannelRecord>> GetChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages the channel, replacing a stored one (its policies are kept).</summary>
    Task UpsertChannelAsync(GraphChannelRecord channel);

    /// <summary>
    /// Stages the funding spend of a channel at <paramref name="height"/>; false when the channel is unknown.
    /// </summary>
    Task<bool> MarkChannelSpentAsync(ShortChannelId shortChannelId, uint height);

    /// <summary>
    /// Reorg: stages clearing the spend of every channel spent above <paramref name="height"/>; returns how many.
    /// </summary>
    Task<int> ClearSpentAboveAsync(uint height);

    /// <summary>The channels whose funding output was spent at or below <paramref name="height"/>.</summary>
    Task<IReadOnlyList<ShortChannelId>> GetChannelsSpentAtOrBelowAsync(uint height);

    /// <summary>Stages the deletion of a channel and its policies; false when the channel is unknown.</summary>
    Task<bool> DeleteChannelAsync(ShortChannelId shortChannelId);

    /// <summary>The stored policies of one channel (zero, one or two), by direction.</summary>
    Task<IReadOnlyList<GraphPolicyRecord>> GetPoliciesAsync(ShortChannelId shortChannelId);

    /// <summary>Every stored policy (startup load).</summary>
    Task<IReadOnlyList<GraphPolicyRecord>> GetAllPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages one direction's policy, replacing a stored one. The channel must be stored (or staged) too: the save
    /// fails otherwise (foreign key).
    /// </summary>
    Task UpsertPolicyAsync(GraphPolicyRecord policy);

    /// <summary>The ban of <paramref name="nodeId"/> (expired or not), or null.</summary>
    Task<GraphBannedNodeRecord?> GetBanAsync(CompactPubKey nodeId);

    /// <summary>The bans that last beyond <paramref name="now"/>.</summary>
    Task<IReadOnlyList<GraphBannedNodeRecord>> GetActiveBansAsync(DateTimeOffset now);

    /// <summary>Stages a ban, replacing a stored one.</summary>
    Task UpsertBanAsync(GraphBannedNodeRecord ban);

    /// <summary>Stages the deletion of every ban that ended at or before <paramref name="now"/>; returns how many.</summary>
    Task<int> DeleteExpiredBansAsync(DateTimeOffset now);

    #region Bulk (BOLT 7 plan G5-T3)

    /// <summary>
    /// Every stored channel, streamed (startup load): nothing is buffered, so the caller maps each row as it arrives.
    /// </summary>
    async IAsyncEnumerable<GraphChannelRecord> StreamChannelsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var channel in await GetChannelsAsync(cancellationToken))
            yield return channel;
    }

    /// <summary>Every stored policy, streamed (startup load).</summary>
    async IAsyncEnumerable<GraphPolicyRecord> StreamPoliciesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var policy in await GetAllPoliciesAsync(cancellationToken))
            yield return policy;
    }

    /// <summary>Every stored node announcement, streamed (startup load).</summary>
    async IAsyncEnumerable<GraphNodeRecord> StreamNodesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var node in await GetNodesAsync(cancellationToken))
            yield return node;
    }

    /// <summary>
    /// Stages every channel as <see cref="UpsertChannelAsync"/> does, reading the stored rows of the whole batch at
    /// once instead of one read per channel (the write-behind flush).
    /// </summary>
    async Task UpsertChannelsAsync(IReadOnlyCollection<GraphChannelRecord> channels,
                                   CancellationToken cancellationToken = default)
    {
        foreach (var channel in channels)
            await UpsertChannelAsync(channel);
    }

    /// <summary>Stages every policy as <see cref="UpsertPolicyAsync"/> does, in one read per batch.</summary>
    async Task UpsertPoliciesAsync(IReadOnlyCollection<GraphPolicyRecord> policies,
                                   CancellationToken cancellationToken = default)
    {
        foreach (var policy in policies)
            await UpsertPolicyAsync(policy);
    }

    /// <summary>Stages every node announcement as <see cref="UpsertNodeAsync"/> does, in one read per batch.</summary>
    async Task UpsertNodesAsync(IReadOnlyCollection<GraphNodeRecord> nodes,
                                CancellationToken cancellationToken = default)
    {
        foreach (var node in nodes)
            await UpsertNodeAsync(node);
    }

    /// <summary>
    /// Stages the deletion of every channel and its policies as <see cref="DeleteChannelAsync"/> does; returns how
    /// many were stored.
    /// </summary>
    async Task<int> DeleteChannelsAsync(IReadOnlyCollection<ShortChannelId> shortChannelIds,
                                        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var shortChannelId in shortChannelIds)
            if (await DeleteChannelAsync(shortChannelId))
                deleted++;

        return deleted;
    }

    /// <summary>Stages the deletion of every node announcement; returns how many were stored.</summary>
    async Task<int> DeleteNodesAsync(IReadOnlyCollection<CompactPubKey> nodeIds,
                                     CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var nodeId in nodeIds)
            if (await DeleteNodeAsync(nodeId))
                deleted++;

        return deleted;
    }

    #endregion
}