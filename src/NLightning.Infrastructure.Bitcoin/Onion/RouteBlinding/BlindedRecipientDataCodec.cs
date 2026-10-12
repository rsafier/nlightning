using System.Buffers.Binary;

namespace NLightning.Infrastructure.Bitcoin.Onion.RouteBlinding;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Reads and writes the BOLT 4 route-blinding <c>encrypted_data_tlv</c> stream.
/// </summary>
/// <remarks>
/// Strict reader (BOLT 1 TLV rules): canonical BigSize types and lengths, strictly increasing types, every length
/// within the stream, fixed-length records of their exact size, minimal truncated integers (<c>tu32</c>/<c>tu64</c>),
/// valid compressed points, and no unknown even type. Every failure is <c>invalid_onion_blinding</c> (BOLT 4: a
/// blinded hop that cannot read its data MUST return an error, and inside a blinded route every error is that code).
/// </remarks>
internal static class BlindedRecipientDataCodec
{
    private const ulong PaddingType = 1;
    private const ulong ShortChannelIdType = 2;
    private const ulong NextNodeIdType = 4;
    private const ulong PathIdType = 6;
    private const ulong NextPathKeyOverrideType = 8;
    private const ulong PaymentRelayType = 10;
    private const ulong PaymentConstraintsType = 12;
    private const ulong AllowedFeaturesType = 14;

    public static BlindedRecipientData Decode(ReadOnlySpan<byte> stream)
    {
        ReadOnlyMemory<byte>? padding = null;
        ShortChannelId? shortChannelId = null;
        CompactPubKey? nextNodeId = null;
        ReadOnlyMemory<byte>? pathId = null;
        CompactPubKey? nextPathKeyOverride = null;
        BlindedPaymentRelay? paymentRelay = null;
        BlindedPaymentConstraints? paymentConstraints = null;
        ReadOnlyMemory<byte>? allowedFeatures = null;
        var unknown = new List<KeyValuePair<ulong, ReadOnlyMemory<byte>>>();

        var position = 0;
        ulong? previousType = null;
        while (position < stream.Length)
        {
            if (!SphinxBigSize.TryRead(stream[position..], out var type, out var typeSize))
                throw Fail("Malformed encrypted_data_tlv type.");
            position += typeSize;

            if (previousType is { } previous && type <= previous)
                throw Fail($"encrypted_data_tlv type {type} is not in strictly increasing order.");
            previousType = type;

            if (!SphinxBigSize.TryRead(stream[position..], out var length, out var lengthSize))
                throw Fail($"Malformed length of encrypted_data_tlv type {type}.");
            position += lengthSize;

            if (length > (ulong)(stream.Length - position))
                throw Fail($"encrypted_data_tlv type {type} is longer than the stream.");

            var value = stream.Slice(position, (int)length);
            position += (int)length;

            switch (type)
            {
                case PaddingType:
                    padding = value.ToArray();
                    break;
                case ShortChannelIdType:
                    RequireLength(type, value, ShortChannelId.Length);
                    shortChannelId = new ShortChannelId(value.ToArray());
                    break;
                case NextNodeIdType:
                    nextNodeId = ReadPoint(type, value);
                    break;
                case PathIdType:
                    pathId = value.ToArray();
                    break;
                case NextPathKeyOverrideType:
                    nextPathKeyOverride = ReadPoint(type, value);
                    break;
                case PaymentRelayType:
                    if (value.Length < 6)
                        throw Fail("payment_relay is too short.");
                    paymentRelay = new BlindedPaymentRelay(BinaryPrimitives.ReadUInt16BigEndian(value),
                                                           BinaryPrimitives.ReadUInt32BigEndian(value[2..]),
                                                           (uint)ReadTruncated(type, value[6..], 4));
                    break;
                case PaymentConstraintsType:
                    if (value.Length < 4)
                        throw Fail("payment_constraints is too short.");
                    paymentConstraints = new BlindedPaymentConstraints(BinaryPrimitives.ReadUInt32BigEndian(value),
                                                                       ReadTruncated(type, value[4..], 8));
                    break;
                case AllowedFeaturesType:
                    allowedFeatures = value.ToArray();
                    break;
                default:
                    if (type % 2 == 0)
                        throw Fail($"Unknown even encrypted_data_tlv type {type}.");
                    unknown.Add(new KeyValuePair<ulong, ReadOnlyMemory<byte>>(type, value.ToArray()));
                    break;
            }
        }

        return new BlindedRecipientData
        {
            Padding = padding,
            ShortChannelId = shortChannelId,
            NextNodeId = nextNodeId,
            PathId = pathId,
            NextPathKeyOverride = nextPathKeyOverride,
            PaymentRelay = paymentRelay,
            PaymentConstraints = paymentConstraints,
            AllowedFeatures = allowedFeatures,
            UnknownOddRecords = unknown
        };
    }

    public static byte[] Encode(BlindedRecipientData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var records = new List<(ulong Type, byte[] Value)>();
        if (data.Padding is { } padding)
            records.Add((PaddingType, padding.ToArray()));
        if (data.ShortChannelId is { } shortChannelId)
            records.Add((ShortChannelIdType, ((byte[])shortChannelId).ToArray()));
        if (data.NextNodeId is { } nextNodeId)
            records.Add((NextNodeIdType, ((byte[])nextNodeId).ToArray()));
        if (data.PathId is { } pathId)
            records.Add((PathIdType, pathId.ToArray()));
        if (data.NextPathKeyOverride is { } nextPathKeyOverride)
            records.Add((NextPathKeyOverrideType, ((byte[])nextPathKeyOverride).ToArray()));
        if (data.PaymentRelay is { } relay)
        {
            var value = new byte[6 + TruncatedLength(relay.FeeBaseMsat)];
            BinaryPrimitives.WriteUInt16BigEndian(value, relay.CltvExpiryDelta);
            BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(2), relay.FeeProportionalMillionths);
            WriteTruncated(relay.FeeBaseMsat, value.AsSpan(6));
            records.Add((PaymentRelayType, value));
        }

        if (data.PaymentConstraints is { } constraints)
        {
            var value = new byte[4 + TruncatedLength(constraints.HtlcMinimumMsat)];
            BinaryPrimitives.WriteUInt32BigEndian(value, constraints.MaxCltvExpiry);
            WriteTruncated(constraints.HtlcMinimumMsat, value.AsSpan(4));
            records.Add((PaymentConstraintsType, value));
        }

        if (data.AllowedFeatures is { } allowedFeatures)
            records.Add((AllowedFeaturesType, allowedFeatures.ToArray()));

        foreach (var (type, value) in data.UnknownOddRecords)
        {
            if (type % 2 == 0 || type == PaddingType)
                throw new ArgumentException($"encrypted_data_tlv type {type} is not an unknown odd type.",
                                            nameof(data));
            records.Add((type, value.ToArray()));
        }

        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        for (var i = 1; i < records.Count; i++)
        {
            if (records[i].Type == records[i - 1].Type)
                throw new ArgumentException($"encrypted_data_tlv type {records[i].Type} appears twice.",
                                            nameof(data));
        }

        var length = records.Sum(r => SphinxBigSize.GetEncodedLength(r.Type)
                                    + SphinxBigSize.GetEncodedLength((ulong)r.Value.Length) + r.Value.Length);
        var output = new byte[length];
        var position = 0;
        foreach (var (type, value) in records)
        {
            position += SphinxBigSize.Write(type, output.AsSpan(position));
            position += SphinxBigSize.Write((ulong)value.Length, output.AsSpan(position));
            value.CopyTo(output, position);
            position += value.Length;
        }

        return output;
    }

    private static void RequireLength(ulong type, ReadOnlySpan<byte> value, int expected)
    {
        if (value.Length != expected)
            throw Fail($"encrypted_data_tlv type {type} has length {value.Length}, expected {expected}.");
    }

    private static CompactPubKey ReadPoint(ulong type, ReadOnlySpan<byte> value)
    {
        RequireLength(type, value, CryptoConstants.CompactPubkeyLen);
        if (!SphinxKeyGenerator.IsValidPublicKey(value))
            throw Fail($"encrypted_data_tlv type {type} is not a valid point.");

        return new CompactPubKey(value.ToArray());
    }

    private static ulong ReadTruncated(ulong type, ReadOnlySpan<byte> value, int maxLength)
    {
        if (value.Length > maxLength)
            throw Fail($"Truncated integer of encrypted_data_tlv type {type} is too long.");

        // BOLT 1: a truncated integer MUST be minimally encoded (no leading zero byte)
        if (!value.IsEmpty && value[0] == 0)
            throw Fail($"Truncated integer of encrypted_data_tlv type {type} is not minimal.");

        ulong result = 0;
        foreach (var b in value)
            result = (result << 8) | b;

        return result;
    }

    private static int TruncatedLength(ulong value)
    {
        var length = 0;
        while (value != 0)
        {
            length++;
            value >>= 8;
        }

        return length;
    }

    private static void WriteTruncated(ulong value, Span<byte> destination)
    {
        for (var i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }
    }

    private static OnionException Fail(string message) => new(FailureCode.InvalidOnionBlinding, message);
}