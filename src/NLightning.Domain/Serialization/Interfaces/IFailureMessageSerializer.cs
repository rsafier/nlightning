using System.Runtime.Serialization;

namespace NLightning.Domain.Serialization.Interfaces;

using Protocol.Onion.Constants;
using Protocol.Onion.Models;

/// <summary>
/// Reads and writes BOLT 4 <c>failuremsg</c> (<c>u16 failure_code || data || [tlv_stream]</c>) and the plaintext
/// framing of a return packet (<c>u16 failure_len || failuremsg || u16 pad_len || pad</c>, i.e. the packet without
/// its HMAC).
/// </summary>
/// <remarks>
/// Synchronous on purpose: the failure onion service (Infrastructure.Bitcoin) calls it inside its crypto loop.
/// </remarks>
public interface IFailureMessageSerializer
{
    /// <summary>
    /// Writes <c>failure_code || data || tlv_stream</c>.
    /// </summary>
    byte[] Serialize(FailureMessage message);

    /// <summary>
    /// Parses a <c>failuremsg</c>. Bytes after the code-specific data are read as a TLV stream; if they are not a
    /// valid TLV stream (BOLT 1 rules, unknown even types included) they are ignored, as BOLT 4 requires of the
    /// origin ("MUST ignore any extra bytes in failuremsg").
    /// </summary>
    /// <exception cref="SerializationException">
    /// If the message is shorter than the code, or the data of a BOLT 4 code is truncated or malformed.
    /// </exception>
    FailureMessage Deserialize(ReadOnlyMemory<byte> failureMessage);

    /// <summary>
    /// Like <see cref="Deserialize"/>, without throwing.
    /// </summary>
    bool TryDeserialize(ReadOnlyMemory<byte> failureMessage, out FailureMessage? message);

    /// <summary>
    /// Writes the return-packet body <c>u16 failure_len || failuremsg || u16 pad_len || pad</c> (zero padding) such
    /// that <c>failure_len + pad_len</c> is at least <paramref name="minFailurePadLength"/>.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="minFailurePadLength">
    /// The minimum of <c>failure_len + pad_len</c>. BOLT 4: MUST be at least 256, SHOULD be exactly 256.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="minFailurePadLength"/> is below 256.</exception>
    /// <exception cref="ArgumentException">
    /// If the return packet (HMAC included) would exceed <see cref="OnionConstants.MaxErrorPacketLength"/>.
    /// </exception>
    byte[] SerializeErrorPayload(FailureMessage message,
                                 int minFailurePadLength = OnionConstants.MinFailurePadLength);

    /// <summary>
    /// Extracts the <c>failuremsg</c> from a decrypted return-packet body (the packet without its HMAC). The padding
    /// is not checked.
    /// </summary>
    /// <returns><c>false</c> if the body is shorter than <c>failure_len</c> says.</returns>
    bool TryReadErrorPayload(ReadOnlyMemory<byte> errorPayload, out ReadOnlyMemory<byte> failureMessage);
}