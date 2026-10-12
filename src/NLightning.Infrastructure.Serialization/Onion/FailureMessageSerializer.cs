using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Onion;

using Domain.Crypto.Constants;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;

/// <inheritdoc />
/// <remarks>
/// No failure TLV types are defined, so extension records are kept as raw <see cref="BaseTlv"/> and written verbatim
/// (<c>bigsize type || bigsize length || value</c>, with the length taken from <see cref="BaseTlv.Value"/>).
/// </remarks>
public class FailureMessageSerializer : IFailureMessageSerializer
{
    private const int LengthFieldLength = sizeof(ushort);

    /// <summary>
    /// The largest <c>failuremsg</c> that fits in a return packet: 32768 - hmac - failure_len - pad_len.
    /// </summary>
    public const int MaxFailureMessageLength =
        OnionConstants.MaxErrorPacketLength - CryptoConstants.Sha256HashLen - 2 * LengthFieldLength;

    /// <inheritdoc />
    public byte[] Serialize(FailureMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var tlvs = message.Extension?.GetTlvs().ToList() ?? [];
        var length = FailureMessage.CodeLength + message.Data.Length;
        foreach (var tlv in tlvs)
            length += BigSizeCodec.GetLength(tlv.Type.Value) + BigSizeCodec.GetLength((ulong)tlv.Value.Length)
                    + tlv.Value.Length;

        var buffer = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)message.Code);
        message.Data.Span.CopyTo(buffer.AsSpan(FailureMessage.CodeLength));

        var offset = FailureMessage.CodeLength + message.Data.Length;
        foreach (var tlv in tlvs)
        {
            offset += BigSizeCodec.Write(tlv.Type.Value, buffer.AsSpan(offset));
            offset += BigSizeCodec.Write((ulong)tlv.Value.Length, buffer.AsSpan(offset));
            tlv.Value.CopyTo(buffer, offset);
            offset += tlv.Value.Length;
        }

        return buffer;
    }

    /// <inheritdoc />
    public FailureMessage Deserialize(ReadOnlyMemory<byte> failureMessage)
    {
        if (failureMessage.Length < FailureMessage.CodeLength)
            throw new SerializationException(
                $"A failure message needs at least {FailureMessage.CodeLength} bytes, got {failureMessage.Length}.");

        var code = (FailureCode)BinaryPrimitives.ReadUInt16BigEndian(failureMessage.Span);
        var dataAndTail = failureMessage[FailureMessage.CodeLength..];
        if (!FailureMessage.TryGetDataLength(code, dataAndTail.Span, out var dataLength))
            throw new SerializationException(
                $"Truncated or malformed data for failure code 0x{(ushort)code:x4} ({code}).");

        var extension = TryReadTlvStream(dataAndTail.Span[dataLength..]);
        return new FailureMessage(code, dataAndTail[..dataLength], extension);
    }

    /// <inheritdoc />
    public bool TryDeserialize(ReadOnlyMemory<byte> failureMessage, out FailureMessage? message)
    {
        try
        {
            message = Deserialize(failureMessage);
            return true;
        }
        catch (SerializationException)
        {
            message = null;
            return false;
        }
    }

    /// <inheritdoc />
    public byte[] SerializeErrorPayload(FailureMessage message,
                                        int minFailurePadLength = OnionConstants.MinFailurePadLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minFailurePadLength, OnionConstants.MinFailurePadLength);

        var failureMessage = Serialize(message);
        if (failureMessage.Length > MaxFailureMessageLength)
            throw new ArgumentException(
                $"The failure message is {failureMessage.Length} bytes; at most {MaxFailureMessageLength} fit in a "
              + "return packet.", nameof(message));

        var padLength = Math.Max(0, minFailurePadLength - failureMessage.Length);
        var payloadLength = 2 * LengthFieldLength + failureMessage.Length + padLength;
        if (CryptoConstants.Sha256HashLen + payloadLength > OnionConstants.MaxErrorPacketLength)
            throw new ArgumentException(
                $"The return packet would exceed {OnionConstants.MaxErrorPacketLength} bytes.",
                nameof(minFailurePadLength));

        var payload = new byte[payloadLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)failureMessage.Length);
        failureMessage.CopyTo(payload, LengthFieldLength);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(LengthFieldLength + failureMessage.Length),
                                              (ushort)padLength);

        // The pad (already zero) follows
        return payload;
    }

    /// <inheritdoc />
    public bool TryReadErrorPayload(ReadOnlyMemory<byte> errorPayload, out ReadOnlyMemory<byte> failureMessage)
    {
        failureMessage = ReadOnlyMemory<byte>.Empty;
        if (errorPayload.Length < LengthFieldLength)
            return false;

        var failureLength = BinaryPrimitives.ReadUInt16BigEndian(errorPayload.Span);
        if (errorPayload.Length - LengthFieldLength < failureLength)
            return false;

        failureMessage = errorPayload.Slice(LengthFieldLength, failureLength);
        return true;
    }

    /// <summary>
    /// Reads the bytes after the data as a BOLT 1 TLV stream (canonical bigsize, strictly increasing types, lengths
    /// within bounds, no unknown even types; failure TLVs are all unknown). Anything else is extra bytes the origin
    /// must ignore, so it yields <c>null</c> rather than an error.
    /// </summary>
    private static TlvStream? TryReadTlvStream(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return null;

        var tlvs = new List<BaseTlv>();
        ulong? previousType = null;
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!BigSizeCodec.TryRead(bytes[offset..], out var type, out var typeLength))
                return null;

            offset += typeLength;
            if (!BigSizeCodec.TryRead(bytes[offset..], out var length, out var lengthLength))
                return null;

            offset += lengthLength;
            if (length > (ulong)(bytes.Length - offset)
             || (previousType.HasValue && type <= previousType.Value)
             || type % 2 == 0)
                return null;

            tlvs.Add(new BaseTlv(type, bytes.Slice(offset, (int)length).ToArray()));
            offset += (int)length;
            previousType = type;
        }

        var stream = new TlvStream();
        foreach (var tlv in tlvs)
            stream.Add(tlv);

        return stream;
    }
}