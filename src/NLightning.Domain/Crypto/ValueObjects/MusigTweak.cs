namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;

/// <summary>
/// A BIP 327 tweak of an aggregate key: a 32-byte big-endian scalar, applied as an x-only tweak (BIP 341 taproot
/// tweaks such as BIP 86's) or as a plain tweak (BIP 32 derivation).
/// </summary>
/// <remarks>
/// Only the length is checked here; a value at or above the curve order is refused when the tweak is applied.
/// </remarks>
public readonly record struct MusigTweak
{
    private readonly byte[] _value;

    /// <summary>
    /// The 32 bytes of the tweak.
    /// </summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>
    /// Whether the tweak is x-only (BIP 327 <c>is_xonly_t</c>).
    /// </summary>
    public bool IsXOnly { get; }

    public MusigTweak(ReadOnlySpan<byte> value, bool isXOnly)
    {
        if (value.Length != MusigConstants.TweakLen)
            throw new ArgumentException($"A MuSig2 tweak must be {MusigConstants.TweakLen} bytes.", nameof(value));

        _value = value.ToArray();
        IsXOnly = isXOnly;
    }

    public bool Equals(MusigTweak other) =>
        IsXOnly == other.IsXOnly && Value.Span.SequenceEqual(other.Value.Span);

    public override int GetHashCode() => HashCode.Combine(IsXOnly, Convert.ToHexString(Value.Span));

    public override string ToString() => $"{(IsXOnly ? "x-only" : "plain")}:{Convert.ToHexStringLower(Value.Span)}";
}