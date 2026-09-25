namespace NLightning.Daemon.Handlers;

using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Interfaces;

/// <summary>
/// Lists a page of our outgoing payments, newest first (ClientCommand 12).
/// </summary>
public sealed class ListPaymentsClientHandler
    : IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>
{
    private readonly IPaymentService _paymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListPayments;

    public ListPaymentsClientHandler(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The page is invalid (negative skip, take outside 1 to
    /// <see cref="ClientRequestGuards.MaxPageSize"/>).</exception>
    public async Task<ListPaymentsClientResponse> HandleAsync(ListPaymentsClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);

        var payments = await _paymentService.ListPaymentsAsync(request.Skip, request.Take, ct);
        return new ListPaymentsClientResponse(payments.Select(PaymentInfoClientResponse.FromModel).ToList());
    }
}