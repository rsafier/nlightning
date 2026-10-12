namespace NLightning.Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// A BOLT 2 "Interactive Transaction Construction" rule the peer broke (<see cref="InteractiveTxRules"/>): the
/// negotiation fails with our <c>tx_abort</c>, whose data is <see cref="Reason"/>.
/// </summary>
/// <param name="RequirementId">The splicing plan requirement (§1.2, for example <c>IT-R-01</c>).</param>
/// <param name="Reason">A printable reason, sent as the <c>tx_abort</c> data.</param>
public sealed record InteractiveTxRuleViolation(string RequirementId, string Reason);