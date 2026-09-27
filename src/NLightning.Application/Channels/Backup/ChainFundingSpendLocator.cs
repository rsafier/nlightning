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
            var fundingTxId = new uint256((byte[])entry.FundingTxId);
            for (var top = (long)highest; top >= lowest; top -= batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bottom = Math.Max(lowest, top - batchSize + 1);
                var heights = new List<uint>();
                for (var height = top; height >= bottom; height--)
                    heights.Add((uint)height);

                // Read the batch at once, look at it from the top down: the spend is above the funding
                var blocks = await Task.WhenAll(heights.Select(h => _chain.GetBlockAsync(h)));
                for (var i = 0; i < heights.Count; i++)
                {
                    if (blocks[i] is not { } block)
                        continue;

                    if (FindSpend(entry, outPoint, block, heights[i]) is { } spend)
                        return new FundingSpendLocation(FundingSpendStatus.SpentFound, spend);

                    // The funding's own block: the spend is not below it
                    if (block.Transactions.Any(t => t.GetHash() == fundingTxId))
                        return new FundingSpendLocation(FundingSpendStatus.SpentNotFound,
                                                        SearchedFromHeight: heights[i], FloorHeight: heights[i]);
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