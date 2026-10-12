namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

/// <summary>
/// We are the final trampoline node (the recipient) of the HTLC: hand <see cref="MergedPayload"/> to
/// <c>FinalHopProcessor</c> (BOLTs PR 836 TR-R-12, decision D-TR4).
/// </summary>
/// <remarks>
/// The payment is identified by the trampoline payload: the HTLC set is counted against the <b>inner</b>
/// <c>total_msat</c> (or <c>total_amount_msat</c> at the end of a blinded route), keyed by payment hash, so a payment
/// split by the last trampoline node into several HTLCs, or sent through several trampoline legs, completes one set.
/// Each HTLC adds its outer <c>amt_to_forward</c> (what it carries); the expiry, secret, total and metadata are the
/// trampoline payload's. The cross-onion checks (outer <c>outgoing_cltv_value</c> not below the inner one, outer total
/// not below the inner <c>amt_to_forward</c>) were made by the processor.
/// </remarks>
/// <inheritdoc cref="IncomingOnionTrampolineResult"/>
public sealed record IncomingOnionTrampolineFinal(Secret OuterSharedSecret, HopPayload OuterPayload,
                                                  Secret TrampolineSharedSecret, HopPayload InnerPayload,
                                                  IncomingBlindedHop? Blinded = null)
    : IncomingOnionTrampolineResult(OuterSharedSecret, OuterPayload, TrampolineSharedSecret, InnerPayload, Blinded)
{
    private HopPayload? _mergedPayload;

    /// <summary>
    /// The payload the final-hop checks see: the trampoline payload's records with <c>amt_to_forward</c> replaced by
    /// the outer one (this HTLC's part of the payment).
    /// </summary>
    public HopPayload MergedPayload => _mergedPayload ??= Merge(OuterPayload, InnerPayload);

    /// <summary>
    /// This result as an ordinary final-hop result over <see cref="MergedPayload"/>, with the outer shared secret.
    /// </summary>
    public IncomingOnionFinal ToFinal() => new(OuterSharedSecret, MergedPayload, Blinded);

    private static HopPayload Merge(HopPayload outer, HopPayload inner)
    {
        var amount = outer.AmtToForward
                  ?? throw new InvalidOperationException("The outer trampoline payload has no amt_to_forward.");

        var records = inner.Tlvs.Where(tlv => tlv.Type != OnionPayloadTlvTypes.AmtToForward)
                           .Prepend(new AmtToForwardTlv(amount))
                           .ToArray<BaseTlv>();
        return new HopPayload(records);
    }
}