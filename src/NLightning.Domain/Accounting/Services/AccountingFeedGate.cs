namespace NLightning.Domain.Accounting.Services;

using Constants;

/// <summary>
/// Holds the accounting feed's live writes while the feed has no cutover (NL-619, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> A1-T6). The feed opens with the node's balances at its cutover; when that fails
/// and the node runs anyway, a live event written meanwhile would make the next start take the feed for one that
/// predates the cutover and write no opening balance at all. So the backfill holds the gate when the cutover fails, the
/// feed's repository drops every new event but the cutover's own (<c>open:</c> keys) while it is held, and the next
/// start's cutover opens the feed with the balances of that moment, which contain everything that happened meanwhile.
/// Open by default (a host without a cutover writes as before); one instance per process, thread-safe.
/// </summary>
public sealed class AccountingFeedGate
{
    private const string OpeningKeyPrefix = "open:";

    private long _dropped;
    private volatile string? _reason;

    /// <summary>Whether live events are dropped (the cutover failed in this process).</summary>
    public bool IsHeld => _reason is not null;

    /// <summary>Why the gate is held, when it is.</summary>
    public string? Reason => _reason;

    /// <summary>How many events were dropped while held.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Holds the gate: live events are dropped until <see cref="Release"/>.</summary>
    public void Hold(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _reason = reason;
    }

    /// <summary>Opens the gate (the cutover exists).</summary>
    public void Release() => _reason = null;

    /// <summary>
    /// Whether an event with this key may be written now: always while open, only the cutover's own events
    /// (<see cref="AccountingEventKeys.Cutover"/>, the opening balances, the memo marker) while held; a refusal is
    /// counted.
    /// </summary>
    public bool Admits(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        if (_reason is null || eventKey.StartsWith(OpeningKeyPrefix, StringComparison.Ordinal))
            return true;

        Interlocked.Increment(ref _dropped);
        return false;
    }
}