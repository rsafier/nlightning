namespace NLightning.Domain.Accounting.Enums;

/// <summary>What a cutover attempt did.</summary>
public enum AccountingCutoverOutcome
{
    /// <summary>The marker existed: nothing was read or written.</summary>
    AlreadyDone,

    /// <summary>The opening balances and the marker were written.</summary>
    Written,

    /// <summary>The feed already had events (a node that ran the feed before the backfill existed): only the marker
    /// was written, with no opening balance, since those would count the recorded facts twice.</summary>
    SkippedOpening
}