using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace NLightning.Transport.Ipc.MessagePack.Formatters;

using Domain.Node;
using Domain.Utils;

public class FeatureSetFormatter : IMessagePackFormatter<FeatureSet?>
{
    public void Serialize(ref MessagePackWriter writer, FeatureSet? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        // SizeInBits is the index of the highest set bit, so the set is one bit longer (it dropped that bit, NL-567)
        var length = value.SizeInBits + 1;
        using var bitWriter = new BitWriter(length);
        value.WriteToBitWriter(bitWriter, length, false);
        writer.Write(length);
        writer.Write(bitWriter.ToArray());
    }

    public FeatureSet? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;

        var sizeInBits = reader.ReadInt32();
        var bytes = reader.ReadBytes() ??
                    throw new SerializationException($"Error deserializing {nameof(FeatureSet)})");
        var bitReader = new BitReader(bytes.FirstSpan.ToArray());
        return FeatureSet.DeserializeFromBitReader(bitReader, sizeInBits, false);
    }
}