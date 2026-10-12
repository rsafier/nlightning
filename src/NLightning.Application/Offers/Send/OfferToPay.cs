using System.Text;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Protocol.Constants;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// An offer (<c>lno1...</c>) read and checked by the payer (BOLT 12 "Offers" reader, B12-OFR-03): what an
/// invoice_request for it needs.
/// </summary>
/// <remarks>
/// Immutable. <see cref="Parse"/> refuses every offer a reader "MUST NOT respond to"; the messages start with the
/// requirement id they break.
/// </remarks>
public sealed class OfferToPay
{
    private static readonly ulong[] s_knownEvenTypes =
    [
        Bolt12TlvTypes.OfferChains, Bolt12TlvTypes.OfferMetadata, Bolt12TlvTypes.OfferCurrency,
        Bolt12TlvTypes.OfferAmount, Bolt12TlvTypes.OfferDescription, Bolt12TlvTypes.OfferFeatures,
        Bolt12TlvTypes.OfferAbsoluteExpiry, Bolt12TlvTypes.OfferPaths, Bolt12TlvTypes.OfferIssuer,
        Bolt12TlvTypes.OfferQuantityMax, Bolt12TlvTypes.OfferIssuerId
    ];

    private OfferToPay(string text, Bolt12TlvStream stream)
    {
        Text = text;
        Stream = stream;
    }

    /// <summary>The offer string as given.</summary>
    public string Text { get; }

    /// <summary>The offer's TLV records, as encoded (an invoice_request copies them all).</summary>
    public Bolt12TlvStream Stream { get; }

    /// <summary><c>offer_chains</c>, or null when absent (bitcoin only).</summary>
    public IReadOnlyList<ChainHash>? Chains { get; private init; }

    /// <summary><c>offer_amount</c> (msat, or in <see cref="Currency"/> units), or null.</summary>
    public ulong? Amount { get; private init; }

    /// <summary><c>offer_currency</c>, or null for msat.</summary>
    public string? Currency { get; private init; }

    /// <summary><c>offer_description</c>, or null.</summary>
    public string? Description { get; private init; }

    /// <summary><c>offer_issuer</c>, or null.</summary>
    public string? Issuer { get; private init; }

    /// <summary><c>offer_absolute_expiry</c>, or null.</summary>
    public DateTimeOffset? AbsoluteExpiry { get; private init; }

    /// <summary><c>offer_quantity_max</c>, or null for a single item (0: unlimited).</summary>
    public ulong? QuantityMax { get; private init; }

    /// <summary><c>offer_issuer_id</c>, or null.</summary>
    public CompactPubKey? IssuerId { get; private init; }

    /// <summary><c>offer_paths</c> (empty when absent).</summary>
    public IReadOnlyList<WireBlindedPath> Paths { get; private init; } = [];

    /// <summary>
    /// Reads and checks an offer for a node on <paramref name="chain"/> at <paramref name="now"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The string is not an offer, or an offer we must not respond to.</exception>
    public static OfferToPay Parse(string text, ChainHash chain, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Bolt12TlvStream stream;
        try
        {
            stream = Bolt12TlvStream.Parse(Bolt12Bech32.Decode(text, Bolt12Constants.OfferHrp));
        }
        catch (FormatException e)
        {
            throw new ArgumentException($"B12-ENC-02: not an offer string: {e.Message}", nameof(text), e);
        }

        foreach (var record in stream.Records)
        {
            if (!IsOfferType(record.Type))
                throw new ArgumentException($"B12-OFR-03: TLV type {record.Type} is outside the offer ranges.",
                                            nameof(text));
            if (record.Type % 2 == 0 && !s_knownEvenTypes.Contains(record.Type))
                throw new ArgumentException($"B12-ENC-03: unknown even TLV type {record.Type}.", nameof(text));
        }

        IReadOnlyList<ChainHash>? chains = null;
        if (stream.TryGetValue(Bolt12TlvTypes.OfferChains, out var chainsValue))
        {
            if (chainsValue.Length == 0 || chainsValue.Length % 32 != 0)
                throw new ArgumentException("B12-OFR-03: offer_chains is empty or not a list of chain hashes.",
                                            nameof(text));
            chains = Enumerable.Range(0, chainsValue.Length / 32)
                               .Select(i => new ChainHash(chainsValue.Span.Slice(i * 32, 32)))
                               .ToList();
            if (!chains.Contains(chain))
                throw new ArgumentException("B12-OFR-03: the offer is not for our chain.", nameof(text));
        }
        else if (chain != ChainConstants.Main)
        {
            throw new ArgumentException("B12-OFR-03: the offer is for bitcoin (no offer_chains), not our chain.",
                                        nameof(text));
        }

        if (stream.TryGetValue(Bolt12TlvTypes.OfferFeatures, out var features) && HasEvenBit(features.Span))
            throw new ArgumentException("B12-OFR-03: offer_features sets an unknown even bit.", nameof(text));

        var amount = ReadTu64(stream, Bolt12TlvTypes.OfferAmount);
        var currency = ReadUtf8(stream, Bolt12TlvTypes.OfferCurrency);
        var description = ReadUtf8(stream, Bolt12TlvTypes.OfferDescription);
        if (amount is 0)
            throw new ArgumentException("B12-OFR-03: offer_amount is zero.", nameof(text));
        if (amount is not null && description is null)
            throw new ArgumentException("B12-OFR-03: offer_amount without offer_description.", nameof(text));
        if (currency is not null && amount is null)
            throw new ArgumentException("B12-OFR-03: offer_currency without offer_amount.", nameof(text));

        CompactPubKey? issuerId = null;
        if (stream.TryGetValue(Bolt12TlvTypes.OfferIssuerId, out var issuerValue))
        {
            if (issuerValue.Length != 33 || issuerValue.Span[0] is not (2 or 3))
                throw new ArgumentException("B12-OFR-03: offer_issuer_id is not a point.", nameof(text));
            issuerId = new CompactPubKey(issuerValue.ToArray());
        }

        IReadOnlyList<WireBlindedPath> paths = [];
        if (stream.TryGetValue(Bolt12TlvTypes.OfferPaths, out var pathsValue))
        {
            if (!BlindedPathCodec.TryReadList(pathsValue.Span, out var read, out var reason) || read.Count == 0)
                throw new ArgumentException($"B12-OFR-03: offer_paths is invalid ({reason ?? "no path"}).",
                                            nameof(text));
            paths = read;
        }

        if (issuerId is null && paths.Count == 0)
            throw new ArgumentException("B12-OFR-03: the offer has neither offer_issuer_id nor offer_paths.",
                                        nameof(text));

        DateTimeOffset? expiry = null;
        if (ReadTu64(stream, Bolt12TlvTypes.OfferAbsoluteExpiry) is { } seconds)
        {
            expiry = seconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                         ? DateTimeOffset.MaxValue
                         : DateTimeOffset.FromUnixTimeSeconds((long)seconds);
            if (now > expiry)
                throw new ArgumentException($"B12-OFR-03: the offer expired at {expiry:u}.", nameof(text));
        }

        return new OfferToPay(text, stream)
        {
            Chains = chains,
            Amount = amount,
            Currency = currency,
            Description = description,
            Issuer = ReadUtf8(stream, Bolt12TlvTypes.OfferIssuer),
            AbsoluteExpiry = expiry,
            QuantityMax = ReadTu64(stream, Bolt12TlvTypes.OfferQuantityMax),
            IssuerId = issuerId,
            Paths = paths
        };
    }

    /// <summary>
    /// BOLT 12 offer TLV ranges: 1 to 79 and 1,000,000,000 to 1,999,999,999.
    /// </summary>
    public static bool IsOfferType(ulong type) => type is >= 1 and <= 79 or >= 1_000_000_000 and <= 1_999_999_999;

    /// <summary>
    /// Whether a big-endian feature bit field sets any even bit (no BOLT 12 even feature is known to us).
    /// </summary>
    internal static bool HasEvenBit(ReadOnlySpan<byte> features)
    {
        foreach (var b in features)
            if ((b & 0x55) != 0)
                return true;
        return false;
    }

    internal static ulong? ReadTu64(Bolt12TlvStream stream, ulong type)
    {
        if (!stream.TryGetValue(type, out var value))
            return null;
        if (!TruncatedInt.TryDecodeTu64(value.Span, out var result))
            throw new ArgumentException($"B12-ENC-03: TLV type {type} is not a minimal tu64.", "text");
        return result;
    }

    internal static string? ReadUtf8(Bolt12TlvStream stream, ulong type)
    {
        if (!stream.TryGetValue(type, out var value))
            return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(value.Span);
        }
        catch (DecoderFallbackException e)
        {
            throw new ArgumentException($"B12-ENC-03: TLV type {type} is not UTF-8.", "text", e);
        }
    }
}