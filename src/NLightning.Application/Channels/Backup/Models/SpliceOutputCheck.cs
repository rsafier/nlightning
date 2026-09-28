namespace NLightning.Application.Channels.Backup.Models;

/// <summary>
/// What <see cref="Interfaces.IFundingSpendLocator.CheckSpliceOutputAsync"/> found for one P2WSH output of a
/// transaction that spent a recovery channel's funding output without being a commitment (a splice whose new funding
/// output could not be recognized, NL-478 review).
/// </summary>
public enum SpliceOutputStatus
{
    /// <summary>The chain could not be read, or the output's spend was not found: nothing can be told yet.</summary>
    Unknown,

    /// <summary>The output is confirmed and unspent: its spend (the peer's commitment) will tell.</summary>
    Unspent,

    /// <summary>
    /// The output was spent with a 2-of-2 witness that holds one of our funding keys: it is the channel's next funding
    /// output (<see cref="SpliceOutputCheck.Next"/>).
    /// </summary>
    Followed,

    /// <summary>The output was spent by a transaction whose witness names none of our funding keys: not the channel's.
    /// </summary>
    NotTheChannel
}

/// <summary>The outcome of <see cref="Interfaces.IFundingSpendLocator.CheckSpliceOutputAsync"/>.</summary>
/// <param name="Status">What was found.</param>
/// <param name="Next">For <see cref="SpliceOutputStatus.Followed"/>: the entry moved to that output, with both keys and
/// our key index read from the witness.</param>
public sealed record SpliceOutputCheck(SpliceOutputStatus Status, ChannelBackupEntry? Next = null);