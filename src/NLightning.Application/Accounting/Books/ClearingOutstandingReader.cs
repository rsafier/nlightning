using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Accounting.Books;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// What the clearing account holds for transactions the node knows to be in flight (NL-621): reported apart as
/// outstanding by the reconcile, never as drift.
/// </summary>
/// <param name="Msat">The clearing balance the in-flight transactions explain.</param>
/// <param name="TransactionCount">How many in-flight transactions hold some of it.</param>
/// <param name="CandidateCount">How many in-flight transactions were looked at.</param>
internal sealed record ClearingOutstanding(long Msat, int TransactionCount, int CandidateCount)
{
    public static ClearingOutstanding None { get; } = new(0, 0, 0);
}

/// <summary>
/// Reads the part of the books' clearing balance that transactions the node knows about, unconfirmed or below their
/// depth, explain (NL-621): the clearing account nets each transaction's wallet side (its wallet inputs spent, its
/// outputs into the wallet, posted at its first block) against its channel side (a funding's
/// <c>ChannelFunded</c> or a splice's <c>SpliceLocked</c> at the lock, a mutual close at its depth, a withdrawal or CPFP
/// child at its block), so between the two the transaction holds a clearing balance that is expected, not a drift.
/// </summary>
/// <remarks>
/// <para><b>In flight</b>: the pending broadcasts (sent, not in a block), the watched transactions not completed (a
/// funding, splice or mutual close below its depth) and the pending splice fundings of the snapshot's loaded channels
/// (a splice past its depth whose <c>splice_locked</c> exchange has not finished). Bounded by the node's live state,
/// never by its history (plan §10).</para>
/// <para><b>Per transaction</b>, the clearing postings of the books' entries of its facts, read by exact key (indexed
/// lookups, no scan): the spend of each wallet input it spends (only when that spend's <c>spentBy</c> is this
/// transaction: RBF siblings spend the same inputs), each of its outputs received into the wallet, and its channel side
/// (<c>ChannelFunded</c>, <c>SpliceLocked</c>, <c>ChannelClosedMutual</c> of its channel, <c>WalletSent</c>,
/// <c>AnchorCpfpFee</c>); for each fact every confirmation generation and its reversal (a reorg), so a transaction
/// reorged out and confirmed again counts once. The transaction's bytes come from its broadcast row, or for a mutual
/// close without one from the channel's closing transaction; a transaction whose bytes are unknown has only its channel
/// side read.</para>
/// </remarks>
internal static class ClearingOutstandingReader
{
    /// <summary>The most confirmation generations of one fact walked (each needs a reorg).</summary>
    private const int MaxGenerations = 64;

    /// <summary>The detail of a <c>WalletOutputSpent</c> naming the spending transaction (the chain monitor's).</summary>
    private const string SpentByDetail = "spentBy";

    public static async Task<ClearingOutstanding> ReadAsync(IUnitOfWork unitOfWork, AccountingSnapshot snapshot,
                                                            ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidates = await CollectCandidatesAsync(unitOfWork, snapshot, logger);
        long total = 0;
        var holding = 0;
        foreach (var candidate in candidates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = candidate.RawTransaction ?? await FindRawTransactionAsync(unitOfWork, candidate, logger);
            var clearing = await SumTransactionClearingAsync(unitOfWork, candidate, raw, logger, cancellationToken);
            if (clearing == 0)
                continue;

            holding++;
            total = checked(total + clearing);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Accounting reconcile: transaction {TxId} (channel {ChannelId}) is in flight and "
                              + "holds {ClearingMsat} msat of clearing", candidate.TxId, candidate.ChannelId,
                                clearing);
        }

        return new ClearingOutstanding(total, holding, candidates.Count);
    }

    private static async Task<Dictionary<TxId, Candidate>> CollectCandidatesAsync(
        IUnitOfWork unitOfWork, AccountingSnapshot snapshot, ILogger logger)
    {
        var candidates = new Dictionary<TxId, Candidate>();

        void Add(TxId txId, ChannelId? channelId, byte[]? raw)
        {
            if (candidates.TryGetValue(txId, out var known))
                candidates[txId] = known with
                {
                    ChannelId = known.ChannelId ?? channelId,
                    RawTransaction = known.RawTransaction ?? raw
                };
            else
                candidates[txId] = new Candidate(txId, channelId, raw);
        }

        foreach (var broadcast in await unitOfWork.BroadcastTransactionDbRepository.GetPendingAsync())
            Add(broadcast.TransactionId, broadcast.ChannelId, broadcast.RawTransaction);

        foreach (var watch in await unitOfWork.WatchedTransactionDbRepository.GetAllPendingAsync())
            Add(watch.TransactionId, watch.ChannelId, null);

        foreach (var bucket in snapshot.Channels)
        {
            if (!bucket.IsLoaded || bucket.State is ChannelState.Closed or ChannelState.Stale
                                                   or ChannelState.OnchainResolving)
                continue;

            try
            {
                var fundings = await unitOfWork.ChannelFundingDbRepository.GetFundingSetAsync(bucket.ChannelId);
                foreach (var pending in fundings?.Pending ?? [])
                    Add(pending.FundingTxId, bucket.ChannelId, null);
            }
            catch (NotSupportedException)
            {
                // A unit of work without channel fundings (test doubles): no splice is pending
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Accounting reconcile: cannot read the fundings of channel {ChannelId}",
                                bucket.ChannelId);
            }
        }

        return candidates;
    }

    /// <summary>The bytes of an in-flight transaction found by txid: its broadcast row (a funding or splice that left
    /// the pending state), else the closing transaction of its channel (a mutual close has no broadcast row).</summary>
    private static async Task<byte[]?> FindRawTransactionAsync(IUnitOfWork unitOfWork, Candidate candidate,
                                                               ILogger logger)
    {
        if (await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(candidate.TxId) is { } row)
            return row.RawTransaction;

        if (candidate.ChannelId is not { } channelId)
            return null;

        try
        {
            var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
            return channel?.ClosingTransaction is { } closing && closing.TxId == candidate.TxId
                       ? closing.RawTxBytes
                       : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "Accounting reconcile: cannot read channel {ChannelId} for transaction {TxId}",
                            channelId, candidate.TxId);
            return null;
        }
    }

    private static async Task<long> SumTransactionClearingAsync(IUnitOfWork unitOfWork, Candidate candidate,
                                                                byte[]? raw, ILogger logger,
                                                                CancellationToken cancellationToken)
    {
        var events = unitOfWork.AccountingEventDbRepository;
        var books = unitOfWork.AccountingBooksDbRepository;
        long sum = 0;

        if (raw is not null && TryLoad(raw, candidate.TxId, logger) is { } transaction)
        {
            var spentBy = candidate.TxId.ToString();
            foreach (var input in transaction.Inputs)
                sum += await SumFactClearingAsync(events, books,
                                                  AccountingEventKeys.WalletOutputSpent(
                                                      new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N),
                                                  e => !e.Details.TryGetValue(SpentByDetail, out var by)
                                                    || string.Equals(by, spentBy, StringComparison.Ordinal),
                                                  cancellationToken);

            for (var i = 0; i < transaction.Outputs.Count; i++)
                sum += await SumFactClearingAsync(events, books,
                                                  AccountingEventKeys.WalletReceived(candidate.TxId, (uint)i), null,
                                                  cancellationToken);
        }

        if (candidate.ChannelId is { } channelId)
        {
            sum += await SumFactClearingAsync(events, books,
                                              AccountingEventKeys.ChannelFunded(channelId, candidate.TxId), null,
                                              cancellationToken);
            sum += await SumFactClearingAsync(events, books,
                                              AccountingEventKeys.SpliceLocked(channelId, candidate.TxId), null,
                                              cancellationToken);
            sum += await SumFactClearingAsync(events, books,
                                              AccountingEventKeys.ChannelClosedMutual(channelId, candidate.TxId), null,
                                              cancellationToken);
        }

        sum += await SumFactClearingAsync(events, books, AccountingEventKeys.WalletSent(candidate.TxId), null,
                                          cancellationToken);
        sum += await SumFactClearingAsync(events, books, AccountingEventKeys.AnchorCpfpFee(candidate.TxId), null,
                                          cancellationToken);
        return sum;
    }

    /// <summary>
    /// The clearing postings of one fact: every confirmation generation (<paramref name="baseKey"/>, then
    /// <see cref="AccountingEventKeys.Reconfirmed"/> 2, 3, ...) that <paramref name="accept"/> takes, with its reversal.
    /// </summary>
    private static async Task<long> SumFactClearingAsync(
        IAccountingEventDbRepository events, IAccountingBooksDbRepository books,
        string baseKey, Func<AccountingEventModel, bool>? accept, CancellationToken cancellationToken)
    {
        long sum = 0;
        for (var generation = 1; generation <= MaxGenerations; generation++)
        {
            var key = generation == 1 ? baseKey : AccountingEventKeys.Reconfirmed(baseKey, generation);
            var accountingEvent = await events.GetByKeyAsync(key, cancellationToken);
            if (accountingEvent is null)
                break;

            if (accept is not null && !accept(accountingEvent))
                continue;

            sum += await ClearingOfAsync(books, key, cancellationToken);
            if (accountingEvent.BlockHeight is { } height)
                sum += await ClearingOfAsync(books, AccountingEventKeys.Reversal(key, height), cancellationToken);
        }

        return sum;
    }

    private static async Task<long> ClearingOfAsync(IAccountingBooksDbRepository books, string eventKey,
                                                    CancellationToken cancellationToken) =>
        (await books.GetEntryByKeyAsync(eventKey, cancellationToken))?.Postings
                                                                      .Where(p => p.Account == AccountRole.Clearing)
                                                                      .Sum(p => p.AmountMsat) ?? 0;

    private static Transaction? TryLoad(byte[] raw, TxId txId, ILogger logger)
    {
        try
        {
            return Transaction.Load(raw, Network.Main);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException
                                      or InvalidOperationException)
        {
            logger.LogDebug(e, "Accounting reconcile: transaction {TxId} does not parse; only its channel side is read",
                            txId);
            return null;
        }
    }

    private sealed record Candidate(TxId TxId, ChannelId? ChannelId, byte[]? RawTransaction);
}