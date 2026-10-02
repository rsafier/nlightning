namespace NLightning.Domain.Client.Requests;

using Accounting.Financial;
using Enums;

/// <summary>
/// One <c>accounting classify</c> action (<c>ClientCommand.AccountingAdmin</c>, NL-602 A3-T3, D-A10); each action reads
/// the fields it needs.
/// </summary>
public sealed class AccountingClassifyClientRequest
{
    public AccountingClassifyAction Action { get; init; } = AccountingClassifyAction.RuleList;

    /// <summary>The rule to add (its id and creation time are assigned), or the candidate of a test.</summary>
    public AccountingRule? Rule { get; init; }

    /// <summary>The rule of a remove, enable or disable.</summary>
    public long? RuleId { get; init; }

    /// <summary>The event of a test, a set or an unset.</summary>
    public string? EventKey { get; init; }

    /// <summary>The override's target account (set).</summary>
    public string? Account { get; init; }

    /// <summary>The override's note (set).</summary>
    public string? Note { get; init; }

    /// <summary>Where the unclassified listing starts (after this ledger sequence).</summary>
    public long AfterLedgerSeq { get; init; }

    /// <summary>How many overrides to skip (override listing).</summary>
    public int Skip { get; init; }

    /// <summary>The most items to return.</summary>
    public int Limit { get; init; } = 100;
}