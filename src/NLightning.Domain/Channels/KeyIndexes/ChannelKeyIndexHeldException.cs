namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>
/// A new channel key index was refused because the <see cref="IChannelKeyIndexGate"/> is held (NL-1392): a restore is
/// in progress, or the node waits for one. Retryable: try the open again once the restore completed.
/// </summary>
public sealed class ChannelKeyIndexHeldException : InvalidOperationException
{
    public ChannelKeyIndexHeldException(string reason) : base(ChannelKeyIndexHold.Refusal("A new channel", reason))
    {
        Reason = reason;
    }

    /// <summary>Why the gate is held.</summary>
    public string Reason { get; }
}