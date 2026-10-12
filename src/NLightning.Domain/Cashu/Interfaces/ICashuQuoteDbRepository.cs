namespace NLightning.Domain.Cashu.Interfaces;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// Stores the CDK payment processor's quotes and on-chain deposits (NL-997, migration <c>AddCashuProcessorQuotes</c>,
/// tables <c>CashuQuotes</c> and <c>CashuDeposits</c>); reached through <c>IUnitOfWork.CashuQuoteDbRepository</c>.
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. Reads see what is saved, not
/// what this unit of work staged. A quote id or a deposit outpoint is stored once: a second add fails the save.
/// </remarks>
public interface ICashuQuoteDbRepository
{
    /// <summary>Stages a new quote.</summary>
    void Add(CashuQuoteModel quote);

    /// <summary>Stages every mutable field of a stored (or staged) quote.</summary>
    void Update(CashuQuoteModel quote);

    /// <summary>The quote, or null.</summary>
    Task<CashuQuoteModel?> GetAsync(string quoteId);

    /// <summary>The newest melt that pays <paramref name="paymentHash"/>, or null.</summary>
    Task<CashuQuoteModel?> GetOutgoingByPaymentHashAsync(Hash paymentHash);

    /// <summary>The on-chain mint quotes whose address is one of <paramref name="addresses"/>.</summary>
    Task<IReadOnlyList<CashuQuoteModel>> GetIncomingByAddressesAsync(IReadOnlyCollection<string> addresses);

    /// <summary>The melts of <paramref name="method"/> in <paramref name="state"/>, oldest first.</summary>
    Task<IReadOnlyList<CashuQuoteModel>> ListOutgoingAsync(CashuQuoteMethod method, CashuQuoteState state);

    /// <summary>Stages a new deposit.</summary>
    void AddDeposit(CashuDepositModel deposit);

    /// <summary>Stages a stored deposit's block height and report time.</summary>
    void UpdateDeposit(CashuDepositModel deposit);

    /// <summary>The deposit, or null.</summary>
    Task<CashuDepositModel?> GetDepositAsync(TxId txId, uint outputIndex);

    /// <summary>The deposits to the quote's address, oldest block first.</summary>
    Task<IReadOnlyList<CashuDepositModel>> GetDepositsAsync(string quoteId);

    /// <summary>The deposits not reported to the mint yet, oldest block first.</summary>
    Task<IReadOnlyList<CashuDepositModel>> ListUnreportedDepositsAsync();
}