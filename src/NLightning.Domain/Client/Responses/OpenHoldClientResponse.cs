namespace NLightning.Domain.Client.Responses;

/// <summary>The hold on channel opens (<c>ClientCommand.OpenHold</c>, NL-1392).</summary>
/// <param name="Held">True when new channels are refused now.</param>
/// <param name="Reason">Why they are refused, or null.</param>
/// <param name="AwaitingRestore">True while the start hold of <c>Node:Backup:HoldOpensUntilRestore</c> is on.</param>
/// <param name="RestoreInProgress">True while a <c>restorechanbackup</c> runs (lifted when it ends).</param>
/// <param name="Released">True when this request lifted the start hold.</param>
public sealed record OpenHoldClientResponse(bool Held, string? Reason, bool AwaitingRestore, bool RestoreInProgress,
                                            bool Released);