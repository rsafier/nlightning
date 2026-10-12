namespace NLightning.Domain.LiquidityAds.Models;

using Enums;

/// <summary>
/// The <c>payment_details</c> of a liquidity request: a TLV whose type is the payment type. Only
/// <see cref="LiquidityPaymentType.FromChannelBalance"/> (empty) is used by this node; the on-the-fly types carry a list
/// of 32-byte payment hashes or preimages, kept raw.
/// </summary>
public sealed record LiquidityPaymentDetails
{
    private LiquidityPaymentDetails(ulong type, byte[] value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>The payment details that pay the fee from the buyer's channel balance.</summary>
    public static LiquidityPaymentDetails FromChannelBalance { get; } = new(0, []);

    /// <summary>The payment type (the TLV type, also its bit index in <c>payment_types</c>).</summary>
    public ulong Type { get; }

    /// <summary>The raw value of the record (empty for <see cref="LiquidityPaymentType.FromChannelBalance"/>).</summary>
    public byte[] Value { get; }

    /// <summary>The payment type, when it is one this node knows.</summary>
    public LiquidityPaymentType? KnownType =>
        Enum.IsDefined(typeof(LiquidityPaymentType), (int)Math.Min(Type, int.MaxValue))
            ? (LiquidityPaymentType)(int)Type
            : null;

    /// <summary>Creates payment details of any type (decoding, tests).</summary>
    public static LiquidityPaymentDetails Create(ulong type, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return type == 0 && value.Length == 0 ? FromChannelBalance : new LiquidityPaymentDetails(type, value.ToArray());
    }

    /// <inheritdoc />
    public bool Equals(LiquidityPaymentDetails? other) =>
        other is not null && Type == other.Type && Value.AsSpan().SequenceEqual(other.Value);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, Value.Length);
}