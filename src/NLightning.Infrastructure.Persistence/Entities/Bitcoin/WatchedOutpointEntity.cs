namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// An outpoint the chain monitor watches for a spend (BOLT 5 plan O0-T2). Keyed by the outpoint.
/// </summary>
public class WatchedOutpointEntity
{
    public required TxId TransactionId { get; set; }
    public required uint OutputIndex { get; set; }
    public required ChannelId ChannelId { get; set; }

    /// <summary><c>Domain.Onchain.Enums.WatchedOutpointPurpose</c>.</summary>
    public required byte Purpose { get; set; }

    public TxId? SpentByTransactionId { get; set; }
    public uint? SpentAtHeight { get; set; }
    public Hash? SpentBlockHash { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    // Default constructor for EF Core
    internal WatchedOutpointEntity() { }
}