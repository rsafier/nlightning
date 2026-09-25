using System.Runtime.Serialization;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Tlv;

using Domain.Protocol.Tlv;

public class TlvSerializer : ITlvSerializer
{
    private readonly IValueObjectTypeSerializer<BigSize> _bigSizeSerializer;

    public TlvSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _bigSizeSerializer = valueObjectSerializerFactory.GetSerializer<BigSize>()
                          ?? throw new ArgumentNullException(nameof(valueObjectSerializerFactory));
    }

    /// <summary>
    /// Serializes a BaseTlv value into a stream.
    /// </summary>
    /// <param name="baseTlv">The BaseTlv value to serialize.</param>
    /// <param name="stream">The stream where the serialized value will be written.</param>
    /// <returns>A task that represents the asynchronous serialization operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the stream is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during the write operation.</exception>
    public async Task SerializeAsync(BaseTlv baseTlv, Stream stream)
    {
        await _bigSizeSerializer.SerializeAsync(baseTlv.Type, stream);
        await _bigSizeSerializer.SerializeAsync(baseTlv.Length, stream);

        await stream.WriteAsync(baseTlv.Value);
    } //2102C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75AC

    /// <summary>
    /// Deserializes a BaseTlv value from a stream.
    /// </summary>
    /// <param name="stream">The stream from which the BaseTlv value will be deserialized.</param>
    /// <returns>
    /// A task that represents the asynchronous deserialization operation, containing the deserialized BaseTlv value,
    /// or <c>null</c> when zero bytes remain before the type.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when the type or length is truncated.</exception>
    /// <exception cref="SerializationException">
    /// Thrown when the length exceeds the number of bytes remaining in the stream.
    /// </exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during the read operation.</exception>
    public async Task<BaseTlv?> DeserializeAsync(Stream stream)
    {
        if (stream.Position == stream.Length)
            return null;

        var type = await _bigSizeSerializer.DeserializeAsync(stream);
        var length = await _bigSizeSerializer.DeserializeAsync(stream);

        // BOLT 1: if length exceeds the number of bytes remaining in the message, MUST fail to parse.
        // Checked before allocating so a peer-supplied length cannot force a large allocation.
        var remaining = (ulong)(stream.Length - stream.Position);
        if (length.Value > remaining)
            throw new SerializationException(
                $"TLV length {length.Value} exceeds the {remaining} bytes remaining in the stream.");

        var value = new byte[(int)length.Value];
        await stream.ReadExactlyAsync(value);

        return new BaseTlv(type, length, value);
    }
}