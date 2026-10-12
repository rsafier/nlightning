namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The financial book's projector (A3-T4, D-A7) as the period close (A3-T5) and the financial reports and exports
/// (A3-T6) use it: it projects the operational entries after the financial cursor into the financial book.
/// </summary>
/// <remarks>
/// <para>Until A3-T4 registers its projector, the default (<see cref="NullFinancialBooksProjector"/>) is off, so a
/// close, a financial rebuild and the financial reports and exports are refused.</para>
/// <para><b>Contract with the close.</b> The projector holds <see cref="IAccountingAdjustmentSink.EnterAsync"/> from
/// its closed-period check to its save, and for every operational entry dated in a locked period
/// (<see cref="IAccountingAdjustmentSink.GetLockingPeriodAsync"/>) it hands the entry it would have posted to
/// <see cref="IAccountingAdjustmentSink.StageAdjustmentAsync"/> as an
/// <see cref="AccountingAdjustmentReason.LateFact"/> (lots and reliefs dated as that adjustment) instead of posting it
/// at its own time; the sink stages nothing when the fact is already in the book (the replay of a rebuild).</para>
/// <para><b>Contract with the rebuild.</b> A financial rebuild (<see cref="IAccountingPeriods.RebuildFinancialAsync"/>)
/// runs inside <see cref="RunExclusiveAsync{T}"/>: it resets the book to the last close
/// (<c>IAccountingBooksDbRepository.ResetToCloseAsync</c>: open entries but adjustments deleted, their lots and reliefs
/// rolled back, balances and cursor set) and then calls <see cref="ProjectAsync"/>, so the projector must read its
/// state (cursor, open lots) from the database at the start of every round, never from a cache that outlives one.</para>
/// </remarks>
public interface IFinancialBooksProjector
{
    /// <summary>Whether the financial book runs (<c>Accounting:Profile=Financial</c> and the books on).</summary>
    bool IsEnabled { get; }

    /// <summary>Projects every operational entry after the financial cursor now; returns how many entries were
    /// written. Serialized with the projector's own rounds; nothing when off.</summary>
    Task<int> ProjectAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="action"/> while no projection round runs (the projector's round gate).</summary>
    Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action,
                                 CancellationToken cancellationToken = default);
}