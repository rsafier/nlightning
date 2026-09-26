namespace NLightning.Infrastructure.Persistence.Entities.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

/// <summary>
/// An output on chain that the node resolves, and where its resolution stands (BOLT 5 plan O1-T2). Keyed by the
/// outpoint. Keys are never stored: they are derived again from the channel's basepoints.
/// </summary>
public class OutputResolutionEntity
{
    public required TxId TransactionId { get; set; }
    public required uint OutputIndex { get; set; }
    public required ChannelId ChannelId { get; set; }

    /// <summary><c>Domain.Onchain.Enums.OutputDescriptorKind</c>.</summary>
    public required byte Descriptor { get; set; }

    /// <summary>The descriptor's data (resolver encoding; empty when it needs none).</summary>
    public required byte[] DescriptorData { get; set; }

    /// <summary><c>Domain.Channels.Enums.HtlcDirection</c> of an HTLC output.</summary>
    public byte? HtlcDirection { get; set; }

    public ulong? HtlcId { get; set; }

    /// <summary><c>Domain.Onchain.Enums.OutputResolutionState</c>.</summary>
    public required byte State { get; set; }

    public TxId? ResolvingTxId { get; set; }
    public uint? WaitUntilHeight { get; set; }
    public uint? DeadlineHeight { get; set; }
    public uint? ResolvedHeight { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    // Default constructor for EF Core
    internal OutputResolutionEntity() { }
}