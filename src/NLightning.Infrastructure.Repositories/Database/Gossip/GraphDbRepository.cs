using Microsoft.EntityFrameworkCore;

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
/// reads read what is saved.
/// </remarks>
public class GraphDbRepository : IGraphDbRepository
{
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
        {
            _context.GraphNodes.Add(new GraphNodeEntity
            {
                NodeId = node.NodeId,
                Timestamp = node.Timestamp,
                Features = node.Features.ToArray(),
                Alias = node.Alias.ToArray(),
                Color = node.Color.ToArray(),
                Addresses = node.Addresses.ToArray(),
                RawAnnouncement = node.RawAnnouncement.ToArray(),
                ReceivedAt = node.ReceivedAt
            });
            return;
        }

        entity.Timestamp = node.Timestamp;
        entity.Features = node.Features.ToArray();
        entity.Alias = node.Alias.ToArray();
        entity.Color = node.Color.ToArray();
        entity.Addresses = node.Addresses.ToArray();
        entity.RawAnnouncement = node.RawAnnouncement.ToArray();
        entity.ReceivedAt = node.ReceivedAt;
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
        var capacitySat = checked((long)channel.CapacitySat);
        var entity = await FindForUpsertAsync(_context.GraphChannels, channel.ShortChannelId);
        if (entity is null)
        {
            _context.GraphChannels.Add(new GraphChannelEntity
            {
                ShortChannelId = channel.ShortChannelId,
                NodeId1 = channel.NodeId1,
                NodeId2 = channel.NodeId2,
                BitcoinKey1 = channel.BitcoinKey1,
                BitcoinKey2 = channel.BitcoinKey2,
                CapacitySat = capacitySat,
                Features = channel.Features.ToArray(),
                RawAnnouncement = channel.RawAnnouncement.ToArray(),
                Verification = (byte)channel.Verification,
                SpentAtHeight = channel.SpentAtHeight,
                ReceivedAt = channel.ReceivedAt
            });
            return;
        }

        entity.NodeId1 = channel.NodeId1;
        entity.NodeId2 = channel.NodeId2;
        entity.BitcoinKey1 = channel.BitcoinKey1;
        entity.BitcoinKey2 = channel.BitcoinKey2;
        entity.CapacitySat = capacitySat;
        entity.Features = channel.Features.ToArray();
        entity.RawAnnouncement = channel.RawAnnouncement.ToArray();
        entity.Verification = (byte)channel.Verification;
        entity.SpentAtHeight = channel.SpentAtHeight;
        entity.ReceivedAt = channel.ReceivedAt;
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
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Direction > 1)
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Direction, "The direction is 0 or 1");

        var entity = await FindForUpsertAsync(_context.GraphChannelPolicies, policy.ShortChannelId, policy.Direction);
        if (entity is null)
        {
            _context.GraphChannelPolicies.Add(new GraphChannelPolicyEntity
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
            });
            return;
        }

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

    private static GraphNodeRecord MapNode(GraphNodeEntity entity) =>
        new(entity.NodeId, entity.Timestamp, entity.Features, entity.Alias, entity.Color, entity.Addresses,
            entity.RawAnnouncement, entity.ReceivedAt);

    private static GraphChannelRecord MapChannel(GraphChannelEntity entity) =>
        new(entity.ShortChannelId, entity.NodeId1, entity.NodeId2, entity.BitcoinKey1, entity.BitcoinKey2,
            checked((ulong)entity.CapacitySat), entity.Features, entity.RawAnnouncement,
            (GraphChannelVerification)entity.Verification, entity.SpentAtHeight, entity.ReceivedAt);

    private static GraphPolicyRecord MapPolicy(GraphChannelPolicyEntity entity) =>
        new(entity.ShortChannelId, entity.Direction, entity.Timestamp, entity.MessageFlags, entity.ChannelFlags,
            entity.CltvExpiryDelta, entity.HtlcMinimumMsat, entity.HtlcMaximumMsat, entity.FeeBaseMsat, entity.FeePpm,
            entity.RawUpdate);

    private static GraphBannedNodeRecord MapBan(GraphBannedNodeEntity entity) =>
        new(entity.NodeId, entity.Reason, entity.Until);
}