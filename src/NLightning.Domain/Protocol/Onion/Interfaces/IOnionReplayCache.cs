namespace NLightning.Domain.Protocol.Onion.Interfaces;

/// <summary>
/// Remembers the HMACs of onion packets this node has already processed (BOLT 4 replay protection).
/// </summary>
/// <remarks>
/// <para>
/// BOLT 4 reader: "if the onion is for a payment: if <c>hmac</c> has previously been received: if the preimage is
/// known MAY immediately redeem the HTLC using the preimage, otherwise MUST abort processing the packet and fail."
/// </para>
/// <para>
/// Call <see cref="TryAdd"/> only after the packet HMAC has verified (i.e. after a successful peel): record only
/// authenticated HMACs. Recording unverified HMACs lets a peer fill a bounded cache with random values at no crypto
/// cost, evict genuine entries and then replay an onion it forwarded earlier.
/// </para>
/// </remarks>
public interface IOnionReplayCache
{
    /// <summary>
    /// Records <paramref name="hmac"/> as seen.
    /// </summary>
    /// <param name="hmac">The 32-byte packet HMAC of the incoming onion.</param>
    /// <returns><c>true</c> when the HMAC was not seen before; <c>false</c> when it is a replay.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="hmac"/> is not 32 bytes long.</exception>
    bool TryAdd(ReadOnlySpan<byte> hmac);
}