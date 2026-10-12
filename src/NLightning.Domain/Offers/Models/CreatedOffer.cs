namespace NLightning.Domain.Offers.Models;

/// <summary>
/// The offer <see cref="Interfaces.IOfferService.CreateOfferAsync"/> stored, with any reachability warning (NL-452).
/// </summary>
/// <param name="Offer">The offer as stored.</param>
/// <param name="Warning">Why payers may not reach the offer: its paths are introduced by peers without an open
/// channel with us, and we never reconnect to those, or null when no such risk was seen at creation.</param>
public sealed record CreatedOffer(OfferModel Offer, string? Warning);