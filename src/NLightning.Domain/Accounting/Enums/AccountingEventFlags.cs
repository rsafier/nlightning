namespace NLightning.Domain.Accounting.Enums;

/// <summary>
/// Markers on an accounting event. The values are persisted: never renumber them.
/// </summary>
[Flags]
public enum AccountingEventFlags
{
    None = 0,

    /// <summary>Written by the one-shot backfill from state that predates the feed.</summary>
    Backfilled = 1,

    /// <summary>A second row with an event key the sealer already sealed: it gets no ledger sequence and is never
    /// read by the books (a bug signal, never a failed core save).</summary>
    Duplicate = 2
}