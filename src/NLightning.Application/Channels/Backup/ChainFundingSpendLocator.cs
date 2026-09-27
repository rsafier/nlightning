using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Models;

/// <summary>
/// <see cref="IFundingSpendLocator"/> over bitcoind: <c>gettxout</c> (without the mempool) tells whether the funding
/// output is spent; a spent one is searched from the tip downwards, <see cref="ChannelBackupOptions.RestoreSpendSearchBatchSize"/>
/// blocks at a time, down to the funding block (the short channel id's height) or
/// <see cref="ChannelBackupOptions.RestoreSpendSearchDepth"/> blocks, whichever is higher. The spend is most likely
/// recent (the peer closed after our node lost its data), so the search is cheap in the usual case; an older one is
/// found by <see cref="RescanAsync"/>, which goes on down to the funding block (NL-430).
/// </summary>
/// <remarks>
/// A backup entry without a short channel id (the funding had not confirmed when the backup was written) uses its
/// funding height as the floor instead: the funding transaction, if it confirmed, is in a block at or above it, and
/// the search stops at the block that holds it. No short channel id and no funding height: nothing can be told.
/// </remarks>
public sealed class ChainFundingSpendLocator : IFundingSpendLocator
{
    /// <summary>How many blocks a background search reads between two progress logs.</summary>
    internal const uint ProgressLogBlocks = 1000;

    private readonly IBitcoinChainService _chain;
    private readonly ChannelBackupOptions _options;
    private readonly ILogger<ChainFundingSpendLocator> _logger;

    public ChainFundingSpendLocator(IBitcoinChainService chain, IOptions<ChannelBackupOptions>? options = null,
                                    ILogger<ChainFundingSpendLocator>? logger = null)
    {
        _chain = chain;
        _options = options?.Value ?? new ChannelBackupOptions();
        _logger = logger ?? NullLogger<ChainFundingSpendLocator>.Instance;
    }

    /// <inheritdoc />
    public Task<FundingSpendLocation> LocateAsync(ChannelBackupEntry entry, CancellationToken cancellationToken) =>
        SearchAsync(entry, null, cancellationToken);

    /// <inheritdoc />
    public Task<FundingSpendLocation> RescanAsync(ChannelBackupEntry entry, uint belowHeight,
                                                  CancellationToken cancellationToken) =>
        SearchAsync(entry, belowHeight, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A pending splice of the backup or our next funding keys against the peer's known keys recognize the output at
    /// once (<see cref="SpliceSpendFollower.TryIdentifyNextFunding"/>); otherwise each spent P2WSH output of the splice
    /// is searched for its spend in the recent window (<see cref="ChannelBackupOptions.RestoreSpendSearchDepth"/>) and
    /// the 2-of-2 script its witness reveals names our key and the peer's new one.
    /// </remarks>
    public async Task<ChannelBackupEntry?> FollowSpliceAsync(ChannelBackupEntry entry, OutpointSpentEventArgs spend,
                                                             Func<uint, CompactPubKey?> deriveLocalFundingKey,
                                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(deriveLocalFundingKey);

        Transaction transaction;
        try
        {
            transaction = Transaction.Load(spend.SpendingTransaction.RawTxBytes, Network.Main);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The spend of the funding output of channel {ChannelId} can't be read",
                               entry.ChannelId);
            return null;
        }

        if (SpliceSpendFollower.IsCommitment(transaction))
            return null;

        if (SpliceSpendFollower.TryIdentifyNextFunding(entry, transaction, spend.BlockHeight, spend.TransactionIndex,
                                                       deriveLocalFundingKey) is { } next)
            return next;

        // The peer rotated its key too: the witness of the new funding output's spend shows both keys
        var txId = new TxId(transaction.GetHash().ToBytes());
        for (var vout = 0; vout < transaction.Outputs.Count && vout <= ushort.MaxValue; vout++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = transaction.Outputs[vout];
            if (!output.ScriptPubKey.IsScriptType(ScriptType.P2WSH))
                continue;

            var candidate = SpliceSpendFollower.MoveTo(entry, txId, (ushort)vout, (ulong)output.Value.Satoshi,
                                                       entry.LocalFundingKeyIndex, entry.LocalFundingPubKey,
                                                       entry.RemoteFundingPubKey, spend.BlockHeight,
                                                       spend.TransactionIndex);
            var location = await SearchAsync(candidate, null, cancellationToken);
            if (location is not { Status: FundingSpendStatus.SpentFound, Spend: { } outputSpend })
                continue;

            try
            {
                var spender = Transaction.Load(outputSpend.SpendingTransaction.RawTxBytes, Network.Main);
                var outPoint = new OutPoint(transaction.GetHash(), vout);
                var input = spender.Inputs.FirstOrDefault(i => i.PrevOut == outPoint);
                if (input is not null
                 && SpliceSpendFollower.TryParseFundingWitness(entry, input.WitScript, deriveLocalFundingKey) is
                 { } keys)
                    return candidate with
                    {
                        LocalFundingKeyIndex = keys.Index,
                        LocalFundingPubKey = keys.Local,
                        RemoteFundingPubKey = keys.Remote
                    };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "The spend of output {Vout} of splice {TxId} of channel {ChannelId} can't be read",
                                   vout, txId, entry.ChannelId);
            }
        }

        return null;
    }

    /// <summary>
    /// The lowest block the funding spend can be in: the short channel id's block, else the funding height recorded
    /// in the backup (null when neither is known).
    /// </summary>
    internal static uint? GetFloorHeight(ChannelBackupEntry entry) =>
        entry.ShortChannelId is { } shortChannelId
            ? shortChannelId.BlockHeight
            : entry.FundingHeight > 0
                ? entry.FundingHeight
                : null;

    /// <param name="entry">The backed-up channel.</param>
    /// <param name="belowHeight">Null: the recent window from the tip; else the blocks below it down to the floor.
    /// </param>
    /// <param name="cancellationToken">Stops the search.</param>
    private async Task<FundingSpendLocation> SearchAsync(ChannelBackupEntry entry, uint? belowHeight,
                                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Without a short channel id or a funding height the funding may never have confirmed: a missing output tells
        // nothing
        if (GetFloorHeight(entry) is not { } floor)
            return new FundingSpendLocation(FundingSpendStatus.NotConfirmed);

        var confirmed = entry.ShortChannelId is not null;
        var outPoint = new OutPoint(new uint256((byte[])entry.FundingTxId), entry.FundingOutputIndex);
        try
        {
            if (await _chain.GetConfirmedUnspentOutputAsync(outPoint) is not null)
                return new FundingSpendLocation(FundingSpendStatus.Unspent);

            var tip = await _chain.GetCurrentBlockHeightAsync();
            uint highest, lowest;
            if (belowHeight is { } below)
            {
                if (below <= floor || below == 0)
                    return NotFound(confirmed, floor, floor);

                highest = Math.Min(below - 1, tip);
                lowest = floor;
            }
            else
            {
                var depth = Math.Max(1u, _options.RestoreSpendSearchDepth);
                highest = tip;
                lowest = Math.Max(floor, tip >= depth ? tip - depth + 1 : 0);
            }

            if (highest < lowest)
                return NotFound(confirmed, lowest, floor);

            var batchSize = Math.Max(1u, _options.RestoreSpendSearchBatchSize);
            // Only a funding without a short channel id needs its own block found: with one, the floor is that block
            var fundingTxId = confirmed ? null : new uint256((byte[])entry.FundingTxId);
            var isRescan = belowHeight is not null;
            var nextProgressAt = highest >= ProgressLogBlocks ? highest - ProgressLogBlocks : 0;
            for (var top = (long)highest; top >= lowest; top -= batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bottom = Math.Max(lowest, top - batchSize + 1);
                var heights = new List<uint>();
                for (var height = top; height >= bottom; height--)
                    heights.Add((uint)height);

                // Read the batch at once, look at it from the top down: the spend is above the funding
                var blocks = await Task.WhenAll(heights.Select(ReadBlockAsync));
                for (var i = 0; i < heights.Count; i++)
                {
                    var (block, pruned) = blocks[i];
                    if (pruned)
                    {
                        _logger.LogError("Block {Height} is pruned on the bitcoin node: the spend of the funding output "
                                       + "{Outpoint} of channel {ChannelId} can't be searched below {Searched}",
                                         heights[i], outPoint, entry.ChannelId, heights[i] + 1);
                        return new FundingSpendLocation(FundingSpendStatus.BlocksPruned,
                                                        SearchedFromHeight: heights[i] + 1, FloorHeight: floor,
                                                        PrunedHeight: heights[i]);
                    }

                    if (block is null)
                        continue;

                    if (FindSpend(entry, outPoint, block, heights[i]) is { } spend)
                        return new FundingSpendLocation(FundingSpendStatus.SpentFound, spend);

                    // The funding's own block: the spend is not below it
                    if (fundingTxId is not null && block.Transactions.Any(t => t.GetHash() == fundingTxId))
                        return new FundingSpendLocation(FundingSpendStatus.SpentNotFound,
                                                        SearchedFromHeight: heights[i], FloorHeight: heights[i]);
                }

                if (isRescan && bottom <= nextProgressAt && bottom > lowest)
                {
                    _logger.LogInformation("Searching for the spend of the funding output {Outpoint} of channel "
                                         + "{ChannelId}: blocks {Highest} down to {Bottom} read, {Left} left",
                                           outPoint, entry.ChannelId, highest, bottom, bottom - lowest);
                    nextProgressAt = bottom >= ProgressLogBlocks ? (uint)bottom - ProgressLogBlocks : 0;
                }
            }

            return NotFound(confirmed, lowest, floor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not check whether the funding output {Outpoint} of channel {ChannelId} is spent",
                             outPoint, entry.ChannelId);
            return new FundingSpendLocation(FundingSpendStatus.ChainUnavailable, Error: e.Message);
        }
    }

    /// <summary>
    /// Nothing found down to <paramref name="searchedFrom"/>. A funding without a short channel id whose own block
    /// was not met either (the whole range searched) never confirmed at or above its funding height.
    /// </summary>
    private static FundingSpendLocation NotFound(bool confirmed, uint searchedFrom, uint floor) =>
        !confirmed && searchedFrom <= floor
            ? new FundingSpendLocation(FundingSpendStatus.NotConfirmed, SearchedFromHeight: searchedFrom,
                                       FloorHeight: floor)
            : new FundingSpendLocation(FundingSpendStatus.SpentNotFound, SearchedFromHeight: searchedFrom,
                                       FloorHeight: floor);

    /// <summary>
    /// One block; <c>Pruned</c> when the bitcoin node no longer has it (any other read error propagates).
    /// </summary>
    private async Task<(Block? Block, bool Pruned)> ReadBlockAsync(uint height)
    {
        try
        {
            return (await _chain.GetBlockAsync(height), false);
        }
        catch (Exception e) when (IsPrunedBlockError(e))
        {
            return (null, true);
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is bitcoind's answer for a block it pruned (<c>getblock</c>: "Block not
    /// available (pruned data)").
    /// </summary>
    internal static bool IsPrunedBlockError(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e.Message.Contains("pruned", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static OutpointSpentEventArgs? FindSpend(ChannelBackupEntry entry, OutPoint outPoint, Block block,
                                                     uint height)
    {
        for (var index = 0; index < block.Transactions.Count; index++)
        {
            var transaction = block.Transactions[index];
            if (transaction.IsCoinBase || transaction.Inputs.All(i => i.PrevOut != outPoint))
                continue;

            return new OutpointSpentEventArgs(entry.ChannelId,
                                              new SignedTransaction(new TxId(transaction.GetHash().ToBytes()),
                                                                    transaction.ToBytes()),
                                              height, (uint)index, entry.FundingTxId, entry.FundingOutputIndex,
                                              new Hash(block.GetHash().ToBytes()));
        }

        return null;
    }
}