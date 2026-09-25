namespace NLightning.Daemon.Handlers;

using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Interfaces;

/// <summary>
/// Lists a page of our invoices, newest first (ClientCommand 11).
/// </summary>
public sealed class ListInvoicesClientHandler
    : IClientCommandHandler<ListInvoicesClientRequest, ListInvoicesClientResponse>
{
    private readonly IInvoiceService _invoiceService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListInvoices;

    public ListInvoicesClientHandler(IInvoiceService invoiceService, TimeProvider timeProvider)
    {
        _invoiceService = invoiceService;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The page is invalid (negative skip, take outside 1 to
    /// <see cref="ClientRequestGuards.MaxPageSize"/>).</exception>
    public async Task<ListInvoicesClientResponse> HandleAsync(ListInvoicesClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);

        var invoices = await _invoiceService.ListInvoicesAsync(request.Skip, request.Take, ct);
        var now = _timeProvider.GetUtcNow();
        return new ListInvoicesClientResponse(invoices.Select(i => InvoiceInfoClientResponse.FromModel(i, now))
                                                      .ToList());
    }
}