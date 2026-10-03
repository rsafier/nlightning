namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// We are an intermediate trampoline node of the HTLC: the trampoline payload names the next trampoline node
/// (<c>outgoing_node_id</c>, or inside a blinded route the recipient data's <c>next_node_id</c>) or the blinded paths of
/// a recipient that does not support trampoline (<c>recipient_blinded_paths</c>). The relay engine collects every part
/// of the payment, then pays the next node with <see cref="NextTrampolinePacket"/> (BOLTs PR 836 TR-R-09..TR-R-11).
/// </summary>
/// <remarks>
/// The amounts and expiries here are per HTLC: <see cref="AmountToForward"/> and <see cref="OutgoingCltvValue"/> come
/// from the trampoline payload, or inside a blinded route from <c>payment_relay</c> applied to the outer total and the
/// outer <c>outgoing_cltv_value</c> of this part. The relay engine decides on the whole set (fee and CLTV policy).
/// </remarks>
/// <inheritdoc cref="IncomingOnionTrampolineResult"/>
/// <param name="NextTrampolinePacket">The peeled trampoline onion for the next trampoline node (same
/// <c>hop_payloads</c> length), to put in TLV 20 of the next node's outer final payload.</param>
public sealed record IncomingOnionTrampolineRelay(Secret OuterSharedSecret, HopPayload OuterPayload,
                                                  Secret TrampolineSharedSecret, HopPayload InnerPayload,
                                                  OnionPacket NextTrampolinePacket, IncomingBlindedHop? Blinded = null)
    : IncomingOnionTrampolineResult(OuterSharedSecret, OuterPayload, TrampolineSharedSecret, InnerPayload, Blinded)
{
    /// <summary>
    /// The next trampoline node: the trampoline payload's <c>outgoing_node_id</c>, or inside a blinded route the
    /// recipient data's <c>next_node_id</c>; null when the payload names <see cref="RecipientBlindedPaths"/> instead.
    /// </summary>
    public CompactPubKey? NextNodeId => Blinded is { } blinded ? blinded.RecipientData.NextNodeId
                                                               : InnerPayload.OutgoingNodeId;

    /// <summary>
    /// Inside a blinded route, the path key to give the next trampoline node (in its outer payload's
    /// <c>current_path_key</c>).
    /// </summary>
    public CompactPubKey? NextPathKey => Blinded?.NextPathKey;

    /// <summary>
    /// The blinded paths to pay a recipient that does not support trampoline (<c>recipient_blinded_paths</c>, 22).
    /// </summary>
    public IReadOnlyList<WireBlindedPaymentPath>? RecipientBlindedPaths => InnerPayload.RecipientBlindedPaths;

    /// <summary>
    /// The recipient's invoice features that may come with <see cref="RecipientBlindedPaths"/>
    /// (<c>recipient_features</c>, 21).
    /// </summary>
    public RecipientFeaturesTlv? RecipientFeatures => InnerPayload.RecipientFeatures;

    /// <summary>
    /// What the next trampoline node (or the recipient) must receive: the trampoline <c>amt_to_forward</c>, or inside a
    /// blinded route the amount computed from <c>payment_relay</c> (null when the processor was not given the
    /// incoming amounts).
    /// </summary>
    public LightningMoney? AmountToForward => Blinded is { } blinded ? blinded.AmountToForward
                                                                     : InnerPayload.AmtToForward;

    /// <summary>
    /// The absolute expiry the next node must receive: the trampoline <c>outgoing_cltv_value</c>, or inside a blinded
    /// route the outer <c>outgoing_cltv_value</c> minus <c>payment_relay.cltv_expiry_delta</c>.
    /// </summary>
    public uint? OutgoingCltvValue => Blinded is { } blinded ? blinded.OutgoingCltvValue
                                                             : InnerPayload.OutgoingCltvValue;

    /// <summary>
    /// The total this part's sender promises us over every part (<see cref="TrampolinePayloadValidator.GetOuterTotal"/>).
    /// </summary>
    public LightningMoney? IncomingTotal => TrampolinePayloadValidator.GetOuterTotal(OuterPayload);
}