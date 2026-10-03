namespace NLightning.Daemon.Tests.Cashu;

using Domain.Bitcoin.ValueObjects;
using Domain.Cashu.Enums;
using Domain.Cashu.Interfaces;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// <see cref="ICashuQuoteDbRepository"/> in memory for the processor tests: writes apply at once (no staging), and
/// every read returns a copy, as a database would, so a test sees only what the processor saved.
/// </summary>
public sealed class InMemoryCashuQuoteStore : ICashuQuoteDbRepository
{
    private readonly Dictionary<(TxId, uint), CashuDepositModel> _deposits = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CashuQuoteModel> _quotes = new(StringComparer.Ordinal);

    public void Add(CashuQuoteModel quote)
    {
        lock (_lock)
        {
            if (!_quotes.TryAdd(quote.QuoteId, Copy(quote)))
                throw new InvalidOperationException($"Quote {quote.QuoteId} is already stored.");
        }
    }

    public void Update(CashuQuoteModel quote)
    {
        lock (_lock)
        {
            if (!_quotes.ContainsKey(quote.QuoteId))
                throw new InvalidOperationException($"Quote {quote.QuoteId} is not stored.");
            _quotes[quote.QuoteId] = Copy(quote);
        }
    }

    public Task<CashuQuoteModel?> GetAsync(string quoteId)
    {
        lock (_lock)
            return Task.FromResult(_quotes.TryGetValue(quoteId, out var quote) ? Copy(quote) : null);
    }

    public Task<CashuQuoteModel?> GetOutgoingByPaymentHashAsync(Hash paymentHash)
    {
        lock (_lock)
            return Task.FromResult(_quotes.Values
                                          .Where(q => q.Direction == CashuQuoteDirection.Outgoing
                                                   && q.PaymentHash == paymentHash)
                                          .OrderByDescending(q => q.CreatedAt)
                                          .Select(Copy)
                                          .FirstOrDefault());
    }

    public Task<IReadOnlyList<CashuQuoteModel>> GetIncomingByAddressesAsync(IReadOnlyCollection<string> addresses)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<CashuQuoteModel>>(
                _quotes.Values.Where(q => q.Direction == CashuQuoteDirection.Incoming && q.Address is not null
                                       && addresses.Contains(q.Address))
                       .Select(Copy)
                       .ToList());
    }

    public Task<IReadOnlyList<CashuQuoteModel>> ListOutgoingAsync(CashuQuoteMethod method, CashuQuoteState state)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<CashuQuoteModel>>(
                _quotes.Values.Where(q => q.Direction == CashuQuoteDirection.Outgoing && q.Method == method
                                       && q.State == state)
                       .OrderBy(q => q.CreatedAt)
                       .Select(Copy)
                       .ToList());
    }

    public void AddDeposit(CashuDepositModel deposit)
    {
        lock (_lock)
        {
            if (!_deposits.TryAdd((deposit.TxId, deposit.OutputIndex), deposit with { }))
                throw new InvalidOperationException($"Deposit {deposit.Outpoint} is already stored.");
        }
    }

    public void UpdateDeposit(CashuDepositModel deposit)
    {
        lock (_lock)
            _deposits[(deposit.TxId, deposit.OutputIndex)] = deposit with { };
    }

    public Task<CashuDepositModel?> GetDepositAsync(TxId txId, uint outputIndex)
    {
        lock (_lock)
            return Task.FromResult(_deposits.TryGetValue((txId, outputIndex), out var deposit) ? deposit with { } : null);
    }

    public Task<IReadOnlyList<CashuDepositModel>> GetDepositsAsync(string quoteId)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<CashuDepositModel>>(
                _deposits.Values.Where(d => d.QuoteId == quoteId).OrderBy(d => d.BlockHeight).Select(d => d with { })
                         .ToList());
    }

    public Task<IReadOnlyList<CashuDepositModel>> ListUnreportedDepositsAsync()
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<CashuDepositModel>>(
                _deposits.Values.Where(d => d.ReportedAt is null).OrderBy(d => d.BlockHeight).Select(d => d with { })
                         .ToList());
    }

    private static CashuQuoteModel Copy(CashuQuoteModel quote) =>
        CashuQuoteModel.Restore(quote.QuoteId, quote.Method, quote.Direction, quote.Amount, quote.MaxFee, quote.Fee,
                                quote.PaymentHash, quote.Address, quote.Request, quote.FeeIndex, quote.TxId,
                                quote.OutputIndex, quote.State, quote.FailureReason, quote.CreatedAt,
                                quote.UpdatedAt);
}