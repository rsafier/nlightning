namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// BOLT 4 "Returning Errors": the legacy (non-attribution) failure onion.
/// </summary>
/// <remarks>
/// A return packet is <c>hmac || u16 failure_len || failuremsg || u16 pad_len || pad</c>, where
/// <c>hmac = HMAC-SHA256(um, everything after it)</c>, and each hop (the erring node included) XORs the whole packet
/// with the ChaCha20 stream of its <c>ammag</c> key. Keys come from the per-hop shared secret of the forward onion.
/// </remarks>
public interface IFailureOnionService
{
    /// <summary>
    /// Builds the return packet at the erring node: frames and pads <paramref name="message"/>, authenticates it with
    /// the <c>um</c> key and obfuscates it with the <c>ammag</c> key of <paramref name="sharedSecret"/>.
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion (from the peel).</param>
    /// <param name="message">The failure message.</param>
    /// <param name="minFailurePadLength">
    /// The minimum of <c>failure_len + pad_len</c> (BOLT 4: at least 256; SHOULD be 256).
    /// </param>
    /// <returns>The packet to put in <c>update_fail_htlc.reason</c>.</returns>
    /// <exception cref="ArgumentException">
    /// If the secret is not 32 bytes, <paramref name="minFailurePadLength"/> is below 256, or the packet would exceed
    /// <see cref="OnionConstants.MaxErrorPacketLength"/>.
    /// </exception>
    byte[] CreateErrorPacket(Secret sharedSecret, FailureMessage message,
                             int minFailurePadLength = OnionConstants.MinFailurePadLength);

    /// <summary>
    /// Obfuscates a return packet received from downstream with this hop's <c>ammag</c> key, before return-forwarding
    /// it upstream. A packet longer than <see cref="OnionConstants.MaxErrorPacketLength"/> is first truncated to that
    /// length (BOLT 4).
    /// </summary>
    /// <param name="sharedSecret">The shared secret of the incoming onion this HTLC was forwarded for.</param>
    /// <param name="errorPacket">The packet from the downstream <c>update_fail_htlc</c>. It is not modified.</param>
    /// <returns>A new array with the wrapped packet.</returns>
    byte[] WrapErrorPacket(Secret sharedSecret, ReadOnlySpan<byte> errorPacket);

    /// <summary>
    /// Decrypts a return packet at the origin node and identifies the erring hop.
    /// </summary>
    /// <remarks>
    /// Runs a constant <see cref="OnionConstants.ErrorDecryptionIterations"/> iterations (or one per hop for longer
    /// routes), with dummy keys past the end of the route and constant-time HMAC comparisons, so the timing does not
    /// reveal the route length or the erring hop's position.
    /// </remarks>
    /// <param name="hopSharedSecrets">The shared secret of each hop of the route, first hop first.</param>
    /// <param name="errorPacket">The packet from <c>update_fail_htlc.reason</c>.</param>
    /// <returns>The erring hop and its message, or <c>null</c> if no hop's HMAC matched.</returns>
    /// <exception cref="ArgumentException">If <paramref name="hopSharedSecrets"/> is empty or a secret is invalid.</exception>
    DecryptedFailure? DecryptErrorPacket(IReadOnlyList<Secret> hopSharedSecrets, ReadOnlySpan<byte> errorPacket);
}