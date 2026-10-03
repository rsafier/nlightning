using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores trampoline relays and their incoming parts (NL-875, migration <c>AddTrampolineRelays</c>).
/// </summary>
/// <remarks>
/// Writes are staged on the unit of work. The lookups by key (<see cref="GetAsync"/>, <see cref="GetPartsAsync"/>,
/// <see cref="GetPartAsync"/>) see what this unit of work staged; <see cref="ListUnfinishedAsync"/> and
/// <see cref="ListAsync"/> read what is saved.
/// </remarks>
public class TrampolineRelayDbRepository : BaseDbRepository<TrampolineRelayEntity>, ITrampolineRelayDbRepository
{
    private readonly DbSet<TrampolineRelayPartEntity> _parts;

    public TrampolineRelayDbRepository(NLightningDbContext context) : base(context)
    {
        _parts = context.TrampolineRelayParts;
    }

    /// <inheritdoc />
    public async Task AddAsync(TrampolineRelayModel relay)
    {
        ArgumentNullException.ThrowIfNull(relay);

        if (await DbSet.FindAsync(relay.PaymentHash) is not null)
            throw new InvalidOperationException($"A trampoline relay for payment hash {relay.PaymentHash} exists");

        var entity = new TrampolineRelayEntity
        {
            PaymentHash = relay.PaymentHash,
            Status = (byte)relay.Status,
            NextNodeId = relay.NextNodeId,
            NextEncryptedRecipientData = relay.NextEncryptedRecipientData?.ToArray(),
            NextPathKey = relay.NextPathKey?.ToArray(),
            RecipientFeatures = relay.RecipientFeatures?.ToArray(),
            RecipientBlindedPaths = relay.RecipientBlindedPaths?.ToArray(),
            NextTrampolinePacket = relay.NextTrampolinePacket?.ToArray(),
            AmountOutMsat = ToMsat(relay.AmountOut),
            CltvExpiryOut = relay.CltvExpiryOut,
            IncomingTotalMsat = ToMsat(relay.IncomingTotal),
            CreatedAt = relay.CreatedAt
        };
        MapMutableFields(relay, entity);
        Insert(entity);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(TrampolineRelayModel relay)
    {
        ArgumentNullException.ThrowIfNull(relay);

        var entity = await DbSet.FindAsync(relay.PaymentHash)
                  ?? throw new InvalidOperationException($"No trampoline relay for payment hash {relay.PaymentHash}");
        MapMutableFields(relay, entity);
    }

    /// <inheritdoc />
    public async Task RemoveFailedAsync(Hash paymentHash)
    {
        var entity = await DbSet.FindAsync(paymentHash)
                  ?? throw new InvalidOperationException($"No trampoline relay for payment hash {paymentHash}");
        if (entity.Status != (byte)TrampolineRelayStatus.Failed)
            throw new InvalidOperationException($"The trampoline relay {paymentHash} is not failed");

        foreach (var part in await _parts.Where(p => p.PaymentHash == paymentHash).ToListAsync())
            _parts.Remove(part);
        foreach (var staged in _parts.Local.Where(p => p.PaymentHash == paymentHash
                                                    && _parts.Entry(p).State == EntityState.Added).ToList())
            _parts.Remove(staged);
        DbSet.Remove(entity);
    }

    /// <inheritdoc />
    public async Task AddPartAsync(TrampolineRelayPartModel part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (await DbSet.FindAsync(part.PaymentHash) is null)
            throw new InvalidOperationException($"No trampoline relay for payment hash {part.PaymentHash}");
        if (await _parts.FindAsync(part.ChannelId, part.HtlcId) is not null)
            throw new InvalidOperationException(
                $"Incoming HTLC {part.HtlcId} of channel {part.ChannelId} already is a trampoline relay part");

        _parts.Add(new TrampolineRelayPartEntity
        {
            ChannelId = part.ChannelId,
            HtlcId = part.HtlcId,
            PaymentHash = part.PaymentHash,
            AmountMsat = ToMsat(part.Amount),
            CltvExpiry = part.CltvExpiry,
            OuterSharedSecret = ((byte[])part.OuterSharedSecret).ToArray(),
            TrampolineSharedSecret = ((byte[])part.TrampolineSharedSecret).ToArray(),
            OuterPaymentSecret = part.OuterPaymentSecret?.ToArray()
        });
    }

    /// <inheritdoc />
    public async Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)?> GetAsync(
        Hash paymentHash)
    {
        var entity = await DbSet.FindAsync(paymentHash);
        if (entity is null)
            return null;

        return (MapEntityToDomain(entity), await GetPartsAsync(paymentHash));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrampolineRelayPartModel>> GetPartsAsync(Hash paymentHash)
    {
        // A tracking query sees the saved rows (with their staged changes); the rows this unit of work added are only
        // in the change tracker
        var saved = await _parts.Where(p => p.PaymentHash == paymentHash).ToListAsync();
        var staged = _parts.Local.Where(p => p.PaymentHash == paymentHash
                                          && _parts.Entry(p).State == EntityState.Added);
        return saved.Concat(staged)
                    .Where(p => _parts.Entry(p).State != EntityState.Deleted)
                    .DistinctBy(p => (p.ChannelId, p.HtlcId))
                    .OrderBy(p => p.ChannelId.ToString(), StringComparer.Ordinal)
                    .ThenBy(p => p.HtlcId)
                    .Select(MapPartToDomain)
                    .ToList();
    }

    /// <inheritdoc />
    public async Task<TrampolineRelayPartModel?> GetPartAsync(ChannelId channelId, ulong htlcId)
    {
        var entity = await _parts.FindAsync(channelId, htlcId);
        return entity is null ? null : MapPartToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrampolineRelayModel>> ListUnfinishedAsync()
    {
        const byte collecting = (byte)TrampolineRelayStatus.Collecting;
        const byte sending = (byte)TrampolineRelayStatus.Sending;
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.Status == collecting || e.Status == sending)
                                  .OrderBy(e => e.CreatedAt)
                                  .ToListAsync();

        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrampolineRelayModel>> ListAsync(TrampolineRelayListQuery query,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Take);
        if (query.Take == 0)
            return [];

        var set = DbSet.AsNoTracking();
        if (query.Since is { } since)
            set = set.Where(e => e.CreatedAt >= since);
        if (query.Until is { } until)
            set = set.Where(e => e.CreatedAt <= until);
        if (query.Status is { } status)
        {
            var statusByte = (byte)status;
            set = set.Where(e => e.Status == statusByte);
        }

        if (query.IncomingChannelId is { } channelId)
        {
            var parts = _parts.AsNoTracking();
            set = set.Where(e => parts.Any(p => p.PaymentHash == e.PaymentHash && p.ChannelId == channelId));
        }

        var entities = await set.OrderByDescending(e => e.CreatedAt)
                                .Skip(query.Skip)
                                .Take(query.Take)
                                .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    internal static TrampolineRelayModel MapEntityToDomain(TrampolineRelayEntity entity)
    {
        return TrampolineRelayModel.Restore(entity.PaymentHash, (TrampolineRelayStatus)entity.Status, entity.NextNodeId,
                                            entity.NextEncryptedRecipientData, entity.NextPathKey,
                                            entity.RecipientFeatures, entity.RecipientBlindedPaths,
                                            entity.NextTrampolinePacket, ToMoney(entity.AmountOutMsat),
                                            entity.CltvExpiryOut, ToMoney(entity.IncomingTotalMsat),
                                            entity.FeeEarnedMsat is { } fee ? ToMoney(fee) : null,
                                            entity.OutgoingPaymentSecret,
                                            entity.Preimage is { } preimage ? new Secret(preimage) : (Secret?)null,
                                            entity.FailureCode, entity.FailureReason, entity.CreatedAt,
                                            entity.CompletedAt);
    }

    private static TrampolineRelayPartModel MapPartToDomain(TrampolineRelayPartEntity entity)
    {
        return new TrampolineRelayPartModel(entity.PaymentHash, entity.ChannelId, entity.HtlcId,
                                            ToMoney(entity.AmountMsat), entity.CltvExpiry,
                                            new Secret(entity.OuterSharedSecret),
                                            new Secret(entity.TrampolineSharedSecret),
                                            entity.OuterPaymentSecret?.ToArray());
    }

    private static void MapMutableFields(TrampolineRelayModel relay, TrampolineRelayEntity entity)
    {
        entity.Status = (byte)relay.Status;
        entity.FeeEarnedMsat = relay.FeeEarned is { } fee ? ToMsat(fee) : null;
        entity.OutgoingPaymentSecret = relay.OutgoingPaymentSecret?.ToArray();
        entity.Preimage = relay.Preimage is { } preimage ? ((byte[])preimage).ToArray() : null;
        entity.FailureCode = relay.FailureCode;
        entity.FailureReason = relay.FailureReason;
        entity.CompletedAt = relay.CompletedAt;
    }

    private static long ToMsat(LightningMoney amount) => checked((long)amount.MilliSatoshi);

    private static LightningMoney ToMoney(long msat) => LightningMoney.MilliSatoshis(checked((ulong)msat));
}