namespace NLightning.Application.Payments.Send;

using Routing;

/// <summary>
/// One caller-supplied route of a <c>payroute</c> session (NL-1082): our first-hop channel as a planner candidate
/// and the ready-built route. Ordered as the caller gave them, so the session's parts align with the request.
/// </summary>
internal sealed record SuppliedRoutePart(LocalChannelCandidate Channel, PaymentRoute Route);