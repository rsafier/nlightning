namespace NLightning.Domain.LiquidityAds.Models;

using Enums;

/// <summary>
/// The rates a seller offers and the payment types it accepts (BOLT PR #1153 <c>will_fund_rates</c>, in <c>init</c>
/// and <c>node_announcement</c>).
/// </summary>
public sealed record WillFundRates
{
    public WillFundRates(IReadOnlyList<FundingRate> rates, byte[] encodedPaymentTypes)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(encodedPaymentTypes);
        if (rates.Count > ushort.MaxValue)
            throw new ArgumentException("More than 65535 funding rates", nameof(rates));
        if (encodedPaymentTypes.Length > ushort.MaxValue)
            throw new ArgumentException("The payment types are longer than 65535 bytes", nameof(encodedPaymentTypes));

        Rates = rates.ToArray();
        EncodedPaymentTypes = encodedPaymentTypes.ToArray();
    }

    /// <summary>The rates, in the seller's order.</summary>
    public IReadOnlyList<FundingRate> Rates { get; }

    /// <summary>The payment types as a big-endian bitfield (bit i is byte <c>Length - 1 - i / 8</c>, bit
    /// <c>i % 8</c>), as received.</summary>
    public byte[] EncodedPaymentTypes { get; }

    /// <summary>Creates rates with the bitfield of <paramref name="paymentTypes"/> in its shortest form (Eclair's
    /// encoding).</summary>
    public static WillFundRates Create(IReadOnlyList<FundingRate> rates, IEnumerable<LiquidityPaymentType> paymentTypes)
    {
        ArgumentNullException.ThrowIfNull(paymentTypes);
        var indexes = paymentTypes.Select(t => (int)t).Distinct().ToArray();
        if (indexes.Length == 0)
            return new WillFundRates(rates, []);

        var encoded = new byte[indexes.Max() / 8 + 1];
        foreach (var index in indexes)
            encoded[encoded.Length - 1 - index / 8] |= (byte)(1 << (index % 8));
        return new WillFundRates(rates, encoded);
    }

    /// <summary>Whether the seller accepts payment type <paramref name="bitIndex"/>.</summary>
    public bool SupportsBit(ulong bitIndex)
    {
        if (bitIndex >= (ulong)EncodedPaymentTypes.Length * 8)
            return false;

        var index = (int)bitIndex;
        return (EncodedPaymentTypes[EncodedPaymentTypes.Length - 1 - index / 8] & (1 << (index % 8))) != 0;
    }

    /// <summary>Whether the seller accepts <paramref name="paymentType"/>.</summary>
    public bool Supports(LiquidityPaymentType paymentType) => SupportsBit((ulong)paymentType);

    /// <summary>The first rate that sells <paramref name="requestedSat"/> (Eclair's <c>findRate</c>).</summary>
    public FundingRate? FindRate(ulong requestedSat)
    {
        foreach (var rate in Rates)
            if (rate.IsCompatible(requestedSat))
                return rate;
        return null;
    }

    /// <inheritdoc />
    public bool Equals(WillFundRates? other) =>
        other is not null && Rates.SequenceEqual(other.Rates)
     && EncodedPaymentTypes.AsSpan().SequenceEqual(other.EncodedPaymentTypes);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Rates.Count, EncodedPaymentTypes.Length);
}