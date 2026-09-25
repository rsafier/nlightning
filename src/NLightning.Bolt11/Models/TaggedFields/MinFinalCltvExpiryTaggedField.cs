using System.Numerics;

namespace NLightning.Bolt11.Models.TaggedFields;

using Domain.Utils;
using Enums;
using Interfaces;

/// <summary>
/// Tagged field for the minimum final cltv expiry
/// </summary>
/// <remarks>
/// The minimum final cltv expiry is a 4-byte field that specifies the minimum number of blocks that the receiver should wait to claim the payment
/// </remarks>
/// <seealso cref="ITaggedField"/>
internal sealed class MinFinalCltvExpiryTaggedField : ITaggedField
{
    public TaggedFieldTypes Type => TaggedFieldTypes.MinFinalCltvExpiry;
    internal ushort Value { get; }
    public short Length { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MinFinalCltvExpiryTaggedField"/> class.
    /// </summary>
    /// <param name="value">The Expiry Time in seconds</param>
    internal MinFinalCltvExpiryTaggedField(ushort value)
    {
        Value = value;
        // Calculate the length of the field by getting the number of bits needed to represent the value plus 1,
        // then add 4 to round up to the next multiple of 5 and divide by 5 to get the number of bytes
        Length = (short)((BitOperations.Log2(Value) + 1 + 4) / 5);
    }

    /// <inheritdoc/>
    public void WriteToBitWriter(BitWriter bitWriter)
    {
        // Write data as big-endian 5-bit groups; a u16 can need 4 groups (20 bits), which is wider than the
        // 16 bits WriteUInt16AsBits accepts
        for (var i = Length - 1; i >= 0; i--)
            bitWriter.WriteByteAsBits((byte)((Value >> (i * 5)) & 0x1F), 5);
    }

    /// <inheritdoc/>
    public bool IsValid()
    {
        return Value > 0;
    }

    /// <summary>
    /// Reads a MinFinalCltvExpiryTaggedField from a BitReader
    /// </summary>
    /// <param name="bitReader">The BitReader to read from</param>
    /// <param name="length">The length of the field</param>
    /// <returns>
    /// The MinFinalCltvExpiryTaggedField, or <c>null</c> for a zero value (empty field included), which is dropped so
    /// the spec default of 18 applies
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when the length is negative or the value does not fit in 16 bits</exception>
    internal static MinFinalCltvExpiryTaggedField? FromBitReader(BitReader bitReader, short length)
    {
        if (length < 0)
            throw new ArgumentException(
                $"Invalid length for {nameof(MinFinalCltvExpiryTaggedField)}. Length must not be negative",
                nameof(length));

        // Read the data from the BitReader as big-endian 5-bit groups
        ulong value = 0;
        for (var i = 0; i < length; i++)
        {
            value = (value << 5) | bitReader.ReadByteFromBits(5);
            if (value > ushort.MaxValue)
                throw new ArgumentException(
                    $"Invalid value for {nameof(MinFinalCltvExpiryTaggedField)}. Value must fit in 16 bits",
                    nameof(length));
        }

        return value == 0 ? null : new MinFinalCltvExpiryTaggedField((ushort)value);
    }
}