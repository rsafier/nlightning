namespace NLightning.Domain.Payments.Models;

using Protocol.Onion.Enums;

/// <summary>
/// The outcome of <c>IForwardingPolicy.Evaluate</c>: forward, or fail the incoming HTLC with a BOLT 4 failure code
/// and the fixed fields that code reports.
/// </summary>
/// <remarks>
/// The caller builds the <c>FailureMessage</c> from it (adding the <c>channel_update</c> of the outgoing channel for
/// UPDATE codes, empty until BOLT 7 exists) and wraps it with the incoming onion's shared secret.
/// </remarks>
public sealed record ForwardingDecision
{
    /// <summary>
    /// Null when the HTLC may be forwarded.
    /// </summary>
    public FailureCode? FailureCode { get; }

    /// <summary>
    /// <c>htlc_msat</c> for <c>amount_below_minimum</c> (the outgoing amount) and <c>fee_insufficient</c> (the incoming
    /// amount), as BOLT 4 requires.
    /// </summary>
    public ulong? HtlcMsat { get; }

    /// <summary>
    /// <c>cltv_expiry</c> for <c>incorrect_cltv_expiry</c>: the outgoing HTLC's <c>cltv_expiry</c>
    /// (<c>outgoing_cltv_value</c>), as BOLT 4 requires.
    /// </summary>
    public uint? CltvExpiry { get; }

    public bool IsForward => FailureCode is null;

    private ForwardingDecision(FailureCode? failureCode, ulong? htlcMsat, uint? cltvExpiry)
    {
        FailureCode = failureCode;
        HtlcMsat = htlcMsat;
        CltvExpiry = cltvExpiry;
    }

    public static ForwardingDecision Forward { get; } = new(null, null, null);

    /// <summary>
    /// Fail with a code that reports no amount or expiry (<c>unknown_next_peer</c>, <c>temporary_channel_failure</c>,
    /// <c>expiry_too_soon</c>, <c>expiry_too_far</c>, ...).
    /// </summary>
    public static ForwardingDecision Fail(FailureCode failureCode) => new(failureCode, null, null);

    /// <summary>
    /// <c>amount_below_minimum</c> with the outgoing HTLC amount.
    /// </summary>
    public static ForwardingDecision AmountBelowMinimum(ulong outgoingHtlcMsat) =>
        new(Protocol.Onion.Enums.FailureCode.AmountBelowMinimum, outgoingHtlcMsat, null);

    /// <summary>
    /// <c>fee_insufficient</c> with the incoming HTLC amount.
    /// </summary>
    public static ForwardingDecision FeeInsufficient(ulong incomingHtlcMsat) =>
        new(Protocol.Onion.Enums.FailureCode.FeeInsufficient, incomingHtlcMsat, null);

    /// <summary>
    /// <c>incorrect_cltv_expiry</c> with the outgoing HTLC's <c>cltv_expiry</c>.
    /// </summary>
    public static ForwardingDecision IncorrectCltvExpiry(uint outgoingCltvExpiry) =>
        new(Protocol.Onion.Enums.FailureCode.IncorrectCltvExpiry, null, outgoingCltvExpiry);
}