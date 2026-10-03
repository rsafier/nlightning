namespace NLightning.Domain.Accounting.Models;

/// <summary>
/// The result of walking the feed's hash chain (<c>nltg accounting verify</c>, plan §6.2 "Audit", §10).
/// </summary>
/// <param name="VerifiedCount">How many sealed events matched their chain hash, from ledger sequence 1 on.</param>
/// <param name="TipLedgerSeq">The last ledger sequence that verified (0 for an empty feed).</param>
/// <param name="TipHash">The chain hash at <see cref="TipLedgerSeq"/> (32 zero bytes for an empty feed).</param>
/// <param name="BreakLedgerSeq">The first ledger sequence that does not verify, or null when the chain is
/// intact.</param>
/// <param name="BreakReason">Why it does not verify.</param>
public sealed record AccountingChainVerification(
    long VerifiedCount,
    long TipLedgerSeq,
    byte[] TipHash,
    long? BreakLedgerSeq = null,
    string? BreakReason = null)
{
    public bool IsIntact => BreakLedgerSeq is null;
}