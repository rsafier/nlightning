using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Writes a <see cref="Hash"/> as a MessagePack <c>bin 8</c> of 32 bytes, or <c>nil</c> for the default value.
/// </summary>
public class HashFormatter : IMessagePackFormatter<Hash>
{
    public void Serialize(ref MessagePackWriter writer, Hash value, MessagePackSerializerOptions options)
    {
        byte[]? bytes = value;
        if (bytes is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write(bytes);
    }

    public Hash Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return default;

        var bytes = reader.ReadBytes() ??
                    throw new SerializationException($"Error deserializing {nameof(Hash)}");
        if (bytes.Length != CryptoConstants.Sha256HashLen)
            throw new SerializationException(
                $"Error deserializing {nameof(Hash)}: expected {CryptoConstants.Sha256HashLen} bytes, got {bytes.Length}");

        return bytes.ToArray();
    }
}