namespace NLightning.Application.Channels.Close;

/// <summary>
/// Mutual close settings (BOLT2 plan N10), bound from <c>Node:Close</c> by the daemon.
/// </summary>
public sealed class ChannelCloseOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:Close";

    /// <summary>
    /// Send <c>fee_range</c> with our <c>closing_signed</c> (BOLT 2 SHOULD). Off only to exercise the legacy
    /// "strictly between" negotiation; an IPC close request can turn it off for one channel.
    /// </summary>
    public bool SendFeeRange { get; set; } = true;

    /// <summary>The confirmations after which a mutual close transaction makes the channel Closed.</summary>
    public uint ConfirmationDepth { get; set; } = 6;

    /// <summary>
    /// As the funder, the highest fee we accept is this many times our estimate (never below the relay floor, never
    /// above our balance).
    /// </summary>
    public uint MaxFeeMultiplier { get; set; } = 3;
}