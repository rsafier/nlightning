namespace NLightning.Domain.Channels.Splicing.Models;

using Enums;

/// <summary>
/// A splice rule that does not hold (<see cref="SpliceRules"/>).
/// </summary>
/// <param name="RequirementId">The row of the splicing plan matrix (§1.3-1.5), e.g. <c>SP-R-01</c>.</param>
/// <param name="Action">What BOLT 2 prescribes.</param>
/// <param name="Reason">A short explanation for logs and the <c>tx_abort</c>/<c>warning</c> text.</param>
public sealed record SpliceRuleViolation(string RequirementId, SpliceRuleAction Action, string Reason);