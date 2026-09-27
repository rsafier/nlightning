namespace NLightning.Domain.Offers.Validators;

using Encoding;
using Protocol.Constants;
using Protocol.ValueObjects;

/// <summary>
/// The BOLT 12 offer reader's "MUST NOT respond" rules (B12-OFR-03), on an offer that already parsed
/// (<see cref="Offer.TryParse(string?, out Offer?, out Bolt12Violation?)"/> enforces the format rules).
/// </summary>
public static class OfferValidator
{
    /// <summary>
    /// The first reader rule <paramref name="offer"/> breaks, or null.
    /// </summary>
    /// <param name="offer">The offer.</param>
    /// <param name="now">The current time for <c>offer_absolute_expiry</c>, or null to skip the expiry check.</param>
    /// <param name="supportedChains">The chains we accept invoices on, or null to skip the chain check (an offer
    /// without <c>offer_chains</c> is for bitcoin mainnet).</param>
    public static Bolt12Violation? Validate(Offer offer, DateTimeOffset? now = null,
                                            IReadOnlyCollection<ChainHash>? supportedChains = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var fields = offer.Fields;

        if (fields.Features is { } features
         && Bolt12FieldCodec.FindUnknownEvenBit(features.Span) is { } bit)
            return Violation($"offer_features sets the unknown even bit {bit}.");

        if (fields.Chains is { Count: 0 })
            return Violation("offer_chains has no entry.");

        if (supportedChains is not null)
        {
            var chains = fields.Chains ?? [ChainConstants.Main];
            if (!chains.Any(supportedChains.Contains))
                return Violation("None of the offer's chains is supported.");
        }

        if (fields.Amount is not null && fields.Description is null)
            return Violation("offer_amount is set without offer_description.");

        if (fields.Amount is 0)
            return Violation("offer_amount is zero.");

        if (fields.Currency is not null && fields.Amount is null)
            return Violation("offer_currency is set without offer_amount.");

        if (fields.IssuerId is null && fields.Paths is null)
            return Violation("The offer has neither offer_issuer_id nor offer_paths.");

        if (fields.Paths is { Count: 0 })
            return Violation("offer_paths has no path.");

        if (now is { } time && fields.AbsoluteExpiry is { } expiry && (ulong)Math.Max(0, time.ToUnixTimeSeconds()) > expiry)
            return Violation("The offer has expired.");

        return null;
    }

    /// <summary>
    /// Parses an <c>lno1...</c> string and applies <see cref="Validate(Offer, DateTimeOffset?, IReadOnlyCollection{ChainHash}?)"/>.
    /// </summary>
    /// <returns>The first violated requirement (format or reader rule), or null with the parsed offer.</returns>
    public static Bolt12Violation? Validate(string? bolt12, out Offer? offer, DateTimeOffset? now = null,
                                            IReadOnlyCollection<ChainHash>? supportedChains = null)
    {
        if (!Offer.TryParse(bolt12, out offer, out var violation))
            return violation;

        violation = Validate(offer, now, supportedChains);
        if (violation is not null)
            offer = null;

        return violation;
    }

    private static Bolt12Violation Violation(string reason) => new(Bolt12RequirementIds.OfferReader, reason);
}