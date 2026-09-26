namespace NLightning.Daemon.Handlers;

using Application.Payments.Routing.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Interfaces;

/// <summary>
/// The route a payment to a node would take now (ClientCommand 19, BOLT 7 plan G4-T4): planned by the payment
/// service exactly as a payment's first round with one part (our direct channels, the gossip graph), nothing sent.
/// </summary>
/// <remarks>
/// A zero amount, our own node id, no processed block yet or no route within the limits is
/// <see cref="ErrorCodes.InvalidOperation"/> with the planner's reason.
/// </remarks>
public sealed class GetRouteClientHandler : IClientCommandHandler<GetRouteClientRequest, GetRouteClientResponse>
{
    private readonly IRouteQueryService _routeQueryService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.GetRoute;

    public GetRouteClientHandler(IRouteQueryService routeQueryService)
    {
        _routeQueryService = routeQueryService;
    }

    /// <inheritdoc/>
    public async Task<GetRouteClientResponse> HandleAsync(GetRouteClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount.IsZero)
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be positive.");

        try
        {
            var quote = await _routeQueryService.QuoteRouteAsync(request.NodeId, request.Amount, request.MaxFee,
                                                                 request.FinalCltvDelta, ct);
            var route = quote.Route;
            var hops = new List<GetRouteHop>(route.Hops.Count);
            for (var i = 0; i < route.Hops.Count; i++)
            {
                var previous = i == 0 ? null : route.Hops[i - 1];
                var received = previous?.AmountToForward ?? route.FirstHopAmount;
                var fee = route.Hops[i].IsFinal ? LightningMoney.Zero : received - route.Hops[i].AmountToForward;
                hops.Add(new GetRouteHop(route.Hops[i].NodeId,
                                         previous?.OutgoingShortChannelId ?? quote.Channel.ShortChannelId, received,
                                         previous?.OutgoingCltvValue ?? route.FirstHopCltvExpiry, fee));
            }

            return new GetRouteClientResponse(quote.Channel.ChannelId, hops, route.FirstHopAmount, route.Fee,
                                              route.FirstHopCltvExpiry, quote.BlockHeight, quote.Probability,
                                              quote.Description);
        }
        catch (Exception e) when (e is ArgumentException || e.GetType() == typeof(InvalidOperationException))
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
    }
}