namespace NLightning.Daemon.Handlers;

using Application.Payments.Send;
using Domain.Bitcoin.Constants;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Pays a BOLT 11 invoice through <see cref="IPaymentService"/> and waits for the outcome (ClientCommand 10).
/// </summary>
/// <remarks>
/// The request's optional <see cref="PayInvoiceClientRequest.MaxFee"/> and <see cref="PayInvoiceClientRequest.MaxParts"/>
/// are the per-call fee and part limits (NL-270, <see cref="PayInvoiceOptions"/>); the timeout also ends the retries.
/// The wait is bounded by <see cref="PayInvoiceClientRequest.TimeoutSeconds"/> (default
/// <see cref="DefaultTimeoutSeconds"/>, at most <see cref="MaxTimeoutSeconds"/>). When it ends first, the response
/// carries the payment still <c>InFlight</c>: its HTLC stays offered and resolves later (see <c>ListPayments</c>).
/// The cap is kept short because the call holds one IPC pipe instance for the whole wait (see
/// <c>NamedPipeIpcService.MaxServerInstances</c>); for a longer wait, poll <c>ListPayments</c>.
/// <para>Only the exceptions <see cref="IPaymentService.PayInvoiceAsync"/> documents as "nothing persisted" become
/// <see cref="ErrorCodes.InvalidOperation"/>: <see cref="ArgumentException"/> and a plain
/// <see cref="InvalidOperationException"/> (duplicate hash). Its subclasses (for example
/// <see cref="ObjectDisposedException"/> at shutdown) and every other exception are not guaranteed to happen before
/// the HTLC was offered, so they become <see cref="ErrorCodes.ServerError"/> with a hint to check <c>ListPayments</c>.
/// </para>
/// <para>While the chain monitor's processing is halted (NL-216) the call is refused with
/// <see cref="ErrorCodes.InvalidOperation"/> before anything is sent (the channel operations refuse every HTLC offer
/// then too; see <see cref="ChainProcessingHalt"/>).</para>
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
    public const uint MaxTimeoutSeconds = 300;

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IPaymentService _paymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PayInvoice;

    public PayInvoiceClientHandler(IPaymentService paymentService, IBlockchainMonitor? blockchainMonitor = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _paymentService = paymentService;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The request is invalid, the invoice was rejected (malformed, expired, other
    /// network, amount missing or inconsistent) or a payment for it is already in flight or succeeded
    /// (<see cref="ErrorCodes.InvalidOperation"/>). Nothing was sent in those cases. Any other failure of the payment
    /// service is <see cref="ErrorCodes.ServerError"/>: the payment may be stored <c>InFlight</c> with its HTLC offered,
    /// so the message asks the user to check <c>ListPayments</c>.</exception>
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
        if (request.MaxParts is 0 or > PaymentSendOptions.MaxPartsLimit)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The part limit must be between 1 and {PaymentSendOptions.MaxPartsLimit}.");
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("payinvoice"));

        var options = new PayInvoiceOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxFee = request.MaxFee,
            MaxParts = request.MaxParts is { } maxParts ? (int)maxParts : null
        };

        try
        {
            var result = await _paymentService.PayInvoiceAsync(request.Bolt11.Trim(), request.Amount, options, ct);
            return new PayInvoiceClientResponse(PaymentInfoClientResponse.FromModel(result.Payment))
            {
                Attempts = result.Attempts,
                Parts = result.Parts
            };
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid invoice: {e.Message}", e);
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            // The contract's "already in flight or succeeded" refusal; nothing was persisted or sent
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ClientException)
        {
            throw new ClientException(ErrorCodes.ServerError,
                                      $"The payment failed with an unexpected error: {e.Message}. It may still be "
                                    + "in flight: check listpayments before paying again.", e);
        }
    }
}