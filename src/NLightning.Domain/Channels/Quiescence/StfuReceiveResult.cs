namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// The outcome of <see cref="QuiescenceRules.Receive"/>: the next state, or the unchanged state and a violation.
/// </summary>
/// <param name="Next">The state after the <c>stfu</c> (unchanged when <paramref name="Violation"/> is set).</param>
/// <param name="Violation">The protocol violation, or null.</param>
public readonly record struct StfuReceiveResult(QuiescenceState Next, QuiescenceViolation? Violation);