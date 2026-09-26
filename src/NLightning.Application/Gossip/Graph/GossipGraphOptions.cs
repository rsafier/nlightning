namespace NLightning.Application.Gossip.Graph;

using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// How the funding output of a received <c>channel_announcement</c> is checked when its block cannot be read (BOLT 7
/// plan §3.4, <c>Gossip:FundingValidation</c>).
/// </summary>
public enum FundingValidationMode
{
    /// <summary>An announcement whose funding block is unavailable (pruned bitcoind) is ignored.</summary>
    Full,

    /// <summary>
    /// An announcement whose funding block is unavailable is kept as <c>Unverified</c> (no capacity, never relayed).
    /// </summary>
    SkipUnavailable
}

/// <summary>
/// The graph ingress, store and pruner settings (BOLT 7 plan §3.3, §3.8, D2, D12), bound from the <c>Gossip</c>
/// section. The property names are the plan's <c>Gossip:*</c> keys.
/// </summary>
public sealed class GossipGraphOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Gossip";

    /// <summary>
    /// Receives, validates and stores graph gossip, and asks each gossip peer for its graph at connect. Unset (the
    /// default) means on everywhere but mainnet (plan D12: mainnet stays off until the G5 proof); read the effective
    /// value with <see cref="IsEnabledFor"/>.
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:Enabled</c>.</remarks>
    public bool? Enabled { get; set; }

    /// <summary>Messages queued per peer before its later ones are dropped (plan §3.8: 2,000).</summary>
    public int MaxQueuedPerPeer { get; set; } = 2_000;

    /// <summary>Messages queued in total before later ones are dropped (plan §3.8: 20,000).</summary>
    public int MaxQueued { get; set; } = 20_000;

    /// <summary>Validation workers; 0 means half the processors (at least 1).</summary>
    public int Workers { get; set; }

    /// <summary>
    /// <c>channel_update</c>s (and <c>node_announcement</c>s) kept while their channel (node) is unknown (plan §3.8:
    /// 10,000).
    /// </summary>
    public int MaxOrphans { get; set; } = 10_000;

    /// <summary>How long an orphan is kept (plan §3.8: 10 minutes).</summary>
    public TimeSpan OrphanTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Hashes of recently processed messages kept to drop exact duplicates cheaply.</summary>
    public int RecentMessageCacheSize { get; set; } = 50_000;

    /// <summary>
    /// Confirmations a funding output needs before its announcement is accepted (BOLT 7: 6). An announcement below
    /// it is retried later.
    /// </summary>
    public uint AnnouncementDepth { get; set; } = 6;

    /// <summary>What to do when a funding block cannot be read.</summary>
    public FundingValidationMode FundingValidation { get; set; } = FundingValidationMode.Full;

    /// <summary>Wait before a message whose chain check was transient is validated again.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Times a message with transient chain results is retried before it is dropped.</summary>
    public int MaxRetries { get; set; } = 10;

    /// <summary>Messages waiting for a retry at once; more are dropped.</summary>
    public int MaxPendingRetries { get; set; } = 5_000;

    /// <summary>The write-behind interval of the graph store (plan D2: 5 s).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Changes that trigger a write-behind flush before the interval (plan D2: 1,000).</summary>
    public int FlushBatchSize { get; set; } = 1_000;

    /// <summary>
    /// How long both nodes of a conflicting <c>channel_announcement</c> for the same funding output (a leaked key,
    /// B7-CA-04) are ignored.
    /// </summary>
    public TimeSpan ConflictBanDuration { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// A channel whose newest update of each direction is older than this is stale: excluded from routing (BOLT 7
    /// two weeks, B7-PR-02; <c>Gossip:StaleAfter</c>).
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(1_209_600);

    /// <summary>A stale channel is deleted from the graph after this long (<c>Gossip:DeleteStaleAfter</c>).</summary>
    public TimeSpan DeleteStaleAfter { get; set; } = TimeSpan.FromDays(28);

    /// <summary>Blocks after the funding spend at which a channel is forgotten (BOLT 7: 72).</summary>
    public uint SpentChannelRetentionBlocks { get; set; } = 72;

    /// <summary>The effective switch: <see cref="Enabled"/> when set, otherwise true on every chain but mainnet.</summary>
    public bool IsEnabledFor(BitcoinNetwork network) =>
        Enabled ?? !string.Equals(network.Name, NetworkConstants.Mainnet, StringComparison.OrdinalIgnoreCase);

    /// <summary>The number of workers to start.</summary>
    public int GetWorkerCount() => Workers > 0 ? Workers : Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The invalid settings, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxQueuedPerPeer < 1)
            errors.Add($"{nameof(MaxQueuedPerPeer)} must be at least 1");
        if (MaxQueued < 1)
            errors.Add($"{nameof(MaxQueued)} must be at least 1");
        if (Workers < 0)
            errors.Add($"{nameof(Workers)} must not be negative");
        if (MaxOrphans < 0)
            errors.Add($"{nameof(MaxOrphans)} must not be negative");
        if (AnnouncementDepth < 1)
            errors.Add($"{nameof(AnnouncementDepth)} must be at least 1");
        if (FlushInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(FlushInterval)} must be positive");
        if (FlushBatchSize < 1)
            errors.Add($"{nameof(FlushBatchSize)} must be at least 1");
        if (StaleAfter <= TimeSpan.Zero)
            errors.Add($"{nameof(StaleAfter)} must be positive");
        if (DeleteStaleAfter < StaleAfter)
            errors.Add($"{nameof(DeleteStaleAfter)} must not be shorter than {nameof(StaleAfter)}");
        return errors;
    }
}