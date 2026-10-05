namespace NLightning.Domain.Payments.Models;

using Enums;
using Protocol.Onion.Enums;

/// <summary>
/// How one caller-supplied route of a <c>payroute</c> payment ended (NL-1082): the route's position in the request,
/// its HTLC (once offered) and, when it failed, the attributed failure.
/// </summary>
public sealed record RouteOutcome(int Index, PaymentPartState Status, ulong? HtlcId, FailureCode? FailureCode,
                                  int? FailureSourceIndex, string? FailureReason);

/// <summary>The result of a <c>payroute</c> call: the payment row (as <c>payinvoice</c> reports it) plus every
/// supplied route's outcome.</summary>
public sealed record PayRouteResult(PaymentModel Payment, IReadOnlyList<RouteOutcome> Outcomes);