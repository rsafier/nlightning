namespace NLightning.Daemon.Contracts.Control;

/// <summary>
/// Transport-agnostic response for NodeInfo command.
/// </summary>
public sealed class NodeInfoResponse
{
    public string? NodeId { get; init; }
    public string? OwnerId { get; init; }
    public string? SignerId { get; init; }
    public bool? SilentPaymentRecoverableElsewhere { get; init; }

    public required string PubKey { get; init; }
    public required string ListeningTo { get; init; }
    public string Network { get; init; } = string.Empty;
    public string BestBlockHash { get; init; } = string.Empty;
    public long BestBlockHeight { get; init; }
    public DateTimeOffset? BestBlockTime { get; init; }
    public string? Implementation { get; init; } = "NLightning";
    public string? Version { get; init; }

    /// <summary>The Tor mode (<c>Off</c>, <c>Hybrid</c>, <c>TorOnly</c>).</summary>
    public string? TorMode { get; init; }

    /// <summary>Our onion service as <c>pubkey@&lt;56 chars&gt;.onion:port</c> once Tor accepted it; else null.</summary>
    public string? OnionAddress { get; init; }
}