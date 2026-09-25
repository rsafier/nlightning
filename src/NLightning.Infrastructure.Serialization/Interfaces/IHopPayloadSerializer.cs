namespace NLightning.Infrastructure.Serialization.Interfaces;

using Domain.Exceptions;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Reads and writes BOLT 4 per-hop <c>payload</c> TLV streams.
/// </summary>
/// <remarks>
/// Parsing is strict (BOLT 1 + BOLT 4): canonical BigSize, strictly increasing types, lengths within bounds, unknown
/// even types rejected, known types decoded with their converters (exact length, minimal truncated ints). Unknown
/// odd records are kept verbatim, so a parsed payload serializes back to the same bytes. Every parse failure is an
/// <see cref="OnionException"/> with <c>invalid_onion_payload</c> and data <c>bigsize type || u16 offset</c>
/// (type 0 / offset 0 when the failure cannot be narrowed down to a record).
/// </remarks>
public interface IHopPayloadSerializer
{
    /// <summary>
    /// Parses a raw TLV stream, without the bigsize length prefix (what the Sphinx peeler returns).
    /// </summary>
    /// <param name="payload">Exactly the payload bytes.</param>
    /// <returns>The payload; record offsets are relative to the start of <paramref name="payload"/>.</returns>
    /// <exception cref="OnionException">Thrown with <c>invalid_onion_payload</c> when the payload is invalid.</exception>
    Task<HopPayload> DeserializeAsync(ReadOnlyMemory<byte> payload);

    /// <summary>
    /// Reads <c>bigsize length || payload</c> from <paramref name="stream"/> and leaves the stream positioned right
    /// after the payload (e.g. at the next hop's HMAC).
    /// </summary>
    /// <returns>The payload; record offsets are relative to the start of the length prefix.</returns>
    /// <exception cref="OnionException">
    /// Thrown with <c>invalid_onion_payload</c> when the length is malformed, below 2, exceeds the bytes remaining, or
    /// the payload is invalid.
    /// </exception>
    Task<HopPayload> DeserializeWithLengthPrefixAsync(Stream stream);

    /// <summary>
    /// Writes the payload TLV stream, without a length prefix.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the payload would encode to fewer than 2 bytes.</exception>
    Task SerializeAsync(HopPayload payload, Stream stream);

    /// <summary>
    /// Writes <c>bigsize length || payload</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the payload would encode to fewer than 2 bytes.</exception>
    Task SerializeWithLengthPrefixAsync(HopPayload payload, Stream stream);
}