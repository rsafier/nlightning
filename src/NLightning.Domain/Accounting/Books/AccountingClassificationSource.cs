namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// Why a financial entry went to its account (A3-T3, <c>AccountingEntries.Classification</c>): the persisted half of the
/// classification engine's answer. Never renumber.
/// </summary>
public enum AccountingClassificationSource : byte
{
    /// <summary>The default account of the event's kind.</summary>
    Default = 1,

    /// <summary>The first matching enabled rule (<c>AccountingEntry.RuleId</c>, D-A10).</summary>
    Rule = 2,

    /// <summary>A manual reclassification of the event key (<c>AccountingOverrides</c>), which wins over the rules.</summary>
    Override = 3
}