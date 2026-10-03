using System.Security.Cryptography;

namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;

/// <summary>
/// We are a trampoline node of the HTLC (BOLT 4 "Trampoline Payments", BOLTs PR 836, NL-875): the outer onion ended at
/// us and its payload carried a <c>trampoline_onion_packet</c> (TLV 20) that we peeled too.
/// </summary>
/// <remarks>
/// <para>Every failure we send back for such an HTLC is created with both secrets: the trampoline layer first, then
/// the outer layer (<c>ITrampolineFailureOnionService.CreateTrampolineErrorPacket</c>), so only the origin reads it;
/// <c>attribution_data</c> stays on the outer layer. Inside a blinded trampoline route, a hop that got its path key
/// from the outer payload (<see cref="IsBlindedPastIntroduction"/>) answers every failure with
/// <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> and the sha256 of the trampoline packet (the PR
/// 836 blinded error vector), and the introduction node of a blinded trampoline route that is not its final node with
/// its own <c>invalid_onion_blinding</c>.</para>
/// </remarks>
/// <param name="OuterSharedSecret">The shared secret of the outer (payment) onion.</param>
/// <param name="OuterPayload">The validated outer final payload (the one carrying TLV 20).</param>
/// <param name="TrampolineSharedSecret">The shared secret of the trampoline layer we peeled (the first one when we
/// peeled hops relaying to ourselves).</param>
/// <param name="InnerPayload">The validated trampoline payload of the layer the result is about (the last one peeled).
/// </param>
/// <param name="Blinded">Set when the trampoline payload is inside a blinded route: the decrypted
/// <c>encrypted_recipient_data</c> and, for a relay, the next path key and the amount and expiry computed from
/// <c>payment_relay</c>.</param>
public abstract record IncomingOnionTrampolineResult(Secret OuterSharedSecret, HopPayload OuterPayload,
                                                     Secret TrampolineSharedSecret, HopPayload InnerPayload,
                                                     IncomingBlindedHop? Blinded)
    : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => OuterSharedSecret;

    /// <summary>
    /// We are a blinded trampoline hop after the introduction node (the path key came in the outer payload's
    /// <c>current_path_key</c>): every failure is <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>
    /// with <see cref="TrampolineOnionSha256"/>.
    /// </summary>
    public bool IsBlindedPastIntroduction => Blinded is { IsIntroduction: false };

    /// <summary>
    /// The sha256 of the received <c>trampoline_onion_packet</c> (TLV 20 of <see cref="OuterPayload"/>).
    /// </summary>
    public byte[] TrampolineOnionSha256 =>
        SHA256.HashData(OuterPayload.TrampolineOnionPacket?.ToBytes()
                     ?? throw new InvalidOperationException("The outer payload has no trampoline_onion_packet."));
}