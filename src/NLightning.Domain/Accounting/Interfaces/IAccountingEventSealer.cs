namespace NLightning.Domain.Accounting.Interfaces;

using Models;

/// <summary>
/// The accounting feed's single sealer (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §3, principle 3): it gives every
/// committed, unsealed event its dense ledger sequence and chain hash, in commit order, and marks repeated keys as
/// duplicates.
/// </summary>
public interface IAccountingEventSealer
{
    /// <summary>
    /// Seals every event committed so far, in batches, now. Rounds never overlap across the process: a call made while
    /// a round runs waits for it, then runs its own.
    /// </summary>
    Task<AccountingSealRoundResult> SealNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the background loop for an early round (a writer that just committed events); never blocks.</summary>
    void Nudge();
}