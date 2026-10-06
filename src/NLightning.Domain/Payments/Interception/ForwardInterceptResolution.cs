namespace NLightning.Domain.Payments.Interception;

using Crypto.ValueObjects;
using Protocol.Onion.Enums;

/// <summary>What an interceptor decided for a held forward (LND's <c>ResolveHoldForwardAction</c>, NL-1183).</summary>
public enum ForwardInterceptAction
{
    /// <summary>Forward it as if it had not been held (the outgoing channel and the policy are checked then).</summary>
    Resume,

    /// <summary>Fail the incoming HTLC back.</summary>
    Fail,

    /// <summary>Fulfill the incoming HTLC with a preimage the interceptor knows.</summary>
    Settle
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
public sealed record ForwardInterceptResolution(
    ForwardInterceptAction Action,
    Secret? Preimage = null,
    FailureCode FailureCode = FailureCode.TemporaryChannelFailure,
    byte[]? ErrorPacket = null)
{
    /// <summary>Resume the forward.</summary>
    public static ForwardInterceptResolution Resume { get; } = new(ForwardInterceptAction.Resume);
}