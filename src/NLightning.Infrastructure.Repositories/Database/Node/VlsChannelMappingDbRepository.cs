using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Channels.ValueObjects;
using Domain.Signing.Vls;
using Persistence.Contexts;
using Persistence.Entities.Node;

public sealed class VlsChannelMappingDbRepository(NLightningDbContext context) : IVlsChannelMappingDbRepository
{
    public async Task<VlsChannelMapping?> GetByKeyIndexAsync(uint keyIndex)
    {
        var entity = await context.VlsChannelMappings.AsNoTracking().SingleOrDefaultAsync(e => e.KeyIndex == keyIndex);
        return entity is null ? null : Map(entity);
    }
    public async Task<VlsChannelMapping?> GetByChannelIdAsync(ChannelId channelId)
    {
        var entity = await context.VlsChannelMappings.AsNoTracking().SingleOrDefaultAsync(e => e.ChannelId == channelId);
        return entity is null ? null : Map(entity);
    }
    public async Task<IReadOnlyList<VlsChannelMapping>> GetAllAsync() =>
        (await context.VlsChannelMappings.AsNoTracking().OrderBy(e => e.KeyIndex).ToListAsync()).Select(Map).ToArray();
    public async Task AddAsync(VlsChannelMapping mapping)
    {
        if (mapping.DbId == 0 || mapping.DbId > long.MaxValue || mapping.SignerIdentity.Length != 33
         || string.IsNullOrWhiteSpace(mapping.Network) || mapping.Network.Length > 64
         || mapping.AllocationRequestId == Guid.Empty || mapping.AllocationEnvelope.Length is 0 or > 1_048_576
         || mapping.AllocationResponse is not null || mapping.VlsChannelId is not null || mapping.ChannelId is not null)
            throw new ArgumentException("Invalid reserved VLS allocation.");
        if (await context.VlsChannelMappings.CountAsync()
          + context.ChangeTracker.Entries<VlsChannelMappingEntity>().Count(e => e.State == EntityState.Added) >= 65_536)
            throw new InvalidOperationException("VLS channel allocation capacity exhausted.");
        context.VlsChannelMappings.Add(new VlsChannelMappingEntity
        {
            KeyIndex = mapping.KeyIndex,
            DbId = mapping.DbId,
            PeerId = mapping.PeerId,
            SignerIdentity = mapping.SignerIdentity.ToArray(),
            Network = mapping.Network,
            AllocationRequestId = mapping.AllocationRequestId,
            AllocationEnvelope = mapping.AllocationEnvelope.ToArray(),
            CreatedAtTicks = mapping.CreatedAtTicks,
            UpdatedAtTicks = mapping.UpdatedAtTicks
        });
    }
    public async Task CompleteAllocationAsync(uint keyIndex, byte[] response, byte[] vlsChannelId)
    {
        if (response.Length is 0 or > 4_194_304 || vlsChannelId.Length != 41)
            throw new ArgumentException("Invalid VLS allocation result.");
        var entity = await RequireAsync(keyIndex);
        if (!vlsChannelId.AsSpan(0, 33).SequenceEqual((byte[])entity.PeerId)
         || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(vlsChannelId.AsSpan(33)) != entity.DbId)
            throw new InvalidOperationException("VLS allocation returned a different peer or database identity.");
        if (entity.AllocationResponse is not null
         && (!entity.AllocationResponse.AsSpan().SequenceEqual(response)
          || entity.VlsChannelId is null || !entity.VlsChannelId.AsSpan().SequenceEqual(vlsChannelId)))
            throw new InvalidOperationException("VLS allocation receipts are immutable.");
        entity.AllocationResponse = response.ToArray();
        entity.VlsChannelId = vlsChannelId.ToArray();
        entity.UpdatedAtTicks = DateTime.UtcNow.Ticks;
    }
    public async Task BindChannelAsync(uint keyIndex, ChannelId channelId)
    {
        var entity = await RequireAsync(keyIndex);
        if (channelId == ChannelId.Zero || entity.VlsChannelId is null || entity.AllocationResponse is null)
            throw new InvalidOperationException("Only a completed VLS allocation can bind a BOLT channel.");
        if (entity.ChannelId is { } existing && existing != channelId)
            throw new InvalidOperationException("A VLS allocation cannot be rebound to another BOLT channel.");
        entity.ChannelId = channelId;
        entity.UpdatedAtTicks = DateTime.UtcNow.Ticks;
    }
    private async Task<VlsChannelMappingEntity> RequireAsync(uint keyIndex) =>
        await context.VlsChannelMappings.FindAsync(keyIndex) ?? throw new KeyNotFoundException("VLS allocation not found.");
    private static VlsChannelMapping Map(VlsChannelMappingEntity e) => new(e.KeyIndex, e.DbId, e.PeerId,
        e.ChannelId, e.SignerIdentity.ToArray(), e.Network, e.AllocationRequestId, e.AllocationEnvelope.ToArray(),
        e.AllocationResponse?.ToArray(), e.VlsChannelId?.ToArray(), e.CreatedAtTicks, e.UpdatedAtTicks);
}