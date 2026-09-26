namespace NLightning.Bolt11.Models.TaggedFields;

using Domain.Enums;
using Domain.Node;
using Domain.Utils;
using Enums;
using Interfaces;

/// <summary>
/// Tagged field for features
/// </summary>
/// <remarks>
/// The features are a collection of features that are supported by the node.
/// The field is big-endian: the last bit of the field is feature bit 0.
/// </remarks>
/// <seealso cref="ITaggedField"/>
internal sealed class FeaturesTaggedField : ITaggedField
{
    public TaggedFieldTypes Type => TaggedFieldTypes.Features;
    internal FeatureSet Value { get; }

    /// <summary>
    /// Minimal number of 5-bit groups that hold every set bit (recomputed, so later changes to
    /// <see cref="Value"/> are always encoded)
    /// </summary>
    public short Length => (short)((Value.SizeInBits + 1 + 4) / 5);

    /// <summary>
    /// Initializes a new instance of the <see cref="FeaturesTaggedField"/> class.
    /// </summary>
    /// <param name="value">The features</param>
    internal FeaturesTaggedField(FeatureSet value)
    {
        Value = value;
    }

    /// <inheritdoc/>
    public void WriteToBitWriter(BitWriter bitWriter)
    {
        // Write data
        Value.WriteToBitWriter(bitWriter, Length * 5, false);
    }

    /// <inheritdoc/>
    public bool IsValid()
    {
        return true;
    }

    /// <summary>
    /// Reads a FeaturesTaggedField from a BitReader
    /// </summary>
    /// <param name="bitReader">The BitReader to read from</param>
    /// <param name="length">The length of the field</param>
    /// <returns>The FeaturesTaggedField</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the length is invalid or when an unknown even (required) feature bit is set (BOLT 11)
    /// </exception>
    internal static FeaturesTaggedField FromBitReader(BitReader bitReader, short length)
    {
        if (length <= 0)
            throw new ArgumentException(
                $"Invalid length for {nameof(FeaturesTaggedField)}. Length must be greater than 0",
                nameof(length));

        var features = FeatureSet.DeserializeFromBitReader(bitReader, length * 5, false);

        // BOLT 11: unknown odd bits are ignored, unknown even bits MUST fail the payment
        var unknownRequiredBits = GetUnknownRequiredBits(features);
        if (unknownRequiredBits.Count > 0)
            throw new ArgumentException(
                $"Invoice requires unknown feature bit(s): {string.Join(", ", unknownRequiredBits)}",
                nameof(bitReader));

        return new FeaturesTaggedField(features);
    }

    /// <summary>
    /// Gets the even (compulsory) bits that are set but don't belong to any known <see cref="Feature"/>
    /// </summary>
    internal static List<int> GetUnknownRequiredBits(FeatureSet features)
    {
        var unknownBits = new List<int>();
        for (var bit = 0; bit <= features.SizeInBits; bit += 2)
        {
            if (features.IsFeatureSet(bit, false) && !Enum.IsDefined((Feature)(bit + 1)))
                unknownBits.Add(bit);
        }

        return unknownBits;
    }
}