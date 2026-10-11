namespace NLightning.Domain.Client.Requests;

/// <summary>Shows or lifts the hold on channel opens (<c>ClientCommand.OpenHold</c>, NL-1392).</summary>
/// <param name="Release">Lift the start hold of <c>Node:Backup:HoldOpensUntilRestore</c>; false only shows it.</param>
public sealed record OpenHoldClientRequest(bool Release);