namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;

/// <summary>
/// What <see cref="IncomingOnionProcessor"/> decided for the onion of a locked-in incoming HTLC (ONION M4-T2).
/// </summary>
/// <remarks>
/// Exactly one of:
/// <list type="bullet">
///   <item><see cref="IncomingOnionForward"/>: we are an intermediate hop; apply the forwarding policy and offer
///   <see cref="IncomingOnionForward.NextPacket"/> on the channel of
///   <see cref="IncomingOnionForward.OutgoingShortChannelId"/>.</item>
///   <item><see cref="IncomingOnionFinal"/>: we are the final node; run <c>FinalHopProcessor</c>.</item>
///   <item><see cref="IncomingOnionMalformed"/>: fail with <c>update_fail_malformed_htlc</c>
///   (a BADONION code and <c>sha256_of_onion</c>; nothing to encrypt).</item>
///   <item><see cref="IncomingOnionFailed"/>: fail with <c>update_fail_htlc</c>, whose reason is
///   <c>IFailureOnionService.CreateErrorPacket(SharedSecret, Failure)</c>.</item>
/// </list>
/// Keep <see cref="SharedSecretOrNull"/> with the incoming HTLC: every later failure of it (policy, final hop,
/// downstream) is created or wrapped with it.
/// </remarks>
public abstract record IncomingOnionResult
{
    /// <summary>
    /// The shared secret with the origin, or null when the onion could not be peeled
    /// (<see cref="IncomingOnionMalformed"/>).
    /// </summary>
    public abstract Secret? SharedSecretOrNull { get; }
}