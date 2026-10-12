namespace NLightning.Domain.Accounting.Interfaces;

using Models;

/// <summary>
/// The accounting feed's one-shot backfill (NL-602 A1-T6, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4 "Backfill",
/// §10): a cutover that opens the feed with the balances the node holds, then memo events for the history before it.
/// </summary>
/// <remarks>
/// <para>The cutover runs at startup before the peers connect and the chain monitor starts, so the database does not
/// change while it reads; once its marker event exists it returns after one indexed key lookup.</para>
/// <para>The memo backfill runs in the background after the chain monitor started; it is resumable (every memo event
/// carries the key its live writer uses, and a key already written is skipped) and ends with a marker of its own.</para>
/// </remarks>
public interface IAccountingBackfill
{
    /// <summary>
    /// Writes the cutover (opening balances and the marker, one save) unless its marker exists. Call it once per start,
    /// before <c>PeerManager.StartAsync</c> and the chain monitor.
    /// </summary>
    Task<AccountingCutoverResult> EnsureCutoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the memo backfill in the background (nothing when the cutover marker is missing or the memo is
    /// complete). Idempotent; nothing after <see cref="StopAsync"/>.</summary>
    void StartMemoBackfill();

    /// <summary>Cancels the memo backfill and waits for it; a batch in progress is not saved.</summary>
    Task StopAsync();
}