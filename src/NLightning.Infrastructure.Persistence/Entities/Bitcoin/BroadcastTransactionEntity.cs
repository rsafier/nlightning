namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A transaction we broadcast, with its raw bytes for rebroadcast (BOLT 5 plan O0-T1). Keyed by its txid.
/// </summary>
public class BroadcastTransactionEntity
{
    public required TxId TransactionId { get; set; }
    public ChannelId? ChannelId { get; set; }
    public required byte[] RawTransaction { get; set; }

    /// <summary><c>Domain.Onchain.Enums.BroadcastPurpose</c>.</summary>
    public required byte Purpose { get; set; }

    public required long FeeratePerKw { get; set; }
    public TxId? ReplacesTransactionId { get; set; }
    public required uint FirstBroadcastHeight { get; set; }

    /// <summary><c>Domain.Onchain.Enums.BroadcastState</c>.</summary>
    public required byte State { get; set; }

    public uint? ConfirmedHeight { get; set; }
    public Hash? ConfirmedBlockHash { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Our local commitment number, for a <c>LocalCommitment</c> broadcast (migration
    /// <c>AddBroadcastCommitmentNumber</c>; NL-271, NL-297). Stored as <c>long</c>: commitment numbers are 48-bit.
    /// </summary>
    public long? CommitmentNumber { get; set; }

    // Default constructor for EF Core
    internal BroadcastTransactionEntity() { }
}