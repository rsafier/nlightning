namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// A period close (or a financial rebuild) that was refused (A3-T5): the message says why and what to do.
/// </summary>
public sealed class AccountingCloseRefusedException : InvalidOperationException
{
    public AccountingCloseRefusedException(string message, int unvaluedPostings = 0, int unclassifiedEntries = 0)
        : base(message)
    {
        UnvaluedPostings = unvaluedPostings;
        UnclassifiedEntries = unclassifiedEntries;
    }

    /// <summary>The period's postings without a fiat value.</summary>
    public int UnvaluedPostings { get; }

    /// <summary>The period's unclassified entries.</summary>
    public int UnclassifiedEntries { get; }
}