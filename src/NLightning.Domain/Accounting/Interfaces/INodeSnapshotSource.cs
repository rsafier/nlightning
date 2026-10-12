namespace NLightning.Domain.Accounting.Interfaces;

using Models;

/// <summary>
/// The node's live balances by bucket (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §7, §10): what the books reconcile
/// their running balances against. Nothing here is persisted.
/// </summary>
public interface INodeSnapshotSource
{
    Task<AccountingSnapshot> TakeSnapshotAsync(CancellationToken cancellationToken = default);
}