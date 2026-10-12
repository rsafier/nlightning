namespace NLightning.Daemon.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Models;
using Interfaces;

/// <summary>
/// Attaches routes to a <c>payroute</c> payment still in flight (ClientCommand 56, <c>payroute --attach</c>,
/// NL-1276): the request is checked and mapped as <see cref="PayRouteClientHandler"/> does, then paid with
/// <see cref="PayRouteAttachMode.Required"/>. The response's route outcomes are this call's routes; its payment is
/// the payment's when it ended or the wait did.
/// </summary>
public sealed class PayRouteAttachClientHandler
    : IClientCommandHandler<PayRouteAttachClientRequest, PayRouteClientResponse>
{
    private readonly PayRouteClientHandler _payRoute;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PayRouteAttach;

    public PayRouteAttachClientHandler(PayRouteClientHandler payRoute)
    {
        _payRoute = payRoute;
    }

    /// <inheritdoc/>
    public Task<PayRouteClientResponse> HandleAsync(PayRouteAttachClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _payRoute.HandleAsync(request.ToPayRouteRequest(), PayRouteAttachMode.Required, ct);
    }
}