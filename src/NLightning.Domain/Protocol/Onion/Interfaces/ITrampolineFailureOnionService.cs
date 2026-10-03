namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Constants;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// BOLT 4 "Returning Errors" for trampoline payments (BOLTs PR 836): failure packets with a trampoline layer under the
/// outer layer.
/// </summary>
/// <remarks>
/// <para>
/// A trampoline node (an intermediate trampoline or the recipient) that fails an HTLC builds the packet with its
/// trampoline shared secret (HMAC with <c>um</c>, obfuscation with <c>ammag</c>) and then obfuscates it once more with
/// the <c>ammag</c> key of its outer shared secret, so it travels upstream like any failure and only the origin, which
/// knows both routes, can read it. An intermediate trampoline node first removes the layers of its own route to the
/// next trampoline node (<see cref="UnwrapDownstreamErrorPacket"/>); a failure from that route it may replace with its
/// own, a failure from the next trampoline node it re-wraps with both of its layers. The origin decrypts the outer
/// route first, then the trampoline route.
/// </para>
/// <para>
/// The packet format and the plain create/wrap/decrypt steps are <see cref="IFailureOnionService"/>'s; this service
/// only orders the layers. <c>attribution_data</c> stays on the outer layer (<see cref="IAttributionDataService"/>):
/// PR 836 defines no attribution for the trampoline layer and has no vector for it.
/// </para>
/// </remarks>
public interface ITrampolineFailureOnionService
{
    /// <summary>
    /// Builds the failure packet at a trampoline node: created with <paramref name="trampolineSharedSecret"/>, then
    /// obfuscated with the <c>ammag</c> key of <paramref name="outerSharedSecret"/>.
    /// </summary>
    /// <param name="trampolineSharedSecret">The shared secret of the trampoline layer this node peeled.</param>
    /// <param name="outerSharedSecret">The shared secret of the incoming (outer) onion.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="minFailurePadLength">As for <see cref="IFailureOnionService.CreateErrorPacket"/>.</param>
    /// <returns>The packet to put in <c>update_fail_htlc.reason</c>.</returns>
    /// <exception cref="ArgumentException">As for <see cref="IFailureOnionService.CreateErrorPacket"/>.</exception>
    byte[] CreateTrampolineErrorPacket(Secret trampolineSharedSecret, Secret outerSharedSecret, FailureMessage message,
                                       int minFailurePadLength = OnionConstants.MinFailurePadLength);

    /// <summary>
    /// Re-wraps, at an intermediate trampoline node, a failure the next trampoline node encrypted for the origin
    /// (<see cref="TrampolineDownstreamFailure.UnwrappedPacket"/>): obfuscated with the <c>ammag</c> key of
    /// <paramref name="trampolineSharedSecret"/>, then with that of <paramref name="outerSharedSecret"/>.
    /// </summary>
    /// <param name="trampolineSharedSecret">The shared secret of the trampoline layer this node peeled.</param>
    /// <param name="outerSharedSecret">The shared secret of the incoming (outer) onion.</param>
    /// <param name="errorPacket">The unwrapped packet. It is not modified.</param>
    /// <returns>A new array with the wrapped packet (truncated to 32768 bytes first, as any wrap).</returns>
    byte[] WrapTrampolineErrorPacket(Secret trampolineSharedSecret, Secret outerSharedSecret,
                                     ReadOnlySpan<byte> errorPacket);

    /// <summary>
    /// Removes, at an intermediate trampoline node, the layers of its own route to the next trampoline node from a
    /// failure received on that route, checking each hop's <c>um</c> key on the way.
    /// </summary>
    /// <param name="downstreamSharedSecrets">
    /// The shared secrets of the outer onion this node built for the failed HTLC, first hop first (the next
    /// trampoline node last).
    /// </param>
    /// <param name="errorPacket">The packet from the downstream <c>update_fail_htlc</c>.</param>
    /// <returns>The failure of an outer hop, or the packet to re-wrap.</returns>
    /// <exception cref="ArgumentException">
    /// If <paramref name="downstreamSharedSecrets"/> is empty or a secret is not 32 bytes.
    /// </exception>
    TrampolineDownstreamFailure UnwrapDownstreamErrorPacket(IReadOnlyList<Secret> downstreamSharedSecrets,
                                                            ReadOnlySpan<byte> errorPacket);

    /// <summary>
    /// Decrypts a trampoline payment's failure at the origin: the outer route's keys first, then, when none matched,
    /// the trampoline route's.
    /// </summary>
    /// <remarks>
    /// Runs max(27, outer + trampoline hops) iterations with constant dummy keys after the routes and constant-time
    /// HMAC comparisons, like <see cref="IFailureOnionService.DecryptErrorPacket"/>.
    /// </remarks>
    /// <param name="outerSharedSecrets">
    /// The outer route's shared secrets, first hop first (the first trampoline node last).
    /// </param>
    /// <param name="trampolineSharedSecrets">The trampoline onion's shared secrets, first trampoline hop first.</param>
    /// <param name="errorPacket">The packet from <c>update_fail_htlc.reason</c>.</param>
    /// <returns>The erring hop with its layer and message, or <c>null</c> if no hop's HMAC matched.</returns>
    /// <exception cref="ArgumentException">
    /// If either list is empty or a secret is not 32 bytes.
    /// </exception>
    TrampolineDecryptedFailure? DecryptTrampolineErrorPacket(IReadOnlyList<Secret> outerSharedSecrets,
                                                             IReadOnlyList<Secret> trampolineSharedSecrets,
                                                             ReadOnlySpan<byte> errorPacket);
}