using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace NLightning.Infrastructure.Repositories.Database.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Persistence;
using Persistence.Contexts;
using Persistence.Entities.Gossip;

/// <summary>
/// The persisted BOLT 7 graph (migration <c>AddGossipGraph</c>, BOLT 7 plan G2-T3): nodes, channels, per-direction
/// policies and bans, each with its raw signed bytes.
/// </summary>
/// <remarks>
/// Writes go through tracked entities and are staged on the unit of work (never <c>ExecuteUpdate</c>/
/// <c>ExecuteDelete</c>), so they commit or roll back with the rest of it. Single-key reads use the change tracker; list
/// reads read what is saved. The bulk writes (G5-T3) read the stored rows of up to <see cref="KeysPerQuery"/> keys in
/// one query; the startup load streams the tables without tracking.
/// </remarks>
public class GraphDbRepository : IGraphDbRepository
{
    /// <summary>
    /// The keys one bulk read asks for (EF 10 sends each as a scalar parameter; SQL Server allows 2,100 per command).
    /// </summary>
    internal const int KeysPerQuery = 500;

    private readonly NLightningDbContext _context;

    public GraphDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    #region Nodes

    /// <inheritdoc />
    public async Task<GraphNodeRecord?> GetNodeAsync(CompactPubKey nodeId)
    {
        var entity = await FindAsync(_context.GraphNodes, nodeId);
        return entity is null ? null : MapNode(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNodeRecord>> GetNodesAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.GraphNodes.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapNode).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertNodeAsync(GraphNodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var entity = await FindForUpsertAsync(_context.GraphNodes, node.NodeId);
        if (entity is null)
            _context.GraphNodes.Add(NewNode(node));
        else
            Apply(entity, node);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteNodeAsync(CompactPubKey nodeId)
    {
        var entity = await FindAsync(_context.GraphNodes, nodeId);
        if (entity is null)
            return false;

        _context.GraphNodes.Remove(entity);
        return true;
    }

    #endregion

    #region Channels

    /// <inheritdoc />
    public async Task<GraphChannelRecord?> GetChannelAsync(ShortChannelId shortChannelId)
    {
        var entity = await FindAsync(_context.GraphChannels, shortChannelId);
        return entity is null ? null : MapChannel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphChannelRecord>> GetChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.GraphChannels.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapChannel).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertChannelAsync(GraphChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var entity = await FindForUpsertAsync(_context.GraphChannels, channel.ShortChannelId);
        if (entity is null)
            _context.GraphChannels.Add(NewChannel(channel));
        else
            Apply(entity, channel);
    }

    /// <inheritdoc />
    public async Task<bool> MarkChannelSpentAsync(ShortChannelId shortChannelId, uint height)
    {
        var entity = await FindAsync(_context.GraphChannels, shortChannelId);
        if (entity is null)
            return false;

        entity.SpentAtHeight = height;
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ClearSpentAboveAsync(uint height)
    {
        // A tracking query returns the already tracked instances, so staged spends are seen too
        var entities = await _context.GraphChannels.Where(c => c.SpentAtHeight > height).ToListAsync();
        var staged = _context.GraphChannels.Local.Where(c => c.SpentAtHeight > height).Except(entities).ToList();
        foreach (var entity in entities.Concat(staged))
            entity.SpentAtHeight = null;

        return entities.Count + staged.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShortChannelId>> GetChannelsSpentAtOrBelowAsync(uint height)
    {
        return await _context.GraphChannels.AsNoTracking()
                             .Where(c => c.SpentAtHeight <= height)
                             .Select(c => c.ShortChannelId)
                             .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteChannelAsync(ShortChannelId shortChannelId)
    {
        var entity = await FindAsync(_context.GraphChannels, shortChannelId);
        if (entity is null)
            return false;

        // The database cascades too, but tracked policies must not be written back after the channel is gone
        var policies = await _context.GraphChannelPolicies.Where(p => p.ShortChannelId == shortChannelId)
                                     .ToListAsync();
        policies.AddRange(_context.GraphChannelPolicies.Local
                                  .Where(p => p.ShortChannelId == shortChannelId)
                                  .Except(policies)
                                  .ToList());
        _context.GraphChannelPolicies.RemoveRange(policies);
        _context.GraphChannels.Remove(entity);
        return true;
    }

    #endregion

    #region Policies

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphPolicyRecord>> GetPoliciesAsync(ShortChannelId shortChannelId)
    {
        var entities = await _context.GraphChannelPolicies.AsNoTracking()
                                     .Where(p => p.ShortChannelId == shortChannelId)
                                     .ToListAsync();
        return entities.OrderBy(p => p.Direction).Select(MapPolicy).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphPolicyRecord>> GetAllPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.GraphChannelPolicies.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapPolicy).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertPolicyAsync(GraphPolicyRecord policy)
    {
        ThrowIfBadDirection(policy);
        var entity = await FindForUpsertAsync(_context.GraphChannelPolicies, policy.ShortChannelId, policy.Direction);
        if (entity is null)
            _context.GraphChannelPolicies.Add(NewPolicy(policy));
        else
            Apply(entity, policy);
    }

    #endregion

    #region Bans

    /// <inheritdoc />
    public async Task<GraphBannedNodeRecord?> GetBanAsync(CompactPubKey nodeId)
    {
        var entity = await FindAsync(_context.GraphBannedNodes, nodeId);
        return entity is null ? null : MapBan(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphBannedNodeRecord>> GetActiveBansAsync(DateTimeOffset now)
    {
        var entities = await _context.GraphBannedNodes.AsNoTracking()
                                     .Where(b => b.Until > now)
                                     .ToListAsync();
        return entities.Select(MapBan).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertBanAsync(GraphBannedNodeRecord ban)
    {
        ArgumentNullException.ThrowIfNull(ban);
        var entity = await FindForUpsertAsync(_context.GraphBannedNodes, ban.NodeId);
        if (entity is null)
        {
            _context.GraphBannedNodes.Add(new GraphBannedNodeEntity
            {
                NodeId = ban.NodeId,
                Reason = ban.Reason,
                Until = ban.Until
            });
            return;
        }

        entity.Reason = ban.Reason;
        entity.Until = ban.Until;
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredBansAsync(DateTimeOffset now)
    {
        var entities = await _context.GraphBannedNodes.Where(b => b.Until <= now).ToListAsync();
        _context.GraphBannedNodes.RemoveRange(entities);
        return entities.Count;
    }

    #endregion

    #region Bulk (BOLT 7 plan G5-T3)

    /// <inheritdoc />
    public async IAsyncEnumerable<GraphChannelRecord> StreamChannelsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entity in _context.GraphChannels.AsNoTracking().AsAsyncEnumerable()
                                             .WithCancellation(cancellationToken))
            yield return MapChannel(entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GraphPolicyRecord> StreamPoliciesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entity in _context.GraphChannelPolicies.AsNoTracking().AsAsyncEnumerable()
                                             .WithCancellation(cancellationToken))
            yield return MapPolicy(entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GraphNodeRecord> StreamNodesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entity in _context.GraphNodes.AsNoTracking().AsAsyncEnumerable()
                                             .WithCancellation(cancellationToken))
            yield return MapNode(entity);
    }

    /// <inheritdoc />
    public async Task UpsertChannelsAsync(IReadOnlyCollection<GraphChannelRecord> channels,
                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        foreach (var chunk in channels.Chunk(KeysPerQuery))
        {
            var stored = await LoadForUpsertAsync(_context.GraphChannels, chunk.Select(c => c.ShortChannelId),
                                                  e => e.ShortChannelId,
                                                  keys => e => keys.Contains(e.ShortChannelId), cancellationToken);
            foreach (var channel in chunk)
            {
                if (stored.TryGetValue(channel.ShortChannelId, out var entity))
                {
                    Apply(entity, channel);
                    continue;
                }

                var added = NewChannel(channel);
                _context.GraphChannels.Add(added);
                stored[channel.ShortChannelId] = added;
            }
        }
    }

    /// <inheritdoc />
    public async Task UpsertPoliciesAsync(IReadOnlyCollection<GraphPolicyRecord> policies,
                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var policy in policies)
            ThrowIfBadDirection(policy);

        foreach (var chunk in policies.Chunk(KeysPerQuery))
        {
            // Tracked rows first (staged, or deleted by this unit of work: revived), then one read for the batch; a
            // tracking query returns the tracked instance of a row it reads, so a staged value is never lost
            var stored = new Dictionary<(ShortChannelId, byte), GraphChannelPolicyEntity>();
            foreach (var entry in _context.ChangeTracker.Entries<GraphChannelPolicyEntity>())
                stored[(entry.Entity.ShortChannelId, entry.Entity.Direction)] = entry.Entity;

            var shortChannelIds = chunk.Select(p => p.ShortChannelId)
                                       .Where(s => !stored.ContainsKey((s, 0)) || !stored.ContainsKey((s, 1)))
                                       .Distinct()
                                       .ToList();
            if (shortChannelIds.Count > 0)
            {
                var read = await _context.GraphChannelPolicies
                                         .Where(p => shortChannelIds.Contains(p.ShortChannelId))
                                         .ToListAsync(cancellationToken);
                foreach (var entity in read)
                    stored.TryAdd((entity.ShortChannelId, entity.Direction), entity);
            }

            foreach (var policy in chunk)
            {
                if (stored.TryGetValue((policy.ShortChannelId, policy.Direction), out var entity))
                {
                    Revive(entity);
                    Apply(entity, policy);
                    continue;
                }

                var added = NewPolicy(policy);
                _context.GraphChannelPolicies.Add(added);
                stored[(policy.ShortChannelId, policy.Direction)] = added;
            }
        }
    }

    /// <inheritdoc />
    public async Task UpsertNodesAsync(IReadOnlyCollection<GraphNodeRecord> nodes,
                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var chunk in nodes.Chunk(KeysPerQuery))
        {
            var stored = await LoadForUpsertAsync(_context.GraphNodes, chunk.Select(n => n.NodeId), e => e.NodeId,
                                                  keys => e => keys.Contains(e.NodeId), cancellationToken);
            foreach (var node in chunk)
            {
                if (stored.TryGetValue(node.NodeId, out var entity))
                {
                    Apply(entity, node);
                    continue;
                }

                var added = NewNode(node);
                _context.GraphNodes.Add(added);
                stored[node.NodeId] = added;
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteChannelsAsync(IReadOnlyCollection<ShortChannelId> shortChannelIds,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shortChannelIds);
        var deleted = 0;
        foreach (var chunk in shortChannelIds.Distinct().Chunk(KeysPerQuery))
        {
            var channels = await LoadExistingAsync(_context.GraphChannels, chunk, e => e.ShortChannelId,
                                                   keys => e => keys.Contains(e.ShortChannelId), cancellationToken);
            if (channels.Count == 0)
                continue;

            // The database cascades too, but tracked policies must not be written back after the channel is gone
            var channelKeys = channels.Select(c => c.ShortChannelId).ToList();
            var policies = await _context.GraphChannelPolicies.Where(p => channelKeys.Contains(p.ShortChannelId))
                                         .ToListAsync(cancellationToken);
            var keySet = channelKeys.ToHashSet();
            policies.AddRange(_context.GraphChannelPolicies.Local
                                      .Where(p => keySet.Contains(p.ShortChannelId))
                                      .Except(policies)
                                      .ToList());
            _context.GraphChannelPolicies.RemoveRange(policies);
            _context.GraphChannels.RemoveRange(channels);
            deleted += channels.Count;
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteNodesAsync(IReadOnlyCollection<CompactPubKey> nodeIds,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var deleted = 0;
        foreach (var chunk in nodeIds.Distinct().Chunk(KeysPerQuery))
        {
            var nodes = await LoadExistingAsync(_context.GraphNodes, chunk, e => e.NodeId,
                                                keys => e => keys.Contains(e.NodeId), cancellationToken);
            _context.GraphNodes.RemoveRange(nodes);
            deleted += nodes.Count;
        }

        return deleted;
    }

    #endregion

    /// <summary>The row with this key, staged or saved, unless this unit of work already deleted it.</summary>
    private async Task<TEntity?> FindAsync<TEntity>(DbSet<TEntity> set, params object[] keyValues)
        where TEntity : class
    {
        var entity = await set.FindAsync(keyValues);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted ? null : entity;
    }

    /// <summary>
    /// The row to overwrite: staged or saved; a row this unit of work deleted is revived (the key is still tracked, so
    /// adding a new instance would fail).
    /// </summary>
    private async Task<TEntity?> FindForUpsertAsync<TEntity>(DbSet<TEntity> set, params object[] keyValues)
        where TEntity : class
    {
        var entity = await set.FindAsync(keyValues);
        if (entity is not null && _context.Entry(entity).State == EntityState.Deleted)
            _context.Entry(entity).State = EntityState.Modified;

        return entity;
    }

    /// <summary>
    /// The rows of <paramref name="keys"/> to overwrite, staged or saved, in one read: tracked rows first (a row this
    /// unit of work deleted is revived, as <see cref="FindForUpsertAsync{TEntity}"/> does), then the others from the
    /// database.
    /// </summary>
    private async Task<Dictionary<TKey, TEntity>> LoadForUpsertAsync<TEntity, TKey>(
        DbSet<TEntity> set, IEnumerable<TKey> keys, Func<TEntity, TKey> keyOf,
        Func<List<TKey>, Expression<Func<TEntity, bool>>> whereKeyIn, CancellationToken cancellationToken)
        where TEntity : class where TKey : notnull
    {
        var tracked = new Dictionary<TKey, EntityEntry<TEntity>>();
        foreach (var entry in _context.ChangeTracker.Entries<TEntity>())
            tracked[keyOf(entry.Entity)] = entry;

        var stored = new Dictionary<TKey, TEntity>();
        var missing = new List<TKey>();
        foreach (var key in keys.Distinct())
        {
            if (!tracked.TryGetValue(key, out var entry))
            {
                missing.Add(key);
                continue;
            }

            if (entry.State == EntityState.Deleted)
                entry.State = EntityState.Modified;
            stored[key] = entry.Entity;
        }

        if (missing.Count > 0)
            foreach (var entity in await set.Where(whereKeyIn(missing)).ToListAsync(cancellationToken))
                stored[keyOf(entity)] = entity;

        return stored;
    }

    /// <summary>The rows of <paramref name="keys"/>, staged or saved, that this unit of work did not delete.</summary>
    private async Task<List<TEntity>> LoadExistingAsync<TEntity, TKey>(
        DbSet<TEntity> set, IEnumerable<TKey> keys, Func<TEntity, TKey> keyOf,
        Func<List<TKey>, Expression<Func<TEntity, bool>>> whereKeyIn, CancellationToken cancellationToken)
        where TEntity : class where TKey : notnull
    {
        var tracked = new Dictionary<TKey, EntityEntry<TEntity>>();
        foreach (var entry in _context.ChangeTracker.Entries<TEntity>())
            tracked[keyOf(entry.Entity)] = entry;

        var found = new List<TEntity>();
        var missing = new List<TKey>();
        foreach (var key in keys)
        {
            if (!tracked.TryGetValue(key, out var entry))
                missing.Add(key);
            else if (entry.State != EntityState.Deleted)
                found.Add(entry.Entity);
        }

        if (missing.Count > 0)
            found.AddRange(await set.Where(whereKeyIn(missing)).ToListAsync(cancellationToken));

        return found;
    }

    private void Revive<TEntity>(TEntity entity) where TEntity : class
    {
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }

    private static void ThrowIfBadDirection(GraphPolicyRecord policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Direction > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Direction, "The direction is 0 or 1");
    }

    private static GraphNodeEntity NewNode(GraphNodeRecord node) =>
        new()
        {
            NodeId = node.NodeId,
            Timestamp = node.Timestamp,
            Features = node.Features.ToArray(),
            Alias = node.Alias.ToArray(),
            Color = node.Color.ToArray(),
            Addresses = node.Addresses.ToArray(),
            RawAnnouncement = node.RawAnnouncement.ToArray(),
            ReceivedAt = node.ReceivedAt
        };

    private static void Apply(GraphNodeEntity entity, GraphNodeRecord node)
    {
        entity.Timestamp = node.Timestamp;
        entity.Features = node.Features.ToArray();
        entity.Alias = node.Alias.ToArray();
        entity.Color = node.Color.ToArray();
        entity.Addresses = node.Addresses.ToArray();
        entity.RawAnnouncement = node.RawAnnouncement.ToArray();
        entity.ReceivedAt = node.ReceivedAt;
    }

    private static GraphChannelEntity NewChannel(GraphChannelRecord channel) =>
        new()
        {
            ShortChannelId = channel.ShortChannelId,
            NodeId1 = channel.NodeId1,
            NodeId2 = channel.NodeId2,
            BitcoinKey1 = channel.BitcoinKey1,
            BitcoinKey2 = channel.BitcoinKey2,
            CapacitySat = checked((long)channel.CapacitySat),
            Features = channel.Features.ToArray(),
            RawAnnouncement = channel.RawAnnouncement.ToArray(),
            Verification = (byte)channel.Verification,
            SpentAtHeight = channel.SpentAtHeight,
            FundingTxId = channel.FundingTxId,
            ReceivedAt = channel.ReceivedAt
        };

    private static void Apply(GraphChannelEntity entity, GraphChannelRecord channel)
    {
        entity.NodeId1 = channel.NodeId1;
        entity.NodeId2 = channel.NodeId2;
        entity.BitcoinKey1 = channel.BitcoinKey1;
        entity.BitcoinKey2 = channel.BitcoinKey2;
        entity.CapacitySat = checked((long)channel.CapacitySat);
        entity.Features = channel.Features.ToArray();
        entity.RawAnnouncement = channel.RawAnnouncement.ToArray();
        entity.Verification = (byte)channel.Verification;
        entity.SpentAtHeight = channel.SpentAtHeight;
        entity.FundingTxId = channel.FundingTxId;
        entity.ReceivedAt = channel.ReceivedAt;
    }

    private static GraphChannelPolicyEntity NewPolicy(GraphPolicyRecord policy) =>
        new()
        {
            ShortChannelId = policy.ShortChannelId,
            Direction = policy.Direction,
            Timestamp = policy.Timestamp,
            MessageFlags = policy.MessageFlags,
            ChannelFlags = policy.ChannelFlags,
            CltvExpiryDelta = policy.CltvExpiryDelta,
            HtlcMinimumMsat = policy.HtlcMinimumMsat,
            HtlcMaximumMsat = policy.HtlcMaximumMsat,
            FeeBaseMsat = policy.FeeBaseMsat,
            FeePpm = policy.FeeProportionalMillionths,
            RawUpdate = policy.RawUpdate.ToArray()
        };

    private static void Apply(GraphChannelPolicyEntity entity, GraphPolicyRecord policy)
    {
        entity.Timestamp = policy.Timestamp;
        entity.MessageFlags = policy.MessageFlags;
        entity.ChannelFlags = policy.ChannelFlags;
        entity.CltvExpiryDelta = policy.CltvExpiryDelta;
        entity.HtlcMinimumMsat = policy.HtlcMinimumMsat;
        entity.HtlcMaximumMsat = policy.HtlcMaximumMsat;
        entity.FeeBaseMsat = policy.FeeBaseMsat;
        entity.FeePpm = policy.FeeProportionalMillionths;
        entity.RawUpdate = policy.RawUpdate.ToArray();
    }

    private static GraphNodeRecord MapNode(GraphNodeEntity entity) =>
        new(entity.NodeId, entity.Timestamp, entity.Features, entity.Alias, entity.Color, entity.Addresses,
            entity.RawAnnouncement, entity.ReceivedAt);

    private static GraphChannelRecord MapChannel(GraphChannelEntity entity) =>
        new(entity.ShortChannelId, entity.NodeId1, entity.NodeId2, entity.BitcoinKey1, entity.BitcoinKey2,
            checked((ulong)entity.CapacitySat), entity.Features, entity.RawAnnouncement,
            (GraphChannelVerification)entity.Verification, entity.SpentAtHeight, entity.ReceivedAt,
            entity.FundingTxId);

    private static GraphPolicyRecord MapPolicy(GraphChannelPolicyEntity entity) =>
        new(entity.ShortChannelId, entity.Direction, entity.Timestamp, entity.MessageFlags, entity.ChannelFlags,
            entity.CltvExpiryDelta, entity.HtlcMinimumMsat, entity.HtlcMaximumMsat, entity.FeeBaseMsat, entity.FeePpm,
            entity.RawUpdate);

    private static GraphBannedNodeRecord MapBan(GraphBannedNodeEntity entity) =>
        new(entity.NodeId, entity.Reason, entity.Until);
}