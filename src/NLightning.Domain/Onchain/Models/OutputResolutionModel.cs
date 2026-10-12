namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;
using Enums;

/// <summary>
/// One output of a commitment or second-level transaction on chain that the node must resolve, and where its
/// resolution stands (BOLT 5 plan §3.3, table <c>OutputResolutions</c>, keyed by the outpoint).
/// </summary>
/// <remarks>
/// Resolvers change it with <c>with</c> and write it back through
/// <see cref="Interfaces.IOnchainResolutionDbRepository.UpsertOutputAsync"/> in the save that decided the change.
/// </remarks>
public sealed record OutputResolutionModel
{
    /// <summary>The transaction holding the output.</summary>
    public required TxId TransactionId { get; init; }

    /// <summary>The output index.</summary>
    public required uint OutputIndex { get; init; }

    public required ChannelId ChannelId { get; init; }

    public required OutputDescriptorKind Descriptor { get; init; }

    /// <summary>What <see cref="Descriptor"/> needs beyond the outpoint, in the resolver's encoding (never keys).</summary>
    public byte[] DescriptorData { get; init; } = [];

    /// <summary>The HTLC direction, for an HTLC output (from our point of view).</summary>
    public HtlcDirection? HtlcDirection { get; init; }

    /// <summary>The HTLC id, for an HTLC output.</summary>
    public ulong? HtlcId { get; init; }

    public OutputResolutionState State { get; init; } = OutputResolutionState.Pending;

    /// <summary>Our transaction that resolves it, once saved for broadcast.</summary>
    public TxId? ResolvingTransactionId { get; init; }

    /// <summary>The height to wait for (CSV delay or CLTV expiry) before acting.</summary>
    public uint? WaitUntilHeight { get; init; }

    /// <summary>The height by which it must be resolved (penalty window, HTLC deadline).</summary>
    public uint? DeadlineHeight { get; init; }

    /// <summary>The height of the block holding the spend that resolved it.</summary>
    public uint? ResolvedHeight { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}