using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The cost-basis lots and their reliefs (<c>AccountingLots</c>, <c>AccountingLotReliefs</c>, NL-602 A3-T4; migration
/// <c>AddAccountingFinancial</c>). Writes are staged on the unit of work and committed by its save, so the financial
/// projector saves its lots and reliefs with its entries and its cursor.
/// </summary>
/// <remarks>
/// <para>Lot ids are assigned here (one past the highest saved or staged), never by the database, so a relief can name
/// a lot opened in the same save. The financial projector is the only writer.</para>
/// <para>The fiat columns are <c>decimal</c>, which SQLite stores as TEXT: nothing here orders or sums them in SQL
/// (HIFO orders the open lots in memory).</para>
/// <para><see cref="ClearAsync"/> deletes at once (bulk deletes, which the change tracker does not see), like
/// <see cref="AccountingBooksDbRepository.ClearAsync(AccountingBook, CancellationToken)"/>.</para>
/// </remarks>
public class AccountingLotDbRepository : IAccountingLotDbRepository
{
    private readonly NLightningDbContext _context;

    public AccountingLotDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task<long> AddLotAsync(AccountingLot lot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lot);
        Validate(lot);

        var id = lot.Id;
        if (id == 0)
        {
            var savedMax = await _context.AccountingLots.AsNoTracking()
                                         .MaxAsync(l => (long?)l.Id, cancellationToken) ?? 0;
            var stagedMax = _context.AccountingLots.Local.Select(l => l.Id).DefaultIfEmpty(0).Max();
            id = Math.Max(savedMax, stagedMax) + 1;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(id);
            if (await FindTrackedAsync(id, cancellationToken) is not null)
                throw new InvalidOperationException($"Lot {id} exists");
        }

        _context.AccountingLots.Add(new AccountingLotEntity
        {
            Id = id,
            AcquiredAt = lot.AcquiredAt,
            Origin = (byte)lot.Origin,
            SourceLedgerSeq = lot.SourceLedgerSeq,
            SourceAdjustment = lot.SourceAdjustment,
            Account = lot.Account is { } account ? (int)account : null,
            ParentLotId = lot.ParentLotId,
            OriginalMsat = lot.OriginalMsat,
            RemainingMsat = lot.RemainingMsat,
            FiatCost = lot.FiatCost,
            FiatCurrency = lot.FiatCurrency,
            PriceId = lot.PriceId,
            BasisEstimated = lot.BasisEstimated,
            ClosedPeriodId = lot.ClosedPeriodId
        });
        return id;
    }

    /// <inheritdoc />
    public async Task UpdateLotAsync(AccountingLot lot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lot);
        Validate(lot);

        var entity = await FindTrackedAsync(lot.Id, cancellationToken)
                  ?? throw new InvalidOperationException($"Lot {lot.Id} does not exist");

        entity.RemainingMsat = lot.RemainingMsat;
        entity.FiatCost = lot.FiatCost;
        entity.FiatCurrency = lot.FiatCurrency;
        entity.PriceId = lot.PriceId;
        entity.Account = lot.Account is { } account ? (int)account : null;
        entity.ClosedPeriodId = lot.ClosedPeriodId;
    }

    /// <inheritdoc />
    public async Task<AccountingLot?> GetLotAsync(long id, CancellationToken cancellationToken = default)
    {
        var staged = _context.AccountingLots.Local.FirstOrDefault(l => l.Id == id);
        if (staged is not null)
            return MapEntityToDomain(staged);

        var entity = await _context.AccountingLots.AsNoTracking()
                                   .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLot>> ListOpenLotsAsync(AccountRole? account = null,
                                                                      CancellationToken cancellationToken = default)
    {
        int? accountValue = account is { } role ? (int)role : null;

        var query = _context.AccountingLots.AsNoTracking().Where(l => l.RemainingMsat > 0);
        if (accountValue is not null)
            query = query.Where(l => l.Account == accountValue);

        var lots = (await query.ToListAsync(cancellationToken)).ToDictionary(l => l.Id);

        // What this unit of work staged (new lots, changed remainders) wins over what is saved
        foreach (var tracked in _context.AccountingLots.Local)
        {
            if (tracked.RemainingMsat > 0 && (accountValue is null || tracked.Account == accountValue))
                lots[tracked.Id] = tracked;
            else
                lots.Remove(tracked.Id);
        }

        return lots.Values
                   .OrderBy(l => l.AcquiredAt)
                   .ThenBy(l => l.Id)
                   .Select(MapEntityToDomain)
                   .ToList();
    }

    /// <inheritdoc />
    public void AddRelief(AccountingLotRelief relief)
    {
        ArgumentNullException.ThrowIfNull(relief);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relief.LotId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relief.LedgerSeq);
        ArgumentOutOfRangeException.ThrowIfNegative(relief.Adjustment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relief.Msat);
        ValidatePeriodId(relief.ClosedPeriodId);

        _context.AccountingLotReliefs.Add(new AccountingLotReliefEntity
        {
            LotId = relief.LotId,
            LedgerSeq = relief.LedgerSeq,
            Adjustment = relief.Adjustment,
            RelievedAt = relief.RelievedAt,
            Msat = relief.Msat,
            FiatCostRelieved = relief.FiatCostRelieved,
            Proceeds = relief.Proceeds,
            ClosedPeriodId = relief.ClosedPeriodId
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLotRelief>> ListReliefsByLotAsync(
        long lotId, CancellationToken cancellationToken = default)
    {
        var entities = await _context.AccountingLotReliefs.AsNoTracking()
                                     .Where(r => r.LotId == lotId)
                                     .OrderBy(r => r.RelievedAt)
                                     .ThenBy(r => r.Id)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLotRelief>> ListReliefsByEntryAsync(
        long ledgerSeq, int adjustment, CancellationToken cancellationToken = default)
    {
        var entities = await _context.AccountingLotReliefs.AsNoTracking()
                                     .Where(r => r.LedgerSeq == ledgerSeq && r.Adjustment == adjustment)
                                     .OrderBy(r => r.Id)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLotRelief>> ListReliefsAsync(
        DateTimeOffset? since, DateTimeOffset? until, long afterId, int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var reliefs = _context.AccountingLotReliefs.AsNoTracking().Where(r => r.Id > afterId);
        if (since is { } from)
            reliefs = reliefs.Where(r => r.RelievedAt >= from);
        if (until is { } to)
            reliefs = reliefs.Where(r => r.RelievedAt < to);

        var entities = await reliefs.OrderBy(r => r.Id).Take(take).ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<int> MarkClosedAsync(string periodId, DateTimeOffset end,
                                           CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodId);
        ValidatePeriodId(periodId);

        var lots = await _context.AccountingLots
                                 .Where(l => l.ClosedPeriodId == null && l.AcquiredAt < end)
                                 .ToListAsync(cancellationToken);
        foreach (var lot in lots)
            lot.ClosedPeriodId = periodId;

        var reliefs = await _context.AccountingLotReliefs
                                    .Where(r => r.ClosedPeriodId == null && r.RelievedAt < end)
                                    .ToListAsync(cancellationToken);
        foreach (var relief in reliefs)
            relief.ClosedPeriodId = periodId;

        return lots.Count + reliefs.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLot>> ListLotsAcquiredBeforeAsync(
        DateTimeOffset end, long afterId, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var entities = await _context.AccountingLots.AsNoTracking()
                                     .Where(l => l.AcquiredAt < end && l.Id > afterId)
                                     .OrderBy(l => l.Id)
                                     .Take(take)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLotRelief>> ListPeriodReliefsAsync(
        string? periodId, DateTimeOffset end, long afterId, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var reliefs = _context.AccountingLotReliefs.AsNoTracking().Where(r => r.Id > afterId);
        reliefs = periodId is null
                      ? reliefs.Where(r => r.ClosedPeriodId == null && r.RelievedAt < end)
                      : reliefs.Where(r => r.ClosedPeriodId == periodId);
        var entities = await reliefs.OrderBy(r => r.Id).Take(take).ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, long>> SumReliefsSinceAsync(
        DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var sums = await _context.AccountingLotReliefs.AsNoTracking()
                                 .Where(r => r.RelievedAt >= since)
                                 .GroupBy(r => r.LotId)
                                 .Select(g => new { LotId = g.Key, Msat = g.Sum(r => r.Msat) })
                                 .ToListAsync(cancellationToken);
        return sums.ToDictionary(s => s.LotId, s => s.Msat);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingLot>> ListLotsByOriginAsync(AccountingLotOrigin origin,
                                                                          CancellationToken cancellationToken = default)
    {
        var value = (byte)origin;
        var entities = await _context.AccountingLots.AsNoTracking()
                                     .Where(l => l.Origin == value)
                                     .OrderBy(l => l.Id)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    /// <remarks>Runs at once (see the class remarks), not at the unit of work's save.</remarks>
    public async Task<int> DeleteLotsByOriginAsync(AccountingLotOrigin origin,
                                                   CancellationToken cancellationToken = default)
    {
        foreach (var tracked in _context.ChangeTracker.Entries()
                                        .Where(e => e.Entity is AccountingLotEntity or AccountingLotReliefEntity)
                                        .ToList())
            tracked.State = EntityState.Detached;

        var value = (byte)origin;
        var lots = _context.AccountingLots.Where(l => l.Origin == value);
        await _context.AccountingLotReliefs.Where(r => lots.Any(l => l.Id == r.LotId))
                      .ExecuteDeleteAsync(cancellationToken);
        return await lots.ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Runs at once (see the class remarks), not at the unit of work's save.</remarks>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        foreach (var tracked in _context.ChangeTracker.Entries()
                                        .Where(e => e.Entity is AccountingLotEntity or AccountingLotReliefEntity)
                                        .ToList())
            tracked.State = EntityState.Detached;

        await _context.AccountingLotReliefs.ExecuteDeleteAsync(cancellationToken);
        await _context.AccountingLots.ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<AccountingLotEntity?> FindTrackedAsync(long id, CancellationToken cancellationToken) =>
        await _context.AccountingLots.FindAsync([id], cancellationToken);

    private static void Validate(AccountingLot lot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lot.OriginalMsat);
        ArgumentOutOfRangeException.ThrowIfNegative(lot.RemainingMsat);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lot.RemainingMsat, lot.OriginalMsat);
        ArgumentOutOfRangeException.ThrowIfNegative(lot.SourceAdjustment);
        if (lot.FiatCurrency is { Length: not AccountingSchemaLimits.CurrencyLength })
            throw new ArgumentException($"'{lot.FiatCurrency}' is not an ISO 4217 code", nameof(lot));
        ValidatePeriodId(lot.ClosedPeriodId);
    }

    private static void ValidatePeriodId(string? periodId)
    {
        if (periodId is { Length: > AccountingSchemaLimits.PeriodIdMaxLength })
            throw new ArgumentException($"Period id '{periodId}' is too long", nameof(periodId));
    }

    private static AccountingLot MapEntityToDomain(AccountingLotEntity entity) =>
        new(entity.Id, entity.AcquiredAt, (AccountingLotOrigin)entity.Origin, entity.SourceLedgerSeq,
            entity.SourceAdjustment, entity.Account is { } account ? (AccountRole)account : null, entity.ParentLotId,
            entity.OriginalMsat, entity.RemainingMsat, entity.FiatCost, entity.FiatCurrency, entity.PriceId,
            entity.BasisEstimated, entity.ClosedPeriodId);

    private static AccountingLotRelief MapEntityToDomain(AccountingLotReliefEntity entity) =>
        new(entity.Id, entity.LotId, entity.LedgerSeq, entity.Adjustment, entity.RelievedAt, entity.Msat,
            entity.FiatCostRelieved, entity.Proceeds, entity.ClosedPeriodId);
}