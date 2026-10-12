using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The accounting periods and their closes (<c>AccountingPeriods</c>, NL-602 A3-T5, D-A8, D-A13; migration
/// <c>AddAccountingFinancial</c>). Writes are staged on the unit of work and committed by its save.
/// </summary>
public class AccountingPeriodDbRepository : IAccountingPeriodDbRepository
{
    private const byte ClosedState = (byte)AccountingPeriodState.Closed;

    private readonly NLightningDbContext _context;

    public AccountingPeriodDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public async Task AddAsync(AccountingPeriod period, CancellationToken cancellationToken = default)
    {
        Validate(period);

        if (await _context.AccountingPeriods.FindAsync([period.PeriodId], cancellationToken) is not null)
            throw new InvalidOperationException($"Period {period.PeriodId} exists");

        var entity = new AccountingPeriodEntity
        {
            PeriodId = period.PeriodId,
            Start = period.Start,
            End = period.End,
            State = (byte)period.State,
            LastLedgerSeq = period.LastLedgerSeq,
            Forced = period.Forced
        };
        CopyMutableFields(period, entity);
        _context.AccountingPeriods.Add(entity);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(AccountingPeriod period, CancellationToken cancellationToken = default)
    {
        Validate(period);

        var entity = await _context.AccountingPeriods.FindAsync([period.PeriodId], cancellationToken)
                  ?? throw new InvalidOperationException($"Period {period.PeriodId} does not exist");

        entity.Start = period.Start;
        entity.End = period.End;
        entity.State = (byte)period.State;
        entity.LastLedgerSeq = period.LastLedgerSeq;
        entity.Forced = period.Forced;
        CopyMutableFields(period, entity);
    }

    /// <inheritdoc />
    public async Task<AccountingPeriod?> GetAsync(string periodId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodId);

        var staged = _context.AccountingPeriods.Local.FirstOrDefault(p => p.PeriodId == periodId);
        if (staged is not null)
            return MapEntityToDomain(staged);

        var entity = await _context.AccountingPeriods.AsNoTracking()
                                   .FirstOrDefaultAsync(p => p.PeriodId == periodId, cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingPeriod>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.AccountingPeriods.AsNoTracking()
                                     .OrderBy(p => p.Start)
                                     .ThenBy(p => p.PeriodId)
                                     .ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<AccountingPeriod?> GetLastClosedAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingPeriods.AsNoTracking()
                                   .Where(p => p.State == ClosedState)
                                   .OrderByDescending(p => p.End)
                                   .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<AccountingPeriod?> GetClosedContainingAsync(DateTimeOffset time,
                                                                  CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingPeriods.AsNoTracking()
                                   .Where(p => p.State == ClosedState && p.Start <= time && p.End > time)
                                   .OrderBy(p => p.End)
                                   .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    private static void CopyMutableFields(AccountingPeriod period, AccountingPeriodEntity entity)
    {
        entity.ClosedAt = period.ClosedAt;
        entity.ChainHash = period.ChainHash?.ToArray();
        entity.Digest = period.Digest?.ToArray();
        entity.Signature = period.Signature?.ToArray();
        entity.ClosingState = period.ClosingState;
    }

    private static void Validate(AccountingPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(period.PeriodId);
        if (period.PeriodId.Length > AccountingSchemaLimits.PeriodIdMaxLength)
            throw new ArgumentException($"Period id '{period.PeriodId}' is too long", nameof(period));
        if (period.End <= period.Start)
            throw new ArgumentException($"Period {period.PeriodId} ends before it starts", nameof(period));
        ArgumentOutOfRangeException.ThrowIfNegative(period.LastLedgerSeq);
    }

    private static AccountingPeriod MapEntityToDomain(AccountingPeriodEntity entity) =>
        new(entity.PeriodId, entity.Start, entity.End, (AccountingPeriodState)entity.State, entity.ClosedAt,
            entity.LastLedgerSeq, entity.ChainHash, entity.Digest, entity.Signature, entity.Forced,
            entity.ClosingState);
}