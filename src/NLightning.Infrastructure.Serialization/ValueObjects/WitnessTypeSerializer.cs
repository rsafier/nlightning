using System.Buffers;
using System.Runtime.Serialization;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.ValueObjects;

using Converters;

public class WitnessTypeSerializer : IValueObjectTypeSerializer<Witness>
{
    /// <summary>
    /// Serializes a Witness value into a stream.
    /// </summary>
    /// <param name="valueObject">The Witness value to serialize.</param>
    /// <param name="stream">The stream where the serialized value will be written.</param>
    /// <returns>A task that represents the asynchronous serialization operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the stream is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during the write operation.</exception>
    public async Task SerializeAsync(IValueObject valueObject, Stream stream)
    {
        if (valueObject is not Witness witness)
            throw new ArgumentException("Value object must be of type Witness.", nameof(valueObject));

        await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian(witness.Length));
        await stream.WriteAsync(witness);
    }

    /// <summary>
    /// Deserializes a Witness value from a stream.
    /// </summary>
    /// <param name="stream">The stream from which the Witness value will be deserialized.</param>
    /// <returns>A task that represents the asynchronous deserialization operation, containing the deserialized Witness value.</returns>
    /// <exception cref="SerializationException">
    /// Thrown when the stream is empty, or the witness length exceeds the bytes remaining in a seekable stream.
    /// </exception>
    public async Task<Witness> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(ushort));

        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(ushort)]);
            var len = EndianBitConverter.ToUInt16BigEndian(buffer[..sizeof(ushort)]);

            // witness_data is a full serialized witness stack, so it is bounded only by its u16 length and by the
            // bytes left in the message. Reject an oversized length before allocating.
            if (stream.CanSeek && len > stream.Length - stream.Position)
                throw new SerializationException(
                    $"Witness length {len} exceeds the {stream.Length - stream.Position} bytes remaining in the stream.");

            var witnessBytes = new byte[len];
            await stream.ReadExactlyAsync(witnessBytes);

            return new Witness(witnessBytes);
        }
        catch (Exception e)
        {
            throw new SerializationException("Error deserializing Witness", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task<IValueObject> IValueObjectTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}