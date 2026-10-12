namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The period closes of the financial book (A3-T5, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2 "Period close and
/// lock", D-A8, D-A13): <c>nltg accounting close</c>, <c>close list</c>, <c>close show</c>, the closes' part of
/// <c>verify</c> and <c>rebuild --book financial</c>.
/// </summary>
public interface IAccountingPeriods
{
    /// <summary>
    /// Closes a period: seals the feed and projects both books first, refuses while the period has unvalued postings or
    /// unclassified entries (or a book lags) unless <paramref name="force"/>, then in one save writes the
    /// <c>AccountingPeriods</c> row with the digest and the node key's signature and marks the period's financial
    /// entries, lots and reliefs closed.
    /// </summary>
    /// <param name="period">The period: <c>YYYY-MM</c> or <c>YYYY-MM-DD..YYYY-MM-DD</c>.</param>
    /// <param name="force">Close although it holds unvalued or unclassified rows or a book lags.</param>
    /// <exception cref="ArgumentException">Not a period.</exception>
    /// <exception cref="AccountingCloseRefusedException">The close is refused (the message says why).</exception>
    Task<AccountingCloseReport> CloseAsync(string period, bool force, CancellationToken cancellationToken = default);

    /// <summary>Every period, oldest first.</summary>
    Task<IReadOnlyList<AccountingCloseReport>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One period with its closing state, or null.</summary>
    /// <exception cref="ArgumentException">Not a period.</exception>
    Task<AccountingCloseReport?> GetAsync(string period, CancellationToken cancellationToken = default);

    /// <summary>Checks every closed period: the digest recomputed from the stored rows, the signature against our node
    /// id, the chain hash against the feed, the closing balances and the contiguity of the closes.</summary>
    Task<IReadOnlyList<AccountingCloseVerification>> VerifyClosesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the financial book from the last close (D-A8): the closed periods are kept as they are, the open
    /// period's entries (but the adjustments), lots and reliefs are rolled back to the close's state and projected
    /// again. Before any close, the whole financial book (imported lots kept). Returns how many entries were projected.
    /// </summary>
    /// <exception cref="AccountingCloseRefusedException">The financial book is off.</exception>
    Task<int> RebuildFinancialAsync(CancellationToken cancellationToken = default);
}