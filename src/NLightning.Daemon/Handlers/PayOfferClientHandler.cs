namespace NLightning.Daemon.Handlers;

using Application.Payments.Send;
using Domain.Bitcoin.Constants;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Fetches an invoice for a BOLT 12 offer and pays it through <see cref="IOfferPaymentService"/>
/// (ClientCommand 29, <c>payoffer</c>, provisional).
/// </summary>
/// <remarks>
/// The fetch takes at most <see cref="PayOfferOptions.MaxFetchAttempts"/> x <see cref="PayOfferOptions.FetchTimeout"/>
/// (the defaults: 3 x 30 s); the payment wait is bounded as for <c>payinvoice</c>
/// (<see cref="PayInvoiceClientHandler.DefaultTimeoutSeconds"/>, at most
/// <see cref="PayInvoiceClientHandler.MaxTimeoutSeconds"/>), and a payment still in flight when it ends is reported as
/// such. A malformed, expired or unpayable offer, a bad amount or quantity, or a payment hash already in flight or paid
/// is <see cref="ErrorCodes.InvalidOperation"/> (nothing sent); a fetch that got no invoice is a normal response with
/// its status. Any other failure is <see cref="ErrorCodes.ServerError"/> with a hint to check <c>listpayments</c>.
/// </remarks>
public sealed class PayOfferClientHandler : IClientCommandHandler<PayOfferClientRequest, PayOfferClientResponse>
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IOfferPaymentService _offerPaymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PayOffer;

    public PayOfferClientHandler(IOfferPaymentService offerPaymentService, IBlockchainMonitor? blockchainMonitor = null)
    {
        _offerPaymentService = offerPaymentService;
        _blockchainMonitor = blockchainMonitor;
    }

    /// <inheritdoc/>
    public async Task<PayOfferClientResponse> HandleAsync(PayOfferClientRequest request, CancellationToken ct)
    {
        var payRequest = FetchInvoiceClientHandler.ToPayOfferRequest(request);
        var timeoutSeconds = request.TimeoutSeconds ?? PayInvoiceClientHandler.DefaultTimeoutSeconds;
        if (timeoutSeconds is 0 or > PayInvoiceClientHandler.MaxTimeoutSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The timeout must be between 1 and {PayInvoiceClientHandler.MaxTimeoutSeconds} "
                                    + "seconds.");
        if (request.MaxParts is 0 or > PaymentSendOptions.MaxPartsLimit)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The part limit must be between 1 and {PaymentSendOptions.MaxPartsLimit}.");
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("payoffer"));

        var options = new PayOfferOptions
        {
            Payment = new PayInvoiceOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                MaxFee = request.MaxFee,
                MaxParts = request.MaxParts is { } maxParts ? (int)maxParts : null
            }
        };

        try
        {
            var result = await _offerPaymentService.PayOfferAsync(payRequest, options, ct);
            return new PayOfferClientResponse(FetchInvoiceClientResponse.FromResult(result.Fetch),
                                              result.Payment is { } payment
                                                  ? PaymentInfoClientResponse.FromModel(payment.Payment)
                                                  : null)
            {
                Attempts = result.Payment?.Attempts ?? 0,
                Parts = result.Payment?.Parts ?? 0
            };
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid offer or request: {e.Message}", e);
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ClientException)
        {
            throw new ClientException(ErrorCodes.ServerError,
                                      $"The offer payment failed with an unexpected error: {e.Message}. It may still be "
                                    + "in flight: check listpayments before paying again.", e);
        }
    }
}