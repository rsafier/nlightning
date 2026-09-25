namespace NLightning.Bolt11.Models.TaggedFields;

using Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Utils;
using Enums;
using Interfaces;

/// <summary>
/// Tagged field for routing information
/// </summary>
/// <remarks>
/// The routing information is a collection of routing information entries.
/// Each entry contains the public key of the node, the short channel id, the base fee in msat, the fee proportional
/// millionths, and the cltv expiry delta.
/// </remarks>
/// <seealso cref="ITaggedField"/>
internal sealed class RoutingInfoTaggedField : ITaggedField
{
    public TaggedFieldTypes Type => TaggedFieldTypes.RoutingInfo;
    internal RoutingInfoCollection Value { get; }
    public short Length { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DescriptionHashTaggedField"/> class.
    /// </summary>
    /// <param name="value">The Description Hash</param>
    internal RoutingInfoTaggedField(RoutingInfoCollection value)
    {
        Value = value;
        Length = CalculateLength(value.Count);

        Value.Changed += OnRoutingInfoCollectionChanged;
    }

    /// <inheritdoc/>
    public void WriteToBitWriter(BitWriter bitWriter)
    {
        // Write data
        foreach (var routingInfo in Value)
        {
            bitWriter.WriteBits(routingInfo.CompactPubKey, 264);
            bitWriter.WriteBits(routingInfo.ShortChannelId, 64);
            bitWriter.WriteInt32AsBits(unchecked((int)routingInfo.FeeBaseMsat), 32);
            bitWriter.WriteInt32AsBits(unchecked((int)routingInfo.FeeProportionalMillionths), 32);
            bitWriter.WriteUInt16AsBits(routingInfo.CltvExpiryDelta, 16);
        }

        // Pad to the 5-bit boundary with zeros
        for (var i = Value.Count * TaggedFieldConstants.RoutingInfoLength; i < Length * 5; i++)
            bitWriter.WriteBit(false);
    }

    /// <inheritdoc/>
    public bool IsValid()
    {
        // All numeric fields are unsigned (u32/u16) and every value is valid
        return true;
    }

    /// <summary>
    /// Create a new instance of the <see cref="RoutingInfoTaggedField"/> from a <see cref="BitReader"/>
    /// </summary>
    /// <param name="bitReader">The bit reader to read from</param>
    /// <param name="length">The length of the tagged field</param>
    /// <returns>A new instance of the <see cref="RoutingInfoTaggedField"/></returns>
    /// <exception cref="ArgumentException">If the length is not a whole number of 408-bit entries</exception>
    internal static RoutingInfoTaggedField FromBitReader(BitReader bitReader, short length)
    {
        var l = length * 5;

        // Each entry is 51 bytes (408 bits); anything left over must be less than a byte of padding
        var entryCount = l / TaggedFieldConstants.RoutingInfoLength;
        var paddingBits = l - entryCount * TaggedFieldConstants.RoutingInfoLength;
        if (entryCount == 0 || paddingBits >= 8)
            throw new ArgumentException(
                $"Invalid length for {nameof(RoutingInfoTaggedField)}. {l} bits is not a whole number of routing entries",
                nameof(length));

        var routingInfos = new RoutingInfoCollection();
        for (var i = 0; i < entryCount; i++)
        {
            var pubkeyBytes = new byte[34];
            bitReader.ReadBits(pubkeyBytes, 264);

            var shortChannelBytes = new byte[9];
            bitReader.ReadBits(shortChannelBytes, 64);

            var feeBaseMsat = unchecked((uint)bitReader.ReadInt32FromBits(32));
            var feeProportionalMillionths = unchecked((uint)bitReader.ReadInt32FromBits(32));
            var cltvExpiryDelta = bitReader.ReadUInt16FromBits(16);

            routingInfos.Add(new RoutingInfo(new CompactPubKey(pubkeyBytes[..^1]),
                                             new ShortChannelId(shortChannelBytes[..^1]),
                                             feeBaseMsat,
                                             feeProportionalMillionths,
                                             cltvExpiryDelta));
        }

        // Skip the padding bits
        if (paddingBits > 0)
            bitReader.SkipBits(paddingBits);

        return new RoutingInfoTaggedField(routingInfos);
    }

    private void OnRoutingInfoCollectionChanged(object? sender, EventArgs e)
    {
        Length = CalculateLength(Value.Count);
    }

    /// <summary>
    /// Minimal number of 5-bit groups that hold <paramref name="count"/> 408-bit entries
    /// </summary>
    private static short CalculateLength(int count)
    {
        return (short)((count * TaggedFieldConstants.RoutingInfoLength + 4) / 5);
    }
}