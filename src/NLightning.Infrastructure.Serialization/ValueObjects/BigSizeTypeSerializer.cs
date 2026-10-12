using System.Buffers;
using System.Runtime.Serialization;
using NLightning.Domain.Interfaces;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.ValueObjects;

using Converters;

public class BigSizeTypeSerializer : IValueObjectTypeSerializer<BigSize>
{
    /// <summary>
    /// Error message used when a BigSize is not minimally encoded (BOLT 1 Appendix A).
    /// </summary>
    public const string NonCanonicalErrorMessage = "decoded bigsize is not canonical";

    /// <summary>
    /// Serializes a BigSize value into a stream.
    /// </summary>
    /// <param name="valueObject">The BigSize value to serialize.</param>
    /// <param name="stream">The stream where the serialized value will be written.</param>
    /// <returns>A task that represents the asynchronous serialization operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the stream is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during the write operation.</exception>
    public async Task SerializeAsync(IValueObject valueObject, Stream stream)
    {
        if (valueObject is not BigSize bigSize)
            throw new ArgumentException("Value object must be of type BigSize.", nameof(valueObject));

        if (bigSize < 0xfd)
        {
            await stream.WriteAsync(new[] { (byte)bigSize });
        }
        else if (bigSize < 0x10000)
        {
            await stream.WriteAsync(new byte[] { 0xfd });
            await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian((ushort)bigSize));
        }
        else if (bigSize < 0x100000000)
        {
            await stream.WriteAsync(new byte[] { 0xfe });
            await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian((uint)bigSize));
        }
        else
        {
            await stream.WriteAsync(new byte[] { 0xff });
            await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian(bigSize.Value));
        }
    }

    /// <summary>
    /// Deserializes a BigSize value from a stream.
    /// </summary>
    /// <param name="stream">The stream from which the BigSize value will be deserialized.</param>
    /// <returns>A task that represents the asynchronous deserialization operation, containing the deserialized BigSize value.</returns>
    /// <remarks>
    /// <para>Decoding is canonical: a value that could have been encoded in fewer bytes is rejected (BOLT 1
    /// Appendix A).</para>
    /// <para>The stream does not need to be seekable: every read goes through <see cref="Stream.ReadExactlyAsync"/>,
    /// so truncation is detected by the read itself and not by comparing <c>Position</c> with <c>Length</c>.</para>
    /// </remarks>
    /// <exception cref="SerializationException">
    /// Thrown when the stream is empty, ends in the middle of a BigSize, or the value is not minimally encoded.
    /// </exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during the read operation.</exception>
    public async Task<BigSize> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(ulong));

        try
        {
            await ReadExactlyAsync(stream, buffer, sizeof(byte), "BigSize cannot be read from an empty stream.");
            ulong value;

            switch (buffer[0])
            {
                case < 0xfd:
                    value = buffer[0];
                    break;
                case 0xfd:
                    {
                        await ReadExactlyAsync(stream, buffer, sizeof(ushort),
                                               "BigSize cannot be read from a stream with insufficient data.");
                        value = EndianBitConverter.ToUInt16BigEndian(buffer[..sizeof(ushort)]);
                        if (value < 0xfd)
                            throw new SerializationException(NonCanonicalErrorMessage);

                        break;
                    }
                case 0xfe:
                    {
                        await ReadExactlyAsync(stream, buffer, sizeof(uint),
                                               "BigSize cannot be read from a stream with insufficient data.");
                        value = EndianBitConverter.ToUInt32BigEndian(buffer[..sizeof(uint)]);
                        if (value < 0x10000)
                            throw new SerializationException(NonCanonicalErrorMessage);

                        break;
                    }
                default:
                    {
                        await ReadExactlyAsync(stream, buffer, sizeof(ulong),
                                               "BigSize cannot be read from a stream with insufficient data.");
                        value = EndianBitConverter.ToUInt64BigEndian(buffer[..sizeof(ulong)]);
                        if (value < 0x100000000)
                            throw new SerializationException(NonCanonicalErrorMessage);

                        break;
                    }
            }

            return new BigSize(value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, string truncatedMessage)
        {
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory()[..count]);
            }
            catch (EndOfStreamException e)
            {
                throw new SerializationException(truncatedMessage, e);
            }
        }
    }

    async Task<IValueObject> IValueObjectTypeSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}