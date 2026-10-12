namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// One closed period checked by <c>nltg accounting verify</c> (A3-T5, D-A13).
/// </summary>
/// <param name="PeriodId">The period.</param>
/// <param name="DigestMatches">The digest recomputed from the stored entries, reliefs, lots and closing state equals the
/// stored one.</param>
/// <param name="SignatureValid">The stored signature is our node key's over the stored digest.</param>
/// <param name="ChainHashMatches">The stored chain hash is the feed's stored hash at the close's last ledger
/// sequence.</param>
/// <param name="ClosingStateMatches">The closing balances equal the previous close's plus the period's entries.</param>
/// <param name="Contiguous">The period starts where the previous closed one ends.</param>
/// <param name="EntryCount">The financial entries recomputed.</param>
/// <param name="ReliefCount">The reliefs recomputed.</param>
/// <param name="OpenLotCount">The lots open at the period's end.</param>
/// <param name="Problem">What does not match, or null.</param>
/// <remarks><see cref="StrayEntryCount"/>: financial entries dated in the period that its close does not hold (a write
/// past the lock); 0 when intact.</remarks>
public sealed record AccountingCloseVerification(
    string PeriodId,
    bool DigestMatches,
    bool SignatureValid,
    bool ChainHashMatches,
    bool ClosingStateMatches,
    bool Contiguous,
    long EntryCount,
    long ReliefCount,
    long OpenLotCount,
    string? Problem)
{
    public long StrayEntryCount { get; init; }

    public bool IsIntact => DigestMatches && SignatureValid && ChainHashMatches && ClosingStateMatches && Contiguous
                         && StrayEntryCount == 0;
}