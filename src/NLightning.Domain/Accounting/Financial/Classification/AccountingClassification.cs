namespace NLightning.Domain.Accounting.Financial.Classification;

using Books;

/// <summary>
/// The answer of the <see cref="ClassificationEngine"/> for one entry (NL-602 A3-T3): where its classifiable lines go
/// and why.
/// </summary>
/// <param name="Account">The financial account of the entry's classifiable lines, or null when the entry has none
/// (every line goes to its chart account).</param>
/// <param name="Source">Why: an override, a rule or the default of the line's role.</param>
/// <param name="RuleId">The rule, when <paramref name="Source"/> is <see cref="AccountingClassificationSource.Rule"/>.</param>
/// <param name="Role">The operational role of the entry's main classifiable line (the largest), or null.</param>
/// <param name="IsUnclassified">The account is one of the chart's unclassified accounts: the entry is listed for
/// review (<see cref="AccountingEntryFlags.Unclassified"/>).</param>
/// <param name="Reason">A short human explanation (<c>override</c>, <c>rule 3 (...)</c>, <c>default for Received</c>).</param>
public sealed record AccountingClassification(
    string? Account,
    AccountingClassificationSource Source,
    long? RuleId,
    AccountRole? Role,
    bool IsUnclassified,
    string Reason)
{
    /// <summary>Whether the entry has a line the classification moves.</summary>
    public bool HasClassifiableLine => Role is not null;

    /// <summary>The rules whose label pattern ran out of time on this entry: they did not match (and should be
    /// reported).</summary>
    public IReadOnlyList<long> TimedOutRuleIds { get; init; } = [];
}