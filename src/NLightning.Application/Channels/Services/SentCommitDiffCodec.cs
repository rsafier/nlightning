using System.Buffers.Binary;

namespace NLightning.Application.Channels.Services;

using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;

/// <summary>
/// The byte format of <c>ChannelModel.SentCommitDiff</c> (BOLT2 plan decision D4): the wire messages we sent since the
/// previous <c>commitment_signed</c> (our <c>update_*</c>, in order) followed by the new <c>commitment_signed</c>, each
/// written as a 2-byte big-endian length and the serialized message (type included). Retransmitted verbatim on
/// <c>channel_reestablish</c> (N7).
/// </summary>
public static class SentCommitDiffCodec
{
    /// <summary>Serializes <paramref name="messages"/> in order.</summary>
    /// <exception cref="InvalidOperationException">A serialized message does not fit the 2-byte length.</exception>
    public static async Task<byte[]> EncodeAsync(IMessageSerializer serializer, IEnumerable<IMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(messages);

        using var diff = new MemoryStream();
        var lengthPrefix = new byte[sizeof(ushort)];
        foreach (var message in messages)
        {
            using var messageStream = new MemoryStream();
            await serializer.SerializeAsync(message, messageStream);
            if (messageStream.Length > ushort.MaxValue)
                throw new InvalidOperationException(
                    $"A {message.Type} message of {messageStream.Length} bytes does not fit in the sent diff");

            BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)messageStream.Length);
            diff.Write(lengthPrefix);
            messageStream.Position = 0;
            await messageStream.CopyToAsync(diff);
        }

        return diff.ToArray();
    }

    /// <summary>Splits an encoded diff back into its serialized messages, without parsing them.</summary>
    /// <exception cref="FormatException">The diff is truncated.</exception>
    public static IReadOnlyList<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> diff)
    {
        var messages = new List<ReadOnlyMemory<byte>>();
        var offset = 0;
        while (offset < diff.Length)
        {
            if (diff.Length - offset < sizeof(ushort))
                throw new FormatException("Truncated length prefix in the sent commitment diff");

            var length = BinaryPrimitives.ReadUInt16BigEndian(diff.Span[offset..]);
            offset += sizeof(ushort);
            if (diff.Length - offset < length)
                throw new FormatException("Truncated message in the sent commitment diff");

            messages.Add(diff.Slice(offset, length));
            offset += length;
        }

        return messages;
    }

    /// <summary>Decodes an encoded diff into its messages, in order.</summary>
    /// <exception cref="FormatException">The diff is truncated or a message cannot be parsed.</exception>
    public static async Task<IReadOnlyList<IMessage>> DecodeAsync(IMessageSerializer serializer,
                                                                  ReadOnlyMemory<byte> diff)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var messages = new List<IMessage>();
        foreach (var bytes in Split(diff))
        {
            // The deserializers find trailing TLVs by position/length, so each message gets its own bounded stream
            using var stream = new MemoryStream(bytes.ToArray(), false);
            var message = await serializer.DeserializeMessageAsync(stream)
                       ?? throw new FormatException("Unparsable message in the sent commitment diff");
            messages.Add(message);
        }

        return messages;
    }
}