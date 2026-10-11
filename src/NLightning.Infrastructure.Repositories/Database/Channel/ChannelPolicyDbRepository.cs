using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.Interfaces;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The per-channel routing policy overrides (table <c>ChannelPolicies</c>, migration <c>AddSpliceFundings</c>; wave sp1
/// lanes SP1-C and SP1-G). Writes are staged; <see cref="GetAsync"/> goes through the change tracker and sees what this
/// unit of work staged, <see cref="GetAllAsync"/> reads what is saved.
/// </summary>
public class ChannelPolicyDbRepository : IChannelPolicyDbRepository
{
    private readonly NLightningDbContext _context;
    private readonly TimeProvider _timeProvider;

    /// <param name="context">The unit of work's database context.</param>
    /// <param name="timeProvider">Stamps <see cref="ChannelPolicyOverride.UpdatedAt"/> when the caller left it
    /// default; <see cref="TimeProvider.System"/> when null.</param>
    public ChannelPolicyDbRepository(NLightningDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ChannelPolicyOverride?> GetAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelPolicies.FindAsync(channelId);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted ? null : MapToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelPolicyOverride>> GetAllAsync()
    {
        var entities = await _context.ChannelPolicies.AsNoTracking().ToListAsync();
        return entities.Select(MapToDomain).OrderBy(p => p.ChannelId.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(ChannelPolicyOverride policyOverride)
    {
        ArgumentNullException.ThrowIfNull(policyOverride);

        var updatedAt = policyOverride.UpdatedAt == default ? _timeProvider.GetUtcNow() : policyOverride.UpdatedAt;
        var entity = await _context.ChannelPolicies.FindAsync(policyOverride.ChannelId);
        if (entity is null)
        {
            _context.ChannelPolicies.Add(new ChannelPolicyEntity
            {
                ChannelId = policyOverride.ChannelId,
                FeeBaseMsat = policyOverride.FeeBaseMsat,
                FeeProportionalMillionths = policyOverride.FeeProportionalMillionths,
                CltvExpiryDelta = policyOverride.CltvExpiryDelta,
                HtlcMinimumMsat = policyOverride.HtlcMinimumMsat,
                HtlcMaximumMsat = policyOverride.HtlcMaximumMsat,
                UpdatedAt = updatedAt
            });
            return;
        }

        entity.FeeBaseMsat = policyOverride.FeeBaseMsat;
        entity.FeeProportionalMillionths = policyOverride.FeeProportionalMillionths;
        entity.CltvExpiryDelta = policyOverride.CltvExpiryDelta;
        entity.HtlcMinimumMsat = policyOverride.HtlcMinimumMsat;
        entity.HtlcMaximumMsat = policyOverride.HtlcMaximumMsat;
        entity.UpdatedAt = updatedAt;

        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelPolicies.FindAsync(channelId);
        if (entity is not null && _context.Entry(entity).State != EntityState.Deleted)
            _context.ChannelPolicies.Remove(entity);
    }

    private static ChannelPolicyOverride MapToDomain(ChannelPolicyEntity entity) =>
        new(entity.ChannelId, entity.FeeBaseMsat, entity.FeeProportionalMillionths, entity.CltvExpiryDelta,
            entity.HtlcMinimumMsat, entity.HtlcMaximumMsat, entity.UpdatedAt);
}