namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Interfaces;

/// <summary>
/// Pays a BOLT 11 invoice through <see cref="IPaymentService"/> and waits for the outcome (ClientCommand 10).
/// </summary>
/// <remarks>
/// The wait is bounded by <see cref="PayInvoiceClientRequest.TimeoutSeconds"/> (default
/// <see cref="DefaultTimeoutSeconds"/>, at most <see cref="MaxTimeoutSeconds"/>). When it ends first, the response
/// carries the payment still <c>InFlight</c>: its HTLC stays offered and resolves later (see <c>ListPayments</c>).
/// </remarks>
public sealed class PayInvoiceClientHandler
    : IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>
{
    /// <summary>
    /// The wait when the request does not choose one.
    /// </summary>
    public const uint DefaultTimeoutSeconds = 60;

    /// <summary>
    /// The longest wait a request may ask for.
    /// </summary>
    public const uint MaxTimeoutSeconds = 3_600;

    private readonly IPaymentService _paymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PayInvoice;

    public PayInvoiceClientHandler(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The request is invalid, the invoice was rejected (malformed, expired, other
    /// network, amount missing or inconsistent) or a payment for it is already in flight or succeeded
    /// (<see cref="ErrorCodes.InvalidOperation"/>). Nothing was sent in those cases.</exception>
    public async Task<PayInvoiceClientResponse> HandleAsync(PayInvoiceClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Bolt11))
            throw new ClientException(ErrorCodes.InvalidOperation, "The invoice is empty.");
        if (request.Amount is { IsZero: true })
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be positive.");

        var timeoutSeconds = request.TimeoutSeconds ?? DefaultTimeoutSeconds;
        if (timeoutSeconds is 0 or > MaxTimeoutSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The timeout must be between 1 and {MaxTimeoutSeconds} seconds.");

        try
        {
            var payment = await _paymentService.PayInvoiceAsync(request.Bolt11.Trim(), request.Amount,
                                                                TimeSpan.FromSeconds(timeoutSeconds), ct);
            return new PayInvoiceClientResponse(PaymentInfoClientResponse.FromModel(payment));
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid invoice: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
    }
}