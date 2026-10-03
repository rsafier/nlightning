using System.Buffers.Binary;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;

/// <summary>
/// The BOLT 4 framing of the Serialization implementation, which this test project does not reference:
/// <c>failuremsg = code || data</c> and <c>u16 failure_len || failuremsg || u16 pad_len || zero pad</c>, so packets
/// created here are byte-identical to the spec vectors' (zero padding to 256).
/// </summary>
internal sealed class SpecFailureMessageSerializer : IFailureMessageSerializer
{
    public byte[] Serialize(FailureMessage message)
    {
        var bytes = new byte[FailureMessage.CodeLength + message.Data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)message.Code);
        message.Data.Span.CopyTo(bytes.AsSpan(FailureMessage.CodeLength));
        return bytes;
    }

    public FailureMessage Deserialize(ReadOnlyMemory<byte> failureMessage)
    {
        return new FailureMessage((FailureCode)BinaryPrimitives.ReadUInt16BigEndian(failureMessage.Span),
                                  failureMessage[FailureMessage.CodeLength..]);
    }

    public bool TryDeserialize(ReadOnlyMemory<byte> failureMessage, out FailureMessage? message)
    {
        try
        {
            message = failureMessage.Length < FailureMessage.CodeLength ? null : Deserialize(failureMessage);
            return message is not null;
        }
        catch (ArgumentException)
        {
            message = null;
            return false;
        }
    }

    public byte[] SerializeErrorPayload(FailureMessage message,
                                        int minFailurePadLength = OnionConstants.MinFailurePadLength)
    {
        var failureMessage = Serialize(message);
        var padLength = Math.Max(0, minFailurePadLength - failureMessage.Length);
        var payload = new byte[4 + failureMessage.Length + padLength];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)failureMessage.Length);
        failureMessage.CopyTo(payload, 2);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + failureMessage.Length), (ushort)padLength);
        return payload;
    }

    public bool TryReadErrorPayload(ReadOnlyMemory<byte> errorPayload, out ReadOnlyMemory<byte> failureMessage)
    {
        failureMessage = ReadOnlyMemory<byte>.Empty;
        if (errorPayload.Length < 2)
            return false;

        var length = BinaryPrimitives.ReadUInt16BigEndian(errorPayload.Span);
        if (errorPayload.Length - 2 < length)
            return false;

        failureMessage = errorPayload.Slice(2, length);
        return true;
    }
}