namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// What <see cref="RevokedCommitResolver"/> reads besides the rows the executor hands it (BOLT 5 plan O5-T2): the
/// channel, the revoked commitment on chain, the peer's revealed secret, the revocation log, the spends of watched
/// outputs, our broadcasts, a destination script and a feerate. Reads only: the resolver never saves (the executor
/// does).
/// </summary>
public interface IRevokedCommitDataSource
{
    /// <summary>
    /// Everything needed to rebuild the revoked commitment of <paramref name="close"/>; null (with the reason) when a
    /// piece is missing (unknown channel, no commitment number, a secret the shachain does not hold, the commitment
    /// transaction not found).
    /// </summary>
    Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, CancellationToken cancellationToken);

    /// <summary>
    /// The confirmed spend of <paramref name="transactionId"/>:<paramref name="outputIndex"/> in the active chain, as
    /// recorded by the chain monitor (watched outpoint), with the spending transaction when it can be fetched; null when
    /// none is recorded. A recorded spend whose transaction cannot be fetched (RPC error, pruned block without
    /// <c>txindex</c>) still comes back, with its txid and a null transaction, so the caller never mistakes it for no
    /// spend or guesses its spender.
    /// </summary>
    Task<RevokedOutputSpend?> GetSpendAsync(TxId transactionId, uint outputIndex, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="transactionId"/> is a transaction we saved for broadcast.</summary>
    Task<bool> IsOurTransactionAsync(TxId transactionId);

    /// <summary>The stored broadcast of <paramref name="transactionId"/>, or null.</summary>
    Task<BroadcastTransactionModel?> GetBroadcastAsync(TxId transactionId);

    /// <summary>A wallet script to pay the penalty to (credited to the wallet once it confirms).</summary>
    Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken);

    /// <summary>The current feerate estimate in sat per 1000 weight units (0 when there is none).</summary>
    Task<uint> GetFeeratePerKwAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The feerate estimate for confirming within <paramref name="confirmationTarget"/> blocks (NL-296); the node-wide
    /// estimate by default.
    /// </summary>
    Task<uint> GetFeeratePerKwAsync(uint confirmationTarget, CancellationToken cancellationToken) =>
        GetFeeratePerKwAsync(cancellationToken);
}

/// <summary>
/// The revoked commitment of a close, as <see cref="IRevokedCommitDataSource.LoadAsync"/> found it.
/// </summary>
/// <param name="Channel">The channel (keys, params, commitment snapshot).</param>
/// <param name="CommitmentTransaction">The revoked commitment on chain.</param>
/// <param name="Number">Its commitment number.</param>
/// <param name="PerCommitmentSecret">The peer's secret of that commitment (from our copy of its shachain).</param>
/// <param name="PerCommitmentPoint"><c>secret * G</c>.</param>
/// <param name="LogEntry">The revocation log entry (its spec), or null when the commitment had no HTLC or the log does
/// not cover it.</param>
/// <param name="LogStart">The first revoked commitment number the log covers for the channel.</param>
public sealed record RevokedCommitContext(
    ChannelModel Channel,
    ChainTx CommitmentTransaction,
    ulong Number,
    Secret PerCommitmentSecret,
    CompactPubKey PerCommitmentPoint,
    RevokedCommitmentModel? LogEntry,
    ulong LogStart)
{
    /// <summary>True when the commitment predates the revocation log: its HTLC outputs cannot be rebuilt.</summary>
    public bool PredatesLog => LogEntry is null && Number < LogStart;
}

/// <summary>
/// The result of <see cref="IRevokedCommitDataSource.LoadAsync"/>: the context, or why there is none.
/// </summary>
public sealed record RevokedCommitLoadResult(RevokedCommitContext? Context, string? Problem)
{
    public static RevokedCommitLoadResult Found(RevokedCommitContext context) => new(context, null);

    public static RevokedCommitLoadResult Missing(string problem) => new(null, problem);
}

/// <summary>
/// A confirmed spend of an output, with the spending transaction when it could be fetched.
/// </summary>
/// <param name="SpendingTransactionId">The spender's txid, as the watched outpoint recorded it.</param>
/// <param name="SpendingTransaction">The transaction (witnesses included), or null when it could not be fetched.</param>
/// <param name="Height">The height of its block.</param>
/// <param name="ByUs">True when it is a transaction we saved for broadcast.</param>
public sealed record RevokedOutputSpend(TxId SpendingTransactionId, ChainTx? SpendingTransaction, uint Height,
                                        bool ByUs);