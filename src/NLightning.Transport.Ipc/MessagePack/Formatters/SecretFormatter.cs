using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Writes a <see cref="Secret"/> (a payment preimage, <c>settleholdinvoice</c>'s argument) as a MessagePack
/// <c>bin 8</c> of 32 bytes, or <c>nil</c> for the default value. Reading rejects any other length.
/// </summary>
[ExcludeFormatterFromSourceGeneratedResolver]
public class SecretFormatter : IMessagePackFormatter<Secret>
{
    public void Serialize(ref MessagePackWriter writer, Secret value, MessagePackSerializerOptions options)
    {
        byte[]? bytes = value;
        if (bytes is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write(bytes);
    }

    public Secret Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return default;

        var bytes = reader.ReadBytes() ??
                    throw new SerializationException($"Error deserializing {nameof(Secret)}");
        if (bytes.Length != CryptoConstants.SecretLen)
            throw new SerializationException(
                $"Error deserializing {nameof(Secret)}: expected {CryptoConstants.SecretLen} bytes, got {bytes.Length}");

        return new Secret(bytes.ToArray());
    }
}