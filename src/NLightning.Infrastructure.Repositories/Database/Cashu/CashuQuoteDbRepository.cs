using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Cashu;

using Domain.Bitcoin.ValueObjects;
using Domain.Cashu.Enums;
using Domain.Cashu.Interfaces;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Persistence.Contexts;
using Persistence.Entities.Cashu;
using Persistence.EntityConfiguration.Cashu;

/// <summary>
/// Stores the CDK payment processor's quotes and on-chain deposits (NL-997, migration <c>AddCashuProcessorQuotes</c>).
/// </summary>
public class CashuQuoteDbRepository : BaseDbRepository<CashuQuoteEntity>, ICashuQuoteDbRepository
{
    private const byte OutgoingDirection = (byte)CashuQuoteDirection.Outgoing;
    private const byte IncomingDirection = (byte)CashuQuoteDirection.Incoming;

    private readonly NLightningDbContext _context;

    public CashuQuoteDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public void Add(CashuQuoteModel quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        Insert(MapDomainToEntity(quote));
    }

    /// <inheritdoc />
    public void Update(CashuQuoteModel quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var entity = MapDomainToEntity(quote);
        var tracked = DbSet.Local.FirstOrDefault(e => e.QuoteId == quote.QuoteId);
        if (tracked is not null)
        {
            _context.Entry(tracked).CurrentValues.SetValues(entity);
            return;
        }

        DbSet.Update(entity);
    }

    /// <inheritdoc />
    public async Task<CashuQuoteModel?> GetAsync(string quoteId)
    {
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(e => e.QuoteId == quoteId);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<CashuQuoteModel?> GetOutgoingByPaymentHashAsync(Hash paymentHash)
    {
        var entity = await DbSet.AsNoTracking()
                                .Where(e => e.Direction == OutgoingDirection && e.PaymentHash == paymentHash)
                                .OrderByDescending(e => e.CreatedAt)
                                .FirstOrDefaultAsync();
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CashuQuoteModel>> GetIncomingByAddressesAsync(
        IReadOnlyCollection<string> addresses)
    {
        if (addresses.Count == 0)
            return [];

        var list = addresses.ToList();
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.Direction == IncomingDirection && e.Address != null
                                           && list.Contains(e.Address))
                                  .ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CashuQuoteModel>> ListOutgoingAsync(CashuQuoteMethod method,
                                                                        CashuQuoteState state)
    {
        var methodByte = (byte)method;
        var stateByte = (byte)state;
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.Direction == OutgoingDirection && e.Method == methodByte
                                           && e.State == stateByte)
                                  .OrderBy(e => e.CreatedAt)
                                  .ToListAsync();
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public void AddDeposit(CashuDepositModel deposit)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        _context.CashuDeposits.Add(MapDepositToEntity(deposit));
    }

    /// <inheritdoc />
    public void UpdateDeposit(CashuDepositModel deposit)
    {
        ArgumentNullException.ThrowIfNull(deposit);

        var entity = MapDepositToEntity(deposit);
        var tracked = _context.CashuDeposits.Local.FirstOrDefault(e => e.TxId == deposit.TxId
                                                                    && e.OutputIndex == deposit.OutputIndex);
        if (tracked is not null)
        {
            _context.Entry(tracked).CurrentValues.SetValues(entity);
            return;
        }

        _context.CashuDeposits.Update(entity);
    }

    /// <inheritdoc />
    public async Task<CashuDepositModel?> GetDepositAsync(TxId txId, uint outputIndex)
    {
        var entity = await _context.CashuDeposits.AsNoTracking()
                                   .FirstOrDefaultAsync(e => e.TxId == txId && e.OutputIndex == outputIndex);
        return entity is null ? null : MapDepositToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CashuDepositModel>> GetDepositsAsync(string quoteId)
    {
        var entities = await _context.CashuDeposits.AsNoTracking()
                                     .Where(e => e.QuoteId == quoteId)
                                     .OrderBy(e => e.BlockHeight)
                                     .ToListAsync();
        return entities.Select(MapDepositToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CashuDepositModel>> ListUnreportedDepositsAsync()
    {
        var entities = await _context.CashuDeposits.AsNoTracking()
                                     .Where(e => e.ReportedAt == null)
                                     .OrderBy(e => e.BlockHeight)
                                     .ToListAsync();
        return entities.Select(MapDepositToDomain).ToList();
    }

    internal static CashuQuoteEntity MapDomainToEntity(CashuQuoteModel quote)
    {
        var reason = quote.FailureReason;
        if (reason is { Length: > CashuQuoteEntityConfiguration.FailureReasonMaxLength })
            reason = reason[..CashuQuoteEntityConfiguration.FailureReasonMaxLength];

        return new CashuQuoteEntity
        {
            QuoteId = quote.QuoteId,
            Method = (byte)quote.Method,
            Direction = (byte)quote.Direction,
            AmountMsat = checked((long)quote.Amount.MilliSatoshi),
            MaxFeeMsat = quote.MaxFee is { } maxFee ? checked((long)maxFee.MilliSatoshi) : null,
            FeeMsat = quote.Fee is { } fee ? checked((long)fee.MilliSatoshi) : null,
            PaymentHash = quote.PaymentHash,
            Address = quote.Address,
            Request = quote.Request,
            FeeIndex = quote.FeeIndex,
            TxId = quote.TxId,
            OutputIndex = quote.OutputIndex,
            State = (byte)quote.State,
            FailureReason = reason,
            CreatedAt = quote.CreatedAt,
            UpdatedAt = quote.UpdatedAt
        };
    }

    internal static CashuQuoteModel MapEntityToDomain(CashuQuoteEntity entity)
    {
        return CashuQuoteModel.Restore(entity.QuoteId, (CashuQuoteMethod)entity.Method,
                                       (CashuQuoteDirection)entity.Direction,
                                       LightningMoney.MilliSatoshis(checked((ulong)entity.AmountMsat)),
                                       entity.MaxFeeMsat is { } maxFee
                                           ? LightningMoney.MilliSatoshis(checked((ulong)maxFee))
                                           : null,
                                       entity.FeeMsat is { } fee
                                           ? LightningMoney.MilliSatoshis(checked((ulong)fee))
                                           : null,
                                       entity.PaymentHash, entity.Address, entity.Request, entity.FeeIndex,
                                       entity.TxId, entity.OutputIndex, (CashuQuoteState)entity.State,
                                       entity.FailureReason, entity.CreatedAt, entity.UpdatedAt);
    }

    internal static CashuDepositEntity MapDepositToEntity(CashuDepositModel deposit) => new()
    {
        TxId = deposit.TxId,
        OutputIndex = deposit.OutputIndex,
        QuoteId = deposit.QuoteId,
        AmountSat = deposit.Amount.Satoshi,
        BlockHeight = deposit.BlockHeight,
        ReportedAt = deposit.ReportedAt
    };

    internal static CashuDepositModel MapDepositToDomain(CashuDepositEntity entity) =>
        new(entity.QuoteId, entity.TxId, entity.OutputIndex,
            LightningMoney.Satoshis(checked((ulong)entity.AmountSat)), entity.BlockHeight)
        {
            ReportedAt = entity.ReportedAt
        };
}