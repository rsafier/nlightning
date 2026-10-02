namespace NLightning.Domain.Accounting.Financial;

using Persistence.Interfaces;

/// <summary>
/// The period lock's adjustment rule (D-A8): a late write that would change a closed period becomes an adjustment
/// entry dated in the open period. The seam between the writers that find such a fact (A3-T2's back-valuation today)
/// and the rule that posts it (A3-T5); the default, <see cref="NullAccountingAdjustmentSink"/>, posts nothing.
/// </summary>
/// <remarks>
/// Implementations stage their writes on the caller's <see cref="IUnitOfWork"/> (the caller saves them with the rest of
/// its batch) and are idempotent per posting: a second call for a posting already adjusted stages nothing and returns
/// true. Callers may call again after a restart.
/// </remarks>
public interface IAccountingAdjustmentSink
{
    /// <summary>
    /// A price was found for a posting of a closed period (A3-T2). Stages the adjustment that carries its value into
    /// the open period; returns true when one is staged or exists, false when the rule did not take it (the posting
    /// stays unvalued and is offered again later).
    /// </summary>
    Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                        CancellationToken cancellationToken = default);
}

/// <summary>The adjustment rule before A3-T5: takes nothing (a closed period's posting stays unvalued).</summary>
public sealed class NullAccountingAdjustmentSink : IAccountingAdjustmentSink
{
    public static NullAccountingAdjustmentSink Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                               CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}