namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The offer <c>createoffer</c> stored.
/// </summary>
public sealed class CreateOfferClientResponse(OfferInfoClientResponse offer)
{
    public OfferInfoClientResponse Offer { get; } = offer;
}

/// <summary>
/// Our offers, newest first, with their invoice counts (<c>listoffers</c>).
/// </summary>
public sealed class ListOffersClientResponse(IReadOnlyList<OfferInfoClientResponse> offers)
{
    public IReadOnlyList<OfferInfoClientResponse> Offers { get; } = offers;
}

/// <summary>
/// The offer after <c>disableoffer</c>.
/// </summary>
/// <param name="offer">The offer as stored.</param>
/// <param name="changed">False when it was already disabled or expired (nothing changed).</param>
public sealed class DisableOfferClientResponse(OfferInfoClientResponse offer, bool changed)
{
    public OfferInfoClientResponse Offer { get; } = offer;

    public bool Changed { get; } = changed;
}