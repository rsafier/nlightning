using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Writes a <see cref="Secret"/>? (a payment preimage) as a MessagePack <c>bin 8</c> of 32 bytes, or <c>nil</c> when
/// absent. Reading rejects any other length.
/// </summary>
[ExcludeFormatterFromSourceGeneratedResolver]
public class SecretNullableFormatter : IMessagePackFormatter<Secret?>
{
    public void Serialize(ref MessagePackWriter writer, Secret? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write((byte[])value.Value);
    }

    public Secret? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var bytes = reader.ReadBytes() ?? throw new SerializationException($"Error deserializing {nameof(Secret)}");
        if (bytes.Length != CryptoConstants.SecretLen)
            throw new SerializationException(
                $"Error deserializing {nameof(Secret)}: expected {CryptoConstants.SecretLen} bytes, got {bytes.Length}");

        return new Secret(bytes.ToArray());
    }
}