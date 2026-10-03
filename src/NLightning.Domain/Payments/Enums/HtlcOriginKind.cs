namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Why we offer an outgoing HTLC. Persisted as a byte; never renumber.
/// </summary>
/// <remarks>
/// 0 is deliberately not a member: <c>default(HtlcOrigin)</c> has kind 0 and is invalid
/// (<c>HtlcOrigin.IsValid</c> is false), so an origin that was never set cannot be persisted as a real one.
/// </remarks>
public enum HtlcOriginKind : byte
{
    /// <summary>
    /// We are the payer: the HTLC belongs to one of our payments (<c>IPaymentService</c>).
    /// </summary>
    Local = 1,

    /// <summary>
    /// We forward it: the HTLC continues an incoming HTLC on another (or the same) channel.
    /// </summary>
    Forwarded = 2,

    /// <summary>
    /// We relay a trampoline payment (BOLTs PR 836, NL-875): the HTLC is one part of the outgoing payment of the
    /// trampoline relay with this payment hash, which answers for the whole set of incoming HTLCs of that relay
    /// (<c>TrampolineRelayModel</c>, <c>ITrampolineRelayDbRepository</c>). It is not a forward circuit: N incoming
    /// HTLCs map to M outgoing ones.
    /// </summary>
    Trampoline = 3
}