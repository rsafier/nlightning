using System.Numerics;

namespace NLightning.Bolt11.Models.TaggedFields;

using Domain.Utils;
using Enums;
using Interfaces;

/// <summary>
/// Tagged field for the expiry time
/// </summary>
/// <remarks>
/// The expiry time is the time in seconds after which the invoice is invalid.
/// </remarks>
/// <seealso cref="ITaggedField"/>
public sealed class ExpiryTimeTaggedField : ITaggedField
{
    public TaggedFieldTypes Type => TaggedFieldTypes.ExpiryTime;
    internal long Value { get; }
    public short Length { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpiryTimeTaggedField"/> class.
    /// </summary>
    /// <param name="value">The Expiry Time in seconds</param>
    internal ExpiryTimeTaggedField(long value)
    {
        Value = value;
        // Minimal number of 5-bit groups that hold the value (BOLT 11: no leading 0 groups, so 0 is an empty field)
        Length = Value <= 0 ? (short)0 : (short)((BitOperations.Log2((ulong)Value) + 1 + 4) / 5);
    }

    /// <inheritdoc/>
    public void WriteToBitWriter(BitWriter bitWriter)
    {
        // Write data as big-endian 5-bit groups; `x` has no upper bound, so it can be wider than 32 bits
        for (var i = Length - 1; i >= 0; i--)
            bitWriter.WriteByteAsBits((byte)((Value >> (i * 5)) & 0x1F), 5);
    }

    /// <inheritdoc/>
    public bool IsValid()
    {
        // 0 is a valid (already expired) expiry
        return Value >= 0;
    }

    /// <summary>
    /// Reads a ExpiryTimeTaggedField from a BitReader
    /// </summary>
    /// <param name="bitReader">The BitReader to read from</param>
    /// <param name="length">The length of the field</param>
    /// <returns>The ExpiryTimeTaggedField</returns>
    /// <exception cref="ArgumentException">Thrown when the length is negative or the value does not fit in 63 bits</exception>
    internal static ExpiryTimeTaggedField FromBitReader(BitReader bitReader, short length)
    {
        if (length < 0)
            throw new ArgumentException(
                $"Invalid length for {nameof(ExpiryTimeTaggedField)}. Length must not be negative", nameof(length));

        // Read the data from the BitReader as big-endian 5-bit groups (an empty field is 0)
        long value = 0;
        for (var i = 0; i < length; i++)
        {
            if (value > long.MaxValue >> 5)
                throw new ArgumentException(
                    $"Invalid value for {nameof(ExpiryTimeTaggedField)}. Value must fit in 63 bits", nameof(length));

            value = (value << 5) | bitReader.ReadByteFromBits(5);
        }

        return new ExpiryTimeTaggedField(value);
    }
}