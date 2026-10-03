namespace NLightning.Application.Payments.Trampoline;

using Domain.Protocol.Onion.Models;

/// <summary>The final failure of the outgoing leg of a trampoline relay (NL-875).</summary>
/// <param name="Kind">The kind of failure.</param>
/// <param name="DownstreamPacketToRewrap">For <see cref="TrampolineLegFailureKind.DownstreamTrampolineError"/>: the
/// error packet after our outer-leg unwrap (<c>ITrampolineFailureOnionService.UnwrapDownstreamErrorPacket</c>), to
/// re-wrap per incoming part with <c>WrapTrampolineErrorPacket(trampolineSecret, outerSecret, packet)</c>.</param>
/// <param name="Failure">The decrypted failure when one of our outer hops reported it (for logs and the relay's own
/// answer).</param>
/// <param name="ErringNodeIsNextTrampoline">True when the failing node was the next trampoline itself.</param>
/// <param name="Reason">A human-readable reason for logs and the relay row.</param>
public sealed record TrampolineLegFailure(
    TrampolineLegFailureKind Kind,
    byte[]? DownstreamPacketToRewrap,
    FailureMessage? Failure,
    bool ErringNodeIsNextTrampoline,
    string Reason);