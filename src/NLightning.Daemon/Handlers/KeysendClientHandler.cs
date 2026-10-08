namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Sends a spontaneous (keysend) payment through <see cref="IPaymentService.PayKeysendAsync"/> and waits for the
/// outcome (ClientCommand 31, <c>keysend</c>; lane lh1-l3).
/// </summary>
/// <remarks>
/// The wait is bounded as for <c>payinvoice</c> (<see cref="PayInvoiceClientHandler.DefaultTimeoutSeconds"/>, at most
/// <see cref="PayInvoiceClientHandler.MaxTimeoutSeconds"/>); a payment still in flight when it ends is reported as such.
/// A zero amount, our own node id, an invalid custom record or an out-of-range option is
/// <see cref="ErrorCodes.InvalidOperation"/> (nothing sent), and so is a halted chain monitor (NL-216). Any other
/// failure is <see cref="ErrorCodes.ServerError"/> with a hint to check <c>listpayments</c>.
/// </remarks>
public sealed class KeysendClientHandler : IClientCommandHandler<KeysendClientRequest, PayInvoiceClientResponse>
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IPaymentService _paymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.Keysend;

    public KeysendClientHandler(IPaymentService paymentService, IBlockchainMonitor? blockchainMonitor = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _paymentService = paymentService;
    }

    /// <inheritdoc/>
    public async Task<PayInvoiceClientResponse> HandleAsync(KeysendClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is null || request.Amount.IsZero)
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be positive.");

        var timeoutSeconds = request.TimeoutSeconds ?? PayInvoiceClientHandler.DefaultTimeoutSeconds;
        if (timeoutSeconds is 0 or > PayInvoiceClientHandler.MaxTimeoutSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The timeout must be between 1 and "
                                    + $"{PayInvoiceClientHandler.MaxTimeoutSeconds} seconds.");

        IReadOnlyList<CustomRecord> customRecords;
        try
        {
            customRecords = CustomRecordCodec.Validate(request.CustomRecords);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid custom record: {e.Message}", e);
        }

        var labels = SourceLabelsGuard.Check(request.Label, request.Tags);
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("keysend"));

        var options = new PayInvoiceOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxFee = request.MaxFee,
            MaxParts = 1,
            Labels = labels
        };

        try
        {
            var result = await _paymentService.PayKeysendAsync(
                             new PayKeysendRequest(request.Destination, request.Amount)
                             {
                                 Preimage = request.Preimage,
                                 CustomRecords = customRecords
                             }, options, ct);
            return new PayInvoiceClientResponse(PaymentInfoClientResponse.FromModel(result.Payment))
            {
                Attempts = result.Attempts,
                Parts = result.Parts
            };
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid keysend: {e.Message}", e);
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            // Nothing was persisted or sent (no block processed yet, or a hash already paid)
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ClientException)
        {
            throw new ClientException(ErrorCodes.ServerError,
                                      $"The keysend failed with an unexpected error: {e.Message}. It may still be in "
                                    + "flight: check listpayments.", e);
        }
    }
}