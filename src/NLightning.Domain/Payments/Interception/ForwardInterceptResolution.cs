namespace NLightning.Domain.Payments.Interception;

using Crypto.ValueObjects;
using Keysend;
using Money;
using Protocol.Onion.Enums;

/// <summary>What an interceptor decided for a held forward (LND's <c>ResolveHoldForwardAction</c>, NL-1183).</summary>
public enum ForwardInterceptAction
{
    /// <summary>Forward it as if it had not been held (the outgoing channel and the policy are checked then).</summary>
    Resume,

    /// <summary>Fail the incoming HTLC back.</summary>
    Fail,

    /// <summary>Fulfill the incoming HTLC with a preimage the interceptor knows.</summary>
    Settle,

    /// <summary>
    /// Forward it with modifications (LND's <c>RESUME_MODIFIED</c>, NL-1182): an incoming amount used in place of the
    /// HTLC's for the forwarding checks, the outgoing amount of the <c>update_add_htlc</c> and custom records merged into
    /// its extension.
    /// </summary>
    ResumeModified
}

/// <summary>
/// An interceptor's resolution of a held forward (NL-1183).
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Preimage">For <see cref="ForwardInterceptAction.Settle"/>: the preimage of the payment hash.</param>
/// <param name="FailureCode">For <see cref="ForwardInterceptAction.Fail"/>: our failure (LND allows
/// <c>temporary_channel_failure</c> and the three BADONION codes); ignored with <paramref name="ErrorPacket"/>.</param>
/// <param name="ErrorPacket">For <see cref="ForwardInterceptAction.Fail"/>: an error packet built by the interceptor
/// (292 bytes), obfuscated with the incoming shared secret like a downstream error.</param>
/// <param name="InAmount">For <see cref="ForwardInterceptAction.ResumeModified"/>: the incoming amount the forwarding
/// checks use instead of the HTLC's (LND's <c>in_amount_msat</c>; it never changes what the peer committed); null keeps
/// the HTLC's.</param>
/// <param name="OutAmount">For <see cref="ForwardInterceptAction.ResumeModified"/>: the amount of the outgoing
/// <c>update_add_htlc</c> (LND's <c>out_amount_msat</c>); null keeps the onion's <c>amt_to_forward</c>.</param>
/// <param name="OutWireCustomRecords">For <see cref="ForwardInterceptAction.ResumeModified"/>: custom records (types of
/// 65536 or more) for the outgoing <c>update_add_htlc</c>'s extension (LND's <c>out_wire_custom_records</c>), merged
/// over the existing ones; null or empty adds none.</param>
public sealed record ForwardInterceptResolution(
    ForwardInterceptAction Action,
    Secret? Preimage = null,
    FailureCode FailureCode = FailureCode.TemporaryChannelFailure,
    byte[]? ErrorPacket = null,
    LightningMoney? InAmount = null,
    LightningMoney? OutAmount = null,
    IReadOnlyList<CustomRecord>? OutWireCustomRecords = null)
{
    /// <summary>Resume the forward.</summary>
    public static ForwardInterceptResolution Resume { get; } = new(ForwardInterceptAction.Resume);

    /// <summary>
    /// A <see cref="ForwardInterceptAction.ResumeModified"/> resolution; a zero amount means unchanged (LND).
    /// </summary>
    /// <exception cref="ArgumentException">A custom record's type is below 65536 or appears twice (LND: "failed to
    /// validate custom records").</exception>
    public static ForwardInterceptResolution Modified(LightningMoney? inAmount, LightningMoney? outAmount,
                                                      IEnumerable<CustomRecord>? outWireCustomRecords)
    {
        var records = WireCustomRecordCodec.Validate(outWireCustomRecords);
        return new ForwardInterceptResolution(ForwardInterceptAction.ResumeModified,
                                              InAmount: inAmount is { IsZero: false } ? inAmount : null,
                                              OutAmount: outAmount is { IsZero: false } ? outAmount : null,
                                              OutWireCustomRecords: records.Count == 0 ? null : records);
    }
}