namespace NLightning.Application.Gossip.Relay;

using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The relay of other nodes' gossip (BOLT 7 plan §3.7, G3-T3, D12), bound from the <c>Gossip</c> section. Our own
/// gossip keeps <c>Gossip:OwnGossipFlushInterval</c> (<c>GossipOptions</c>).
/// </summary>
public sealed class GossipRelayOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Gossip";

    /// <summary>
    /// Whether we relay the gossip of other nodes to our peers. Unset (the default) means on everywhere but mainnet
    /// (plan D12). Our own gossip is always sent.
    /// </summary>
    /// <remarks>Configuration key <c>Gossip:RelayEnabled</c>.</remarks>
    public bool? RelayEnabled { get; set; }

    /// <summary>
    /// How often each peer gets the gossip accepted since its last flush (BOLT 7: SHOULD flush every 60 seconds). Each
    /// connection has its own phase in the interval (staggered), so the peers are not all served at once.
    /// </summary>
    public TimeSpan RelayFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the relay looks for newly accepted gossip in the graph (a scan of the graph snapshot).
    /// </summary>
    public TimeSpan RelayCollectInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The relay's clock tick: per-peer flushes and the backlog pacing run on it.
    /// </summary>
    public TimeSpan RelayTickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How fast the graph backlog a new <c>gossip_timestamp_filter</c> asks for goes out, per peer (plan: 1,000).
    /// </summary>
    public int BacklogMessagesPerSecond { get; set; } = 1_000;

    /// <summary>How many received message versions keep their origin peers (origin suppression).</summary>
    public int MaxTrackedOrigins { get; set; } = GossipOriginTracker.DefaultCapacity;

    /// <summary>The effective switch: <see cref="RelayEnabled"/> when set, otherwise true on every chain but mainnet.
    /// </summary>
    public bool IsRelayEnabledFor(BitcoinNetwork network) =>
        RelayEnabled ?? !string.Equals(network.Name, NetworkConstants.Mainnet, StringComparison.OrdinalIgnoreCase);

    /// <summary>The invalid settings, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (RelayFlushInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(RelayFlushInterval)} must be positive");
        if (RelayCollectInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(RelayCollectInterval)} must be positive");
        if (RelayTickInterval <= TimeSpan.Zero)
            errors.Add($"{nameof(RelayTickInterval)} must be positive");
        if (BacklogMessagesPerSecond < 1)
            errors.Add($"{nameof(BacklogMessagesPerSecond)} must be at least 1");
        if (MaxTrackedOrigins < 1)
            errors.Add($"{nameof(MaxTrackedOrigins)} must be at least 1");
        return errors;
    }
}