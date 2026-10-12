namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Outputs;
using ValueObjects;

/// <summary>
/// The result of building a commitment transaction: the unsigned transaction and where each untrimmed HTLC output
/// ended up after BOLT 3 output ordering (BIP 69 by amount then scriptPubKey, ties broken by <c>cltv_expiry</c>).
/// </summary>
/// <param name="Transaction">The unsigned commitment transaction.</param>
/// <param name="HtlcOutputsInTxOrder">
/// Every HTLC output of the model, sorted by its output index (<c>Vout</c>). This is the order of the HTLC
/// transactions and of <c>commitment_signed.htlc_signatures</c>.
/// </param>
public sealed record CommitmentTransactionBuildResult(
    SignedTransaction Transaction,
    IReadOnlyList<(HtlcOutputInfo Output, uint Vout)> HtlcOutputsInTxOrder);