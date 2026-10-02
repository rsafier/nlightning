namespace NLightning.Domain.Accounting.Financial;

using Books;
using Persistence.Interfaces;

/// <summary>
/// The lock of the closed periods (A3-T5, D-A8): nothing that writes the financial book changes a closed period. A
/// writer whose write would land in one (the financial projector reaching a fact dated there, a new override, a rule
/// change, a price found later) hands the correction to <see cref="StageAdjustmentAsync"/> (or, for A3-T2's prices,
/// <see cref="AdjustLateValuationAsync"/>), which stages it as an adjustment entry of the open period, dated now, in
/// the writer's own unit of work. The production rule is <c>AccountingPeriodService</c>;
/// <see cref="NullAccountingAdjustmentSink"/> is the default without it.
/// </summary>
/// <remarks>
/// <para><b>The write lock.</b> A writer of the financial book holds <see cref="EnterAsync"/> from its check
/// (<see cref="GetLockingPeriodAsync"/>) to its save; the close holds it too, so a close never commits between a writer's
/// check and its save. Take it inside your own locks (the financial projector's round gate) and never call the
/// projector while holding it.</para>
/// <para><b>What is locked.</b> Every time before the end of the last closed period (closes are contiguous, so that is
/// every closed period and nothing else). A lot acquired or relieved there is never changed again either: its cost and
/// reliefs are covered by the close's digest (<see cref="AccountingCloseDigest"/>), and <c>verify</c> reports a
/// change.</para>
/// </remarks>
public interface IAccountingAdjustmentSink
{
    /// <summary>The financial book's write lock; dispose to release.</summary>
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The closed period that locks <paramref name="time"/> (the one holding it, else the last one when the time is
    /// before every closed period), or null when the time is after the end of the last close (open). Cached: one database
    /// read per process until the next close or rebuild.
    /// </summary>
    Task<AccountingPeriod?> GetLockingPeriodAsync(DateTimeOffset time, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages <paramref name="adjustment"/> on <paramref name="unitOfWork"/> as an entry of the financial book with the
    /// fact's ledger sequence and the next adjustment number (1 or more), dated now (or at the end of the last close
    /// when the clock is behind it), flagged <see cref="AccountingEntryFlags.Adjustment"/>, with a note naming the
    /// reason, the closed period and the fact's time. The caller saves.
    /// </summary>
    /// <returns>The staged entry, or null when nothing was staged: a <see cref="AccountingAdjustmentReason.LateFact"/>
    /// whose event key already has an entry in the financial book (a closed one, or an earlier adjustment: a replay of a
    /// rebuild), or an adjustment whose <see cref="AccountingAdjustment.DedupeKey"/> is already there.</returns>
    /// <exception cref="ArgumentException">The postings do not balance or a line names no account.</exception>
    Task<AccountingEntry?> StageAdjustmentAsync(IUnitOfWork unitOfWork, AccountingAdjustment adjustment,
                                                CancellationToken cancellationToken = default);

    /// <summary>
    /// A price was found for a posting of a closed period (A3-T2's back-valuation, which never fills it). Stages the
    /// adjustment that carries its value into the open period (<see cref="AccountingAdjustmentReason.Price"/>, one
    /// zero-msat line on the posting's account with the fiat value, deduplicated per posting); returns true when one is
    /// staged or exists, false when the rule did not take it (the posting stays unvalued and is offered again later).
    /// </summary>
    Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                        CancellationToken cancellationToken = default);
}

/// <summary>The adjustment rule when no period service is registered: locks nothing and takes nothing (a closed
/// period's posting stays unvalued).</summary>
public sealed class NullAccountingAdjustmentSink : IAccountingAdjustmentSink
{
    public static NullAccountingAdjustmentSink Instance { get; } = new();

    /// <inheritdoc />
    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IDisposable>(NoLock.Instance);

    /// <inheritdoc />
    public Task<AccountingPeriod?> GetLockingPeriodAsync(DateTimeOffset time,
                                                         CancellationToken cancellationToken = default) =>
        Task.FromResult<AccountingPeriod?>(null);

    /// <inheritdoc />
    public Task<AccountingEntry?> StageAdjustmentAsync(IUnitOfWork unitOfWork, AccountingAdjustment adjustment,
                                                       CancellationToken cancellationToken = default) =>
        Task.FromResult<AccountingEntry?>(null);

    /// <inheritdoc />
    public Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                               CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    private sealed class NoLock : IDisposable
    {
        public static NoLock Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}