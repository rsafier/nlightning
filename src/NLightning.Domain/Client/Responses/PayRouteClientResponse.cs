namespace NLightning.Domain.Client.Responses;

using Payments.Enums;
using Protocol.Onion.Enums;

/// <summary>
/// The outcome of <c>PayRoute</c>: the payment as stored when every route resolved or the wait ended (routes still
/// pending then read <see cref="PaymentPartState.InFlight"/>), plus every supplied route's outcome.
/// </summary>
public sealed class PayRouteClientResponse
{
    public required PaymentInfoClientResponse Payment { get; init; }

    /// <summary>
    /// How each supplied route ended, in the request's order.
    /// </summary>
    public required IReadOnlyList<RouteOutcomeClientInfo> RouteOutcomes { get; init; }
}

/// <summary>
/// How one caller-supplied route of a <see cref="PayRouteClientResponse"/> ended (NL-1082): the route's position in
/// the request, its HTLC (once offered) and, when it failed, the attributed failure.
/// </summary>
/// <param name="Index">The route's position in the request (0 first).</param>
/// <param name="Status">The route's part state.</param>
/// <param name="HtlcId">The HTLC offered over the route, once offered.</param>
/// <param name="FailureCode">The BOLT 4 failure code, when the route failed with a readable error.</param>
/// <param name="FailureSourceIndex">The route index of the failing node (0 = our peer), when attributable.</param>
/// <param name="FailureReason">The failure in words, when the route failed.</param>
public sealed record RouteOutcomeClientInfo(
    int Index,
    PaymentPartState Status,
    ulong? HtlcId,
    FailureCode? FailureCode,
    int? FailureSourceIndex,
    string? FailureReason);