namespace NLightning.Domain.Payments.Keysend;

/// <summary>
/// One application record of a final hop payload (a type of at least 65536, <see cref="CustomRecordCodec.MinType"/>):
/// what a keysend or boostagram payer attaches for the payee's application.
/// </summary>
/// <remarks>Equality compares the type and the value bytes.</remarks>
public sealed class CustomRecord : IEquatable<CustomRecord>
{
    /// <summary>The TLV type.</summary>
    public ulong Type { get; }

    /// <summary>The value bytes (a copy of what was given).</summary>
    public ReadOnlyMemory<byte> Value { get; }

    public CustomRecord(ulong type, ReadOnlySpan<byte> value)
    {
        Type = type;
        Value = value.ToArray();
    }

    public bool Equals(CustomRecord? other) =>
        other is not null && Type == other.Type && Value.Span.SequenceEqual(other.Value.Span);

    public override bool Equals(object? obj) => obj is CustomRecord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Type, Value.Length);

    public override string ToString() => $"{Type}={Convert.ToHexString(Value.Span).ToLowerInvariant()}";
}