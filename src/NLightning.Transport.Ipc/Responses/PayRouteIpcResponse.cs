using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// Response for PayRoute (ClientCommand 48): the payment as stored when every route resolved or the wait ended,
/// plus every supplied route's outcome.
/// </summary>
[MessagePackObject]
public sealed class PayRouteIpcResponse
{
    [Key(0)] public required PaymentInfoIpcResponse Payment { get; init; }

    /// <summary>
    /// How each supplied route ended, in the request's order.
    /// </summary>
    [Key(1)] public required List<RouteOutcomeIpcInfo> RouteOutcomes { get; init; }

    public static PayRouteIpcResponse FromClientResponse(PayRouteClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new PayRouteIpcResponse
        {
            Payment = PaymentInfoIpcResponse.FromClientResponse(clientResponse.Payment),
            RouteOutcomes = clientResponse.RouteOutcomes.Select(o => new RouteOutcomeIpcInfo
            {
                Index = o.Index,
                Status = o.Status,
                HtlcId = o.HtlcId,
                FailureCode = o.FailureCode,
                FailureSourceIndex = o.FailureSourceIndex,
                FailureReason = o.FailureReason
            })
                                                .ToList()
        };
    }
}

/// <summary>How one supplied route of a <see cref="PayRouteIpcResponse"/> ended.</summary>
[MessagePackObject]
public sealed class RouteOutcomeIpcInfo
{
    /// <summary>The route's position in the request (0 first).</summary>
    [Key(0)] public int Index { get; init; }

    /// <summary>The route's part state.</summary>
    [Key(1)] public PaymentPartState Status { get; init; }

    /// <summary>The HTLC offered over the route, once offered.</summary>
    [Key(2)] public ulong? HtlcId { get; init; }

    /// <summary>
    /// The BOLT 4 failure code decoded at the origin. It crosses the wire as its <c>u16</c>, so a code this build does
    /// not name still arrives.
    /// </summary>
    [Key(3)] public FailureCode? FailureCode { get; init; }

    /// <summary>The route index of the failing node (0 = our peer), when attributable.</summary>
    [Key(4)] public int? FailureSourceIndex { get; init; }

    /// <summary>The failure in words, when the route failed.</summary>
    [Key(5)] public string? FailureReason { get; init; }
}