namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// The outcome of <see cref="QuiescenceRules.Receive"/>: the next state, or the violation (the state is then
/// unchanged).
/// </summary>
public union StfuReceiveResult(QuiescenceState, QuiescenceViolation);