namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Interfaces;

/// <summary>
/// Fetches and verifies an invoice for a BOLT 12 offer without paying it (ClientCommand 30, <c>fetchinvoice</c>,
/// provisional).
/// </summary>
/// <remarks>
/// The payment limits of the request are ignored. A malformed, expired or unpayable offer, or a bad amount or quantity,
/// is <see cref="ErrorCodes.InvalidOperation"/> (nothing sent); a fetch that got no invoice is a normal response with
/// its status (<c>InvoiceError</c>, <c>TimedOut</c>, <c>Unreachable</c>, <c>InvalidInvoice</c>).
/// </remarks>
public sealed class FetchInvoiceClientHandler
    : IClientCommandHandler<PayOfferClientRequest, FetchInvoiceClientResponse>
{
    private readonly IOfferPaymentService _offerPaymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.FetchInvoice;

    public FetchInvoiceClientHandler(IOfferPaymentService offerPaymentService)
    {
        _offerPaymentService = offerPaymentService;
    }

    /// <inheritdoc/>
    public async Task<FetchInvoiceClientResponse> HandleAsync(PayOfferClientRequest request, CancellationToken ct)
    {
        var payRequest = ToPayOfferRequest(request);
        try
        {
            var result = await _offerPaymentService.FetchInvoiceAsync(payRequest, new PayOfferOptions(), ct);
            return FetchInvoiceClientResponse.FromResult(result);
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid offer or request: {e.Message}", e);
        }
    }

    /// <summary>
    /// The service request of a client request, after the checks both commands share.
    /// </summary>
    /// <exception cref="ClientException">An empty offer, a zero amount or quantity, or an empty note.</exception>
    internal static PayOfferRequest ToPayOfferRequest(PayOfferClientRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Offer))
            throw new ClientException(ErrorCodes.InvalidOperation, "The offer is empty.");
        if (request.Amount is { IsZero: true })
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be positive.");
        if (request.Quantity is 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The quantity must be positive.");
        if (request.PayerNote is { Length: 0 })
            throw new ClientException(ErrorCodes.InvalidOperation, "The payer note is empty.");

        return new PayOfferRequest(request.Offer.Trim(), request.Amount, request.Quantity, request.PayerNote);
    }
}