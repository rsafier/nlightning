namespace NLightning.Domain.Bitcoin.Transactions.Models;

using ValueObjects;

/// <summary>
/// The result of building a funding transaction: the unsigned transaction and where the funding output ended up.
/// </summary>
/// <param name="Transaction">The unsigned funding transaction.</param>
/// <param name="FundingOutputIndex">The index of the funding output in <paramref name="Transaction"/>.</param>
public sealed record FundingTransactionBuildResult(SignedTransaction Transaction, ushort FundingOutputIndex);