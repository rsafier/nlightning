namespace NLightning.Application.Payments.Trampoline;

/// <summary>Why the outgoing leg of a trampoline relay failed (NL-875).</summary>
public enum TrampolineLegFailureKind
{
    /// <summary>No route to the next node within the fee and expiry budget.</summary>
    NoRoute = 0,

    /// <summary>The error came from the next trampoline node (or beyond it) and must be re-wrapped for the origin:
    /// <see cref="TrampolineLegFailure.DownstreamPacketToRewrap"/> holds the packet (BOLT 4 trampoline errors).</summary>
    DownstreamTrampolineError = 1,

    /// <summary>An outer hop of our leg failed for good (retries exhausted, or a failure that stops the leg), or the next
    /// trampoline node answered on its outer layer only (<see cref="TrampolineLegFailure.ErringNodeIsNextTrampoline"/>
    /// set, never retried: its error is encrypted for us, not for the origin, so it cannot be re-wrapped); the relay
    /// answers with its own error (<c>temporary_trampoline_failure</c> or <c>unknown_next_trampoline</c>).
    /// <see cref="TrampolineLegFailure.Failure"/> holds the last decrypted failure when one was read.</summary>
    RouteFailure = 2,

    /// <summary>The leg's deadline passed before it completed.</summary>
    Timeout = 3,

    /// <summary>A local refusal (link down, channel quiescent, node shutting down, ...).</summary>
    LocalFailure = 4
}