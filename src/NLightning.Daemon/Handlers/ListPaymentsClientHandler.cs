namespace NLightning.Daemon.Handlers;

using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Lists a page of our outgoing payments, newest first (ClientCommand 12).
/// </summary>
/// <remarks>
/// NL-899: the outgoing legs of the trampoline payments we relayed (<c>PaymentModel.IsTrampolineRelay</c>) are not our
/// spending, so they are left out unless the request asks for them (<c>--include-relay-legs</c>); the response says
/// how many were left out, and a listed one is marked. A payment of ours sent through a trampoline node carries its
/// trampoline node and the inner route of its last trampoline attempt (<c>PaymentTrampolineHops</c>, read through the
/// scope's unit of work). Without the scope's payment repository (test hosts) the payment service's page is filtered
/// instead, and the hidden count is that page's.
/// </remarks>
public sealed class ListPaymentsClientHandler
    : IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>
{
    private readonly IPaymentDbRepository? _paymentRepository;
    private readonly IPaymentService _paymentService;
    private readonly IUnitOfWork? _unitOfWork;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListPayments;

    public ListPaymentsClientHandler(IPaymentService paymentService, IPaymentDbRepository? paymentRepository = null,
                                     IUnitOfWork? unitOfWork = null)
    {
        _paymentService = paymentService;
        _paymentRepository = paymentRepository;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The page is invalid (negative skip, take outside 1 to
    /// <see cref="ClientRequestGuards.MaxPageSize"/>).</exception>
    public async Task<ListPaymentsClientResponse> HandleAsync(ListPaymentsClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);

        IReadOnlyList<PaymentModel> payments;
        int hidden;
        if (_paymentRepository is { } repository)
        {
            ct.ThrowIfCancellationRequested();
            payments = await repository.ListAsync(request.Skip, request.Take, request.IncludeRelayLegs);
            hidden = request.IncludeRelayLegs ? 0 : await repository.CountTrampolineRelaysAsync();
        }
        else
        {
            var page = await _paymentService.ListPaymentsAsync(request.Skip, request.Take, ct);
            payments = request.IncludeRelayLegs ? page : page.Where(p => !p.IsTrampolineRelay).ToList();
            hidden = page.Count - payments.Count;
        }

        var result = new List<PaymentInfoClientResponse>(payments.Count);
        foreach (var payment in payments)
            result.Add(PaymentInfoClientResponse.FromModel(payment, await GetTrampolineHopsAsync(payment)));

        return new ListPaymentsClientResponse(result, hidden);
    }

    /// <summary>The trampoline hops stored for a payment of ours (every attempt), or none.</summary>
    private async Task<IReadOnlyList<PaymentTrampolineHopModel>> GetTrampolineHopsAsync(PaymentModel payment)
    {
        if (_unitOfWork is null || payment.IsTrampolineRelay)
            return [];

        try
        {
            return _unitOfWork.PaymentTrampolineHopDbRepository is { } hops
                       ? await hops.GetByPaymentAsync(payment.PaymentHash) ?? []
                       : [];
        }
        catch (NotSupportedException)
        {
            // A unit of work that stores no trampoline hops (test doubles)
            return [];
        }
    }
}