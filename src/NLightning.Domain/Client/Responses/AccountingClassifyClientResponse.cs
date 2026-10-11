namespace NLightning.Domain.Client.Responses;

using Accounting.Financial;
using Accounting.Financial.Classification;
using Enums;

/// <summary>
/// The answer to an <c>accounting classify</c> action (NL-602 A3-T3): the fields of the action are set.
/// </summary>
/// <param name="Action">The action.</param>
/// <param name="Profile">The books' profile in effect (rules and overrides are kept either way; only the financial
/// profile projects with them).</param>
public sealed record AccountingClassifyClientResponse(AccountingClassifyAction Action, AccountingProfile Profile)
{
    /// <summary>The rules (list), or the one added.</summary>
    public IReadOnlyList<AccountingRule>? Rules { get; init; }

    /// <summary>Whether a remove, enable, disable or unset found its rule or override.</summary>
    public bool? Changed { get; init; }

    public AccountingClassifyTestResult? Test { get; init; }

    /// <summary>The override stored by a set.</summary>
    public AccountingOverride? Override { get; init; }

    /// <summary>The overrides (list).</summary>
    public IReadOnlyList<AccountingOverride>? Overrides { get; init; }

    public AccountingUnclassifiedPage? Unclassified { get; init; }

    /// <summary>What the operator should know (stored rules that can never match, ignored chart names, a profile that
    /// does not use the rules).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}