namespace NLightning.Infrastructure.Serialization.Interfaces;

using Domain.Protocol.Models;
using Domain.Protocol.ValueObjects;

public interface ITlvStreamSerializer
{
    Task SerializeAsync(TlvStream? baseTlv, Stream stream);
    Task<TlvStream?> DeserializeAsync(Stream stream);

    /// <summary>
    /// Deserializes a TLV stream following every BOLT 1 <c>tlv_stream</c> reader rule that does not depend on the
    /// value encoding: types must be strictly increasing, lengths must not exceed the bytes remaining, and any even
    /// type outside <paramref name="knownTypes"/> fails the stream. Reads until the end of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">A seekable stream that holds exactly the TLV stream (use a bounded sub-stream).</param>
    /// <param name="knownTypes">The TLV types this namespace understands.</param>
    /// <returns>
    /// All records as raw <see cref="Domain.Protocol.Tlv.BaseTlv"/> (unknown odd records are kept so callers can
    /// ignore or forward them). Never <c>null</c>; an empty input yields an empty stream.
    /// </returns>
    /// <remarks>
    /// Exact-length and minimal-encoding checks of known values are the job of the per-type converters.
    /// </remarks>
    /// <exception cref="System.Runtime.Serialization.SerializationException">Thrown when the stream is invalid.</exception>
    Task<TlvStream> DeserializeStrictAsync(Stream stream, IReadOnlySet<BigSize> knownTypes);
}