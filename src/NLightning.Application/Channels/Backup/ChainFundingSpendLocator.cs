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
/// output is spent; a spent one is searched from the tip downwards, block by block, down to the funding block (the
/// short channel id's height) or <see cref="ChannelBackupOptions.RestoreSpendSearchDepth"/> blocks, whichever is
/// higher. The spend is most likely recent (the peer closed after our node lost its data), so the search is cheap in
/// the usual case.
/// </summary>
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
    public async Task<FundingSpendLocation> LocateAsync(ChannelBackupEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Without a short channel id the funding may never have confirmed: a missing output tells nothing
        if (entry.ShortChannelId is not { } shortChannelId)
            return new FundingSpendLocation(FundingSpendStatus.NotConfirmed);

        var outPoint = new OutPoint(new uint256((byte[])entry.FundingTxId), entry.FundingOutputIndex);
        try
        {
            if (await _chain.GetConfirmedUnspentOutputAsync(outPoint) is not null)
                return new FundingSpendLocation(FundingSpendStatus.Unspent);

            var tip = await _chain.GetCurrentBlockHeightAsync();
            var depth = Math.Max(1u, _options.RestoreSpendSearchDepth);
            var lowest = Math.Max(shortChannelId.BlockHeight, tip >= depth ? tip - depth + 1 : 0);
            for (var height = tip; height >= lowest; height--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _chain.GetBlockAsync(height) is { } block
                 && FindSpend(entry, outPoint, block, height) is { } spend)
                    return new FundingSpendLocation(FundingSpendStatus.SpentFound, spend);

                if (height == 0)
                    break;
            }

            return new FundingSpendLocation(FundingSpendStatus.SpentNotFound, SearchedFromHeight: lowest);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not check whether the funding output {Outpoint} of channel {ChannelId} is spent",
                             outPoint, entry.ChannelId);
            return new FundingSpendLocation(FundingSpendStatus.ChainUnavailable, Error: e.Message);
        }
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