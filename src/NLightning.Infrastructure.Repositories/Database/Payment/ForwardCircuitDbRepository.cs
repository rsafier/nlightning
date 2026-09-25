using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores forward circuits (ONION M4-T7), keyed by the incoming (channel, HTLC id).
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. <see cref="GetByIncomingAsync"/> and <see cref="GetByOutgoingAsync"/> see
/// what this unit of work staged (they go through the change tracker); <see cref="GetUnresolvedAsync"/> reads what is
/// saved.
/// </remarks>
public class ForwardCircuitDbRepository : BaseDbRepository<ForwardCircuitEntity>, IForwardCircuitDbRepository
{
    public ForwardCircuitDbRepository(NLightningDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A circuit for the same incoming HTLC exists.</exception>
    public async Task AddAsync(ForwardCircuitModel circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (await DbSet.FindAsync(circuit.IncomingChannelId, circuit.IncomingHtlcId) is not null)
            throw new InvalidOperationException(
                $"A circuit for incoming HTLC {circuit.IncomingHtlcId} of channel {circuit.IncomingChannelId} exists");

        var entity = new ForwardCircuitEntity
        {
            IncomingChannelId = circuit.IncomingChannelId,
            IncomingHtlcId = circuit.IncomingHtlcId,
            IncomingAmountMsat = ToMsat(circuit.IncomingAmount),
            IncomingCltvExpiry = circuit.IncomingCltvExpiry,
            PaymentHash = circuit.PaymentHash,
            IncomingSharedSecret = ((byte[])circuit.IncomingSharedSecret).ToArray(),
            OutgoingShortChannelId = circuit.OutgoingShortChannelId,
            OutgoingAmountMsat = ToMsat(circuit.OutgoingAmount),
            OutgoingCltvExpiry = circuit.OutgoingCltvExpiry,
            CreatedAt = circuit.CreatedAt,
            Status = 0
        };
        MapMutableFields(circuit, entity);
        Insert(entity);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The circuit does not exist.</exception>
    public async Task UpdateAsync(ForwardCircuitModel circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var entity = await DbSet.FindAsync(circuit.IncomingChannelId, circuit.IncomingHtlcId)
                  ?? throw new InvalidOperationException(
                         $"No circuit for incoming HTLC {circuit.IncomingHtlcId} of channel {circuit.IncomingChannelId}");
        MapMutableFields(circuit, entity);
    }

    /// <inheritdoc />
    public async Task<ForwardCircuitModel?> GetByIncomingAsync(ChannelId incomingChannelId, ulong incomingHtlcId)
    {
        var entity = await DbSet.FindAsync(incomingChannelId, incomingHtlcId);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<ForwardCircuitModel?> GetByOutgoingAsync(ChannelId outgoingChannelId, ulong outgoingHtlcId)
    {
        // A circuit staged in this unit of work first (the query below only sees the saved outgoing columns)
        var entity = DbSet.Local.FirstOrDefault(e => e.OutgoingChannelId == outgoingChannelId
                                                  && e.OutgoingHtlcId == outgoingHtlcId
                                                  && DbSet.Entry(e).State != EntityState.Deleted);
        if (entity is null)
        {
            ChannelId? channelId = outgoingChannelId;
            ulong? htlcId = outgoingHtlcId;
            entity = await DbSet.Where(e => e.OutgoingChannelId == channelId && e.OutgoingHtlcId == htlcId)
                                .OrderBy(e => e.CreatedAt)
                                .FirstOrDefaultAsync();

            // A tracked row whose staged outgoing HTLC differs from the saved one is not a match
            if (entity is not null
             && (entity.OutgoingChannelId != outgoingChannelId || entity.OutgoingHtlcId != outgoingHtlcId))
                entity = null;
        }

        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ForwardCircuitModel>> GetUnresolvedAsync()
    {
        const byte pending = (byte)ForwardCircuitStatus.Pending;
        const byte offered = (byte)ForwardCircuitStatus.Offered;
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.Status == pending || e.Status == offered)
                                  .OrderBy(e => e.CreatedAt)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    internal static ForwardCircuitModel MapEntityToDomain(ForwardCircuitEntity entity)
    {
        return ForwardCircuitModel.Restore(entity.IncomingChannelId, entity.IncomingHtlcId,
                                           ToMoney(entity.IncomingAmountMsat), entity.IncomingCltvExpiry,
                                           entity.PaymentHash, new Secret(entity.IncomingSharedSecret),
                                           entity.OutgoingShortChannelId, ToMoney(entity.OutgoingAmountMsat),
                                           entity.OutgoingCltvExpiry, entity.CreatedAt,
                                           (ForwardCircuitStatus)entity.Status, entity.OutgoingChannelId,
                                           entity.OutgoingHtlcId, entity.ResolvedAt);
    }

    private static void MapMutableFields(ForwardCircuitModel circuit, ForwardCircuitEntity entity)
    {
        entity.Status = (byte)circuit.Status;
        entity.OutgoingChannelId = circuit.OutgoingChannelId;
        entity.OutgoingHtlcId = circuit.OutgoingHtlcId;
        entity.ResolvedAt = circuit.ResolvedAt;
    }

    private static long ToMsat(LightningMoney amount) => checked((long)amount.MilliSatoshi);

    private static LightningMoney ToMoney(long msat) => LightningMoney.MilliSatoshis(checked((ulong)msat));
}