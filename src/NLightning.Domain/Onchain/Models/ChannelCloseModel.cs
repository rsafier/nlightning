namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// The transaction that spent a channel's funding output in the active chain, and what it is (BOLT 5 plan §3.2 step 3,
/// table <c>ChannelCloses</c>). Written with the channel's move to <c>OnchainResolving</c> and its output descriptors
/// in one save.
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Kind">The classification of the spend.</param>
/// <param name="CommitmentTransactionId">The transaction that spent the funding output.</param>
/// <param name="CommitmentNumber">The commitment number it carries, for a commitment transaction.</param>
/// <param name="SpentAtHeight">The height of the block holding it.</param>
/// <param name="BlockHash">The hash of that block.</param>
/// <param name="CreatedAt">When it was recorded.</param>
public sealed record ChannelCloseModel(
    ChannelId ChannelId,
    ChannelCloseKind Kind,
    TxId CommitmentTransactionId,
    ulong? CommitmentNumber,
    uint SpentAtHeight,
    Hash BlockHash,
    DateTimeOffset CreatedAt);