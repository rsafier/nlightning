namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Interfaces;

/// <summary>
/// Creates, signs and persists an invoice through <see cref="IInvoiceService"/> (ClientCommand 9). The returned
/// BOLT 11 string is payable as soon as this returns.
/// </summary>
public sealed class CreateInvoiceClientHandler
    : IClientCommandHandler<CreateInvoiceClientRequest, CreateInvoiceClientResponse>
{
    private readonly IInvoiceService _invoiceService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.CreateInvoice;

    public CreateInvoiceClientHandler(IInvoiceService invoiceService, TimeProvider timeProvider)
    {
        _invoiceService = invoiceService;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The amount is zero, the expiry is zero, or the invoice service rejected the
    /// arguments (<see cref="ErrorCodes.InvalidOperation"/>).</exception>
    public async Task<CreateInvoiceClientResponse> HandleAsync(CreateInvoiceClientRequest request,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is { IsZero: true })
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The invoice amount must be positive; leave it out for an any-amount invoice.");
        if (request.ExpirySeconds == 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The invoice expiry must be positive.");

        try
        {
            var invoice = await _invoiceService.CreateInvoiceAsync(request.Amount, request.Description ?? string.Empty,
                                                                   request.ExpirySeconds, ct);
            return new CreateInvoiceClientResponse(
                InvoiceInfoClientResponse.FromModel(invoice, _timeProvider.GetUtcNow()));
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid invoice: {e.Message}", e);
        }
    }
}