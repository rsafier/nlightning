namespace NLightning.Infrastructure.Bitcoin.Gossip;

/// <summary>
/// Limits of <see cref="FundingOutputLookup"/> (BOLT 7 plan §3.4). The property names match the plan's
/// <c>Gossip:*</c> keys, so the host can bind this class from the <c>Gossip</c> section.
/// </summary>
public sealed class FundingOutputLookupOptions
{
    /// <summary>Lookups running against bitcoind at once (<c>Gossip:ChainLookupConcurrency</c>, default 4).</summary>
    public int ChainLookupConcurrency { get; set; } = 4;

    /// <summary>
    /// Lookups started per second, with a burst of the same size (<c>Gossip:ChainLookupsPerSecond</c>, default 50), to
    /// stay below bitcoind's <c>rpcworkqueue</c>.
    /// </summary>
    public int ChainLookupsPerSecond { get; set; } = 50;

    /// <summary>
    /// Blocks whose txid list is kept, least recently used evicted first (<c>Gossip:ChainLookupCacheHeights</c>,
    /// default 256).
    /// </summary>
    public int ChainLookupCacheHeights { get; set; } = 256;

    /// <summary>
    /// How long an "only spent in the mempool" answer is reused for the same short channel id while no new block was
    /// seen (<c>Gossip:MempoolSpentRecheckInterval</c>, default 10 minutes, about one block; NL-414). A new block ends
    /// it at once; the interval only bounds it when no block is reported (bitcoind stalled, or no chain monitor).
    /// </summary>
    public TimeSpan MempoolSpentRecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The invalid settings, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (ChainLookupConcurrency < 1)
            errors.Add($"{nameof(ChainLookupConcurrency)} must be at least 1");
        if (ChainLookupsPerSecond < 1)
            errors.Add($"{nameof(ChainLookupsPerSecond)} must be at least 1");
        if (ChainLookupCacheHeights < 1)
            errors.Add($"{nameof(ChainLookupCacheHeights)} must be at least 1");
        if (MempoolSpentRecheckInterval < TimeSpan.Zero)
            errors.Add($"{nameof(MempoolSpentRecheckInterval)} must not be negative");
        return errors;
    }
}