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
                                        uint? OutgoingCltvValue = null);