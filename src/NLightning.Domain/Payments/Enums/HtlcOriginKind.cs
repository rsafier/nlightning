namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Why we offer an outgoing HTLC.
/// </summary>
public enum HtlcOriginKind : byte
{
    /// <summary>
    /// We are the payer: the HTLC belongs to one of our payments (<c>IPaymentService</c>).
    /// </summary>
    Local = 0,

    /// <summary>
    /// We forward it: the HTLC continues an incoming HTLC on another (or the same) channel.
    /// </summary>
    Forwarded = 1
}