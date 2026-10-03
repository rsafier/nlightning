using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Response for NodeInfo command
/// </summary>
[MessagePackObject]
public sealed class NodeInfoIpcResponse
{
    [Key(0)] public required CompactPubKey PubKey { get; init; }
    [Key(1)] public required List<string> ListeningTo { get; init; }
    [Key(2)] public BitcoinNetwork Network { get; init; }
    [Key(3)] public Hash BestBlockHash { get; init; }
    [Key(4)] public long BestBlockHeight { get; init; }
    [Key(5)] public DateTimeOffset? BestBlockTime { get; init; }
    [Key(6)] public string? Implementation { get; set; } = "NLightning";
    [Key(7)] public string? Version { get; init; }

    /// <summary>Connected peers; null from a daemon that does not report it.</summary>
    [Key(8)] public int? PeerCount { get; init; }

    /// <summary>Channels in the Open state.</summary>
    [Key(9)] public int? ActiveChannelCount { get; init; }

    /// <summary>Channels being opened (funding not locked yet).</summary>
    [Key(10)] public int? PendingChannelCount { get; init; }

    /// <summary>Channels shutting down, failed or resolving on chain, not Closed yet.</summary>
    [Key(11)] public int? ClosingChannelCount { get; init; }

    /// <summary>The Tor mode (<c>Off</c>, <c>Hybrid</c>, <c>TorOnly</c>); null from a daemon that does not report it.
    /// </summary>
    [Key(12)] public string? TorMode { get; init; }

    /// <summary>Our onion service, <c>pubkey@&lt;56 chars&gt;.onion:port</c>, once Tor accepted it.</summary>
    [Key(13)] public string? OnionAddress { get; init; }
}