namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;

/// <summary>
/// What we learnt as a hop of a blinded route (BOLT 4 "Route Blinding", ONION M5): the recipient's instructions for us
/// and what they make of the incoming HTLC.
/// </summary>
/// <param name="IsIntroduction">We are the introduction node: the path_key came as the payload's
/// <c>current_path_key</c>, not in <c>update_add_htlc</c>. An introduction node that is not the final node fails with
/// <c>update_fail_htlc</c> + <c>invalid_onion_blinding</c>; a node that got the path_key in <c>update_add_htlc</c> with
/// <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> (BOLT 2 "Removing an HTLC").</param>
/// <param name="RecipientData">The decrypted <c>encrypted_recipient_data</c>, already checked by
/// <see cref="Domain.Protocol.Onion.Validators.BlindedRecipientDataValidator"/>.</param>
/// <param name="NextPathKey">The path_key to send to the next hop in <c>update_add_htlc</c>.</param>
/// <param name="AmountToForward">Non-final hop: <c>amt_to_forward</c> computed from <c>payment_relay</c>; null at the
/// final hop, or when the incoming amount was not given to the processor.</param>
/// <param name="OutgoingCltvValue">Non-final hop: <c>cltv_expiry - payment_relay.cltv_expiry_delta</c>; null at the
/// final hop, or when the incoming expiry was not given to the processor.</param>
public sealed record IncomingBlindedHop(bool IsIntroduction, BlindedRecipientData RecipientData,
                                        CompactPubKey NextPathKey, LightningMoney? AmountToForward = null,
                                        uint? OutgoingCltvValue = null)
{
    /// <summary>
    /// How many hops relaying to ourselves (dummy hops of a path we made, NL-440) were peeled before this one; 0 when
    /// the HTLC reached this hop directly. <see cref="RecipientData"/> is then the last layer's.
    /// </summary>
    public int DummyHops { get; init; }

    /// <summary>
    /// Final hop after dummy hops: the amount our final hop would have received once every dummy hop's
    /// <c>payment_relay</c> was applied to the HTLC's (null without dummy hops, or when the incoming amount was not
    /// given). The final-hop checks compare the onion's <c>amt_to_forward</c> with it, as they would the HTLC's.
    /// </summary>
    public LightningMoney? ReceivedAmount { get; init; }

    /// <summary>
    /// Final hop after dummy hops: the <c>cltv_expiry</c> our final hop would have received (see
    /// <see cref="ReceivedAmount"/>).
    /// </summary>
    public uint? ReceivedCltvExpiry { get; init; }
}