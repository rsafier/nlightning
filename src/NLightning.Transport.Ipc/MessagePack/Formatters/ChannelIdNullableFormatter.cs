using System.Buffers;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Channels.ValueObjects;

/// <summary>
/// Writes a <see cref="ChannelId"/>? as its bytes, or <c>nil</c> when absent.
/// </summary>
[ExcludeFormatterFromSourceGeneratedResolver]
public class ChannelIdNullableFormatter : IMessagePackFormatter<ChannelId?>
{
    public void Serialize(ref MessagePackWriter writer, ChannelId? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write((byte[])value.Value);
    }

    public ChannelId? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var bytes = reader.ReadBytes() ??
                    throw new SerializationException($"Error deserializing {nameof(ChannelId)}");
        return new ChannelId(bytes.ToArray());
    }
}