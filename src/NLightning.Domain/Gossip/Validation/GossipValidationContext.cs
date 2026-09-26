namespace NLightning.Domain.Gossip.Validation;

using Crypto.ValueObjects;
using Protocol.ValueObjects;

/// <summary>
/// What <see cref="GossipValidator"/> needs to know about us and the world. Pure data: the caller reads the clock
/// and the chain tip.
/// </summary>
/// <param name="ChainHash">Our chain; any other <c>chain_hash</c> is unknown.</param>
/// <param name="NowUnixSeconds">The current UNIX time.</param>
/// <param name="TipHeight">Our best block height, or null to skip the depth check (it is then left to the chain
/// stage).</param>
public sealed record GossipValidationContext(ChainHash ChainHash, ulong NowUnixSeconds, uint? TipHeight = null)
{
    /// <summary>
    /// The confirmations a funding output needs before its announcement is accepted (BOLT 7: 6).
    /// </summary>
    public uint MinConfirmations { get; init; } = 6;

    /// <summary>
    /// How many confirmations short of <see cref="MinConfirmations"/> are still accepted, in case we have not seen
    /// the latest block(s) yet (BOLT 7 MAY). Default 1.
    /// </summary>
    public uint DepthToleranceBlocks { get; init; } = 1;

    /// <summary>
    /// How far a <c>channel_update</c> timestamp may be ahead of <see cref="NowUnixSeconds"/> (BOLT 7 MAY discard
    /// "unreasonably far in the future"; the same 14 days as <c>ChannelUpdateService.MaxFutureTimestamp</c>).
    /// </summary>
    public TimeSpan MaxFutureSkew { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// The age after which a <c>channel_update</c> is stale (BOLT 7: two weeks, 1,209,600 s).
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(1_209_600);

    /// <summary>
    /// Ignore updates older than <see cref="StaleAfter"/> (BOLT 7 MAY ignore stale channels; B7-PR-02). Default true.
    /// </summary>
    public bool IgnoreStaleUpdates { get; init; } = true;

    /// <summary>
    /// Returns true for a blacklisted node (B7-CA-04); null means none is.
    /// </summary>
    public Func<CompactPubKey, bool>? IsBlacklisted { get; init; }
}