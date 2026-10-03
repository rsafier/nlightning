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

    /// <summary>
    /// How long the peer has to answer a <c>closing_signed</c> of ours before we fail the channel (BOLT 2: "if it
    /// doesn't receive a closing_signed response after a reasonable amount of time: MUST fail the channel",
    /// B2-CLS-03). Counted on the current connection only. Zero or less turns it off.
    /// </summary>
    public TimeSpan ClosingSignedReplyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long, from the first <c>fee_range</c> that did not overlap ours, the peer has to send one that does before
    /// we fail the channel (B2-CLS-R04 MUST). Zero or less turns it off.
    /// </summary>
    public TimeSpan FeeRangeTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a fetched fee estimate serves new closing negotiations before it is fetched again.</summary>
    public TimeSpan FeeEstimateMaxAge { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>After a failed fee estimate fetch, how long no close fetches again (the last value, then the cached
    /// one, is used meanwhile).</summary>
    public TimeSpan FeeEstimateRetryAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest a negotiation that starts under the channel's lock (the peer's inbound loop) waits for a fee
    /// estimate fetch; the fetch goes on in the background. Zero or less never waits.
    /// </summary>
    public TimeSpan FeeEstimateWaitUnderLock { get; set; } = TimeSpan.FromSeconds(2);
}