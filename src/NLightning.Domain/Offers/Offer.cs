using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers;

using Constants;
using Encoding;
using Validators;

/// <summary>
/// A BOLT 12 offer (<c>lno1...</c>): its raw TLV stream and typed fields.
/// </summary>
/// <remarks>
/// Parsing enforces the format: the BOLT 1 stream rules, the offer ranges (1-79 and 1,000,000,000-1,999,999,999,
/// B12-ENC-04), no unknown even type (B12-ENC-03) and every known field's value format (UTF-8, points on the curve,
/// <c>blinded_path</c>s, <c>chain_hash</c> lengths). The semantic reader rules (amount, description, issuer or paths,
/// features, chains, expiry) are <see cref="OfferValidator"/>'s. Unknown odd fields are kept in <see cref="Stream"/>.
/// </remarks>
public sealed class Offer
{
    /// <summary>
    /// The offer's TLV stream, exactly as received or built.
    /// </summary>
    public Bolt12TlvStream Stream { get; }

    /// <summary>
    /// The typed fields.
    /// </summary>
    public OfferFields Fields { get; }

    private Offer(Bolt12TlvStream stream, OfferFields fields)
    {
        Stream = stream;
        Fields = fields;
    }

    /// <summary>
    /// The <c>lno1...</c> string (all lowercase, or all uppercase for QR codes).
    /// </summary>
    public string ToBolt12String(bool uppercase = false) =>
        Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, Stream.Encode(), uppercase);

    public override string ToString() => ToBolt12String();

    /// <summary>
    /// Reads an offer from its TLV stream.
    /// </summary>
    public static bool TryParse(Bolt12TlvStream stream, [NotNullWhen(true)] out Offer? offer,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        offer = null;
        try
        {
            Bolt12TlvRanges.CheckTypes(stream, Bolt12TlvRanges.IsOfferField, Bolt12TlvRanges.OfferTypes, "offer");
            offer = new Offer(stream, OfferFields.Read(stream, Bolt12RequirementIds.OfferReader));
            violation = null;
            return true;
        }
        catch (Bolt12FormatException e)
        {
            violation = e.Violation;
            return false;
        }
    }

    /// <summary>
    /// Reads an offer from its TLV bytes.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out Offer? offer,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        offer = null;
        if (!Bolt12TlvStream.TryParse(bytes, out var stream, out var reason))
        {
            violation = new Bolt12Violation(Bolt12RequirementIds.TlvStream, reason);
            return false;
        }

        return TryParse(stream, out offer, out violation);
    }

    /// <summary>
    /// Reads an <c>lno1...</c> string (either case, <c>+</c> continuations allowed).
    /// </summary>
    public static bool TryParse(string? bolt12, [NotNullWhen(true)] out Offer? offer,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        offer = null;
        if (!Bolt12String.TryDecode(bolt12, [Bolt12Constants.OfferHrp], out var data, out violation))
            return false;

        return TryParse(data, out offer, out violation);
    }

    /// <summary>
    /// Reads an <c>lno1...</c> string.
    /// </summary>
    /// <exception cref="FormatException">The string is not a well-formed offer.</exception>
    public static Offer Parse(string bolt12) =>
        TryParse(bolt12, out var offer, out var violation)
            ? offer
            : throw new FormatException(violation.ToString());

    /// <summary>
    /// Reads an offer's TLV stream.
    /// </summary>
    /// <exception cref="FormatException">The stream is not a well-formed offer.</exception>
    public static Offer Parse(Bolt12TlvStream stream) =>
        TryParse(stream, out var offer, out var violation)
            ? offer
            : throw new FormatException(violation.ToString());
}