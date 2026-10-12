namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>The refusal texts of the channel key index gate (NL-1392, <see cref="IChannelKeyIndexGate"/>).</summary>
public static class ChannelKeyIndexHold
{
    /// <summary>The ledger entry of the gate, used as the requirement id of the refusals.</summary>
    public const string RequirementId = "NL-1392";

    /// <summary>Why the gate is held while a restore runs.</summary>
    public const string RestoreInProgressReason = "a channel restore (restorechanbackup) is in progress";

    /// <summary>Why the gate is held at a start with <c>Node:Backup:HoldOpensUntilRestore</c>.</summary>
    public const string AwaitingRestoreReason =
        "the node waits for a channel restore (Node:Backup:HoldOpensUntilRestore): run restorechanbackup, or lift the "
      + "hold with 'openhold --release'";

    /// <summary>What the peer is told: never the internal reason.</summary>
    public const string PeerMessage = "Not accepting channels right now, try again later";

    /// <summary>The refusal text for <paramref name="operation"/>.</summary>
    public static string Refusal(string operation, string reason) =>
        $"[{RequirementId}] {operation} refused: {reason}; try again later";
}