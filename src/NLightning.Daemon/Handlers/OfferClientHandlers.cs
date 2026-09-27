namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Interfaces;

/// <summary>
/// Creates and stores one of our BOLT 12 offers through <see cref="IOfferService"/> (<c>createoffer</c>,
/// ClientCommand 26). The returned <c>lno1...</c> string is answerable as soon as this returns.
/// </summary>
/// <remarks>
/// Errors (<see cref="ErrorCodes.InvalidOperation"/>): offers unavailable (onion messages or route blinding off, no
/// BOLT 12 signer), a zero amount, an amount without a description, an expiry in the past or out of range, or offer
/// paths needed and no peer to introduce them.
/// </remarks>
public sealed class CreateOfferClientHandler : IClientCommandHandler<CreateOfferClientRequest, CreateOfferClientResponse>
{
    /// <summary>The largest <c>offer_absolute_expiry</c> accepted: 9999-12-31T23:59:59Z.</summary>
    internal const ulong MaxAbsoluteExpiry = 253_402_300_799;

    private readonly IOfferService _offerService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.CreateOffer;

    public CreateOfferClientHandler(IOfferService offerService, TimeProvider timeProvider)
    {
        _offerService = offerService;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task<CreateOfferClientResponse> HandleAsync(CreateOfferClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_offerService.IsAvailable)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Offers are not available: they need option_onion_messages, "
                                    + "option_route_blinding and a BOLT 12 signer.");
        if (request.Amount is { IsZero: true })
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The offer amount must be positive; leave it out for an any-amount offer.");
        if (request.AbsoluteExpiry > MaxAbsoluteExpiry)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The absolute expiry {request.AbsoluteExpiry} is out of range.");

        var offerRequest = new CreateOfferRequest(request.Amount, request.Description, request.Issuer,
                                                  request.QuantityMax,
                                                  request.AbsoluteExpiry is { } expiry
                                                      ? DateTimeOffset.FromUnixTimeSeconds((long)expiry)
                                                      : null,
                                                  request.ForcePaths);
        try
        {
            var offer = await _offerService.CreateOfferAsync(offerRequest, ct);
            return new CreateOfferClientResponse(
                OfferInfoClientResponse.FromModel(offer, _timeProvider.GetUtcNow(), new OfferInvoiceCounts(0, 0)));
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid offer: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
    }
}

/// <summary>
/// Lists a page of our BOLT 12 offers, newest first, with their invoice counts (<c>listoffers</c>,
/// ClientCommand 27).
/// </summary>
public sealed class ListOffersClientHandler : IClientCommandHandler<ListOffersClientRequest, ListOffersClientResponse>
{
    private readonly IOfferService _offerService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListOffers;

    public ListOffersClientHandler(IOfferService offerService, TimeProvider timeProvider)
    {
        _offerService = offerService;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The page is invalid.</exception>
    public async Task<ListOffersClientResponse> HandleAsync(ListOffersClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);

        var offers = await _offerService.ListOffersAsync(request.ActiveOnly, request.Skip, request.Take, ct);
        var now = _timeProvider.GetUtcNow();
        var result = new List<OfferInfoClientResponse>(offers.Count);
        foreach (var offer in offers)
            result.Add(OfferInfoClientResponse.FromModel(offer, now,
                                                         await _offerService.GetInvoiceCountsAsync(offer.OfferId, ct)));

        return new ListOffersClientResponse(result);
    }
}

/// <summary>
/// Disables one of our BOLT 12 offers (<c>disableoffer</c>, ClientCommand 28): later invoice_requests
/// for it get an <c>invoice_error</c>; invoices already issued stay payable.
/// </summary>
public sealed class DisableOfferClientHandler
    : IClientCommandHandler<DisableOfferClientRequest, DisableOfferClientResponse>
{
    private readonly IOfferService _offerService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.DisableOffer;

    public DisableOfferClientHandler(IOfferService offerService, TimeProvider timeProvider)
    {
        _offerService = offerService;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">No offer has that id.</exception>
    public async Task<DisableOfferClientResponse> HandleAsync(DisableOfferClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var before = await _offerService.GetOfferAsync(request.OfferId, ct)
                  ?? throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown offer {request.OfferId}.");
        var wasActive = before.Status == OfferStatus.Active;
        var offer = await _offerService.DisableOfferAsync(request.OfferId, ct) ?? before;
        var counts = await _offerService.GetInvoiceCountsAsync(offer.OfferId, ct);
        return new DisableOfferClientResponse(OfferInfoClientResponse.FromModel(offer, _timeProvider.GetUtcNow(),
                                                                                counts),
                                              wasActive && offer.Status == OfferStatus.Disabled);
    }
}