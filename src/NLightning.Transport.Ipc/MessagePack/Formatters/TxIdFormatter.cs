using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;

/// <summary>
/// Writes a <see cref="TxId"/> as a MessagePack <c>bin 8</c> of 32 bytes, or <c>nil</c> for the default value.
/// </summary>
public class TxIdFormatter : IMessagePackFormatter<TxId>
{
    public void Serialize(ref MessagePackWriter writer, TxId value, MessagePackSerializerOptions options)
    {
        byte[]? bytes = value;
        if (bytes is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write(bytes);
    }

    public TxId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return default;

        var bytes = reader.ReadBytes() ??
                    throw new SerializationException($"Error deserializing {nameof(TxId)}");
        if (bytes.Length != CryptoConstants.Sha256HashLen)
            throw new SerializationException(
                $"Error deserializing {nameof(TxId)}: expected {CryptoConstants.Sha256HashLen} bytes, got {bytes.Length}");

        return bytes.ToArray();
    }
}