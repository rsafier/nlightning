namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.ValueObjects;

/// <summary>One transaction of the wallet's durable history (NL-1187, migration AddWalletTransactions).</summary>
public sealed class WalletTransactionEntity
{
    public required TxId TransactionId { get; set; }
    public required byte[] RawTransaction { get; set; }

    /// <summary>Null after a reorg disconnected its block.</summary>
    public uint? BlockHeight { get; set; }

    public byte[]? BlockHash { get; set; }
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>The wallet's output indexes, comma separated (<c>0,2</c>).</summary>
    public required string OurOutputs { get; set; }

    /// <summary>The wallet's inputs as index:value in sat, comma separated (<c>1:5000,3:7000</c>).</summary>
    public required string OurInputs { get; set; }

    /// <summary>Owned output values/scripts and input outpoints/values; null until historical backfill.</summary>
    public string? OwnershipSummary { get; set; }

    // Default constructor for EF Core
    internal WalletTransactionEntity() { }
}