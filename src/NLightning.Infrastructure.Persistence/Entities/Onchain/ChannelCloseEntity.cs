namespace NLightning.Infrastructure.Persistence.Entities.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The transaction that spent a channel's funding output, and its classification (BOLT 5 plan O1-T2). One row per
/// channel.
/// </summary>
public class ChannelCloseEntity
{
    public required ChannelId ChannelId { get; set; }

    /// <summary><c>Domain.Onchain.Enums.ChannelCloseKind</c>.</summary>
    public required byte Kind { get; set; }

    public required TxId CommitmentTxId { get; set; }
    public ulong? CommitmentNumber { get; set; }
    public required uint SpentAtHeight { get; set; }
    public required Hash BlockHash { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    // Default constructor for EF Core
    internal ChannelCloseEntity() { }
}