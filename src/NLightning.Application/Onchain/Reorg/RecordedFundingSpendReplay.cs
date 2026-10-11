using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// NL-493 (splicing plan SP2-C-T4): after a restart, the funding spends the chain monitor recorded on a funding watch
/// (<c>WatchedOutpoints</c>, purpose <c>FundingOutput</c>) are handed to the on-chain watcher again, once per process.
/// The monitor records the spend in its block save and raises the event after it; a crash between that save and the
/// watcher's own save (the close's rows, or the retirement of a close reorged out for a discarded splice) would
/// otherwise leave the spend unhandled: the monitor never raises a recorded spend again.
/// </summary>
/// <remarks>
/// <para>Replayed: a spend by a transaction that is not a funding of the channel (a commitment, a mutual close, an
/// unknown spend) and is not the recorded close's; and, for a <see cref="ChannelState.Failed"/> or
/// <see cref="ChannelState.OnchainResolving"/> channel, a spend by one of its pending or discarded splices (the watcher
/// retires a close reorged out for it, or hands the splice to the commitment broadcaster). A spend by the current or a
/// replaced funding is the normal splice path (the lock moved the channel) and is skipped. The watcher is idempotent, so
/// a spend it had handled changes nothing.</para>
/// <para>The spending transaction is read from its block through <see cref="IBitcoinChainService"/>. When a block cannot
/// be read (an error, or a pruned block) or the watcher throws, the spends not handed over yet are tried again in the
/// next round, up to <see cref="MaxRounds"/> rounds, after which the replay gives up with a CRITICAL log. A spend no
/// longer in the recorded block (a stale record) is skipped with a warning.</para>
/// </remarks>
internal sealed class RecordedFundingSpendReplay
{
    /// <summary>The rounds a replay that cannot complete is tried before it gives up (about a day of blocks).</summary>
    internal const int MaxRounds = 144;

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly HashSet<(ChannelId, TxId, uint, TxId)> _handed = [];
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private bool _done;
    private int _failedRounds;

    public RecordedFundingSpendReplay(IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                                      IServiceScopeFactory serviceScopeFactory)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>Whether the replay ran to the end in this process.</summary>
    public bool IsDone => _done;

    /// <summary>Hands every recorded, possibly unhandled funding spend to the watcher (once per process); returns how
    /// many were handed over.</summary>
    public async Task<int> ReplayAsync(CancellationToken cancellationToken)
    {
        if (_done)
            return 0;

        var channels = _channelMemoryRepository.FindChannels(
            c => c.State is not (ChannelState.Closed or ChannelState.Stale)
              && c.FundingOutput?.TransactionId is not null);
        if (channels is not { Count: > 0 })
        {
            _done = true;
            return 0;
        }

        var spends = new List<(ChannelId ChannelId, TxId FundingTxId, uint Vout, TxId SpentBy, uint Height)>();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A unit of work per channel: one for every channel tracked each row read so far, and each funding read
            // went over all of them, so the first round after a start was quadratic in the channel count (NL-1359)
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (unitOfWork.WatchedOutpointDbRepository is not { } watches)
            {
                _done = true;
                return 0;
            }

            var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
            ChannelCloseModel? close = null;
            var closeRead = false;
            foreach (var funding in fundings)
            {
                var watch = await watches.GetAsync(funding.FundingTxId, funding.OutputIndex);
                if (watch is not { SpentByTransactionId: { } spentBy, SpentAtHeight: { } height })
                    continue;

                // The close is read only for a channel with a recorded spend (most channels have none)
                if (!closeRead)
                {
                    close = unitOfWork.OnchainResolutionDbRepository is { } resolutions
                                ? await resolutions.GetCloseAsync(channel.ChannelId)
                                : null;
                    closeRead = true;
                }

                if (close?.CommitmentTransactionId == spentBy)
                    continue;

                var spender = fundings.FirstOrDefault(f => f.FundingTxId == spentBy);
                if (spender is not null
                 && (spender.Status is ChannelFundingStatus.Current or ChannelFundingStatus.Replaced
                  || channel.State is not (ChannelState.Failed or ChannelState.OnchainResolving)))
                    continue;

                if (!_handed.Contains((channel.ChannelId, funding.FundingTxId, funding.OutputIndex, spentBy)))
                    spends.Add((channel.ChannelId, funding.FundingTxId, funding.OutputIndex, spentBy, height));
            }
        }

        if (spends.Count == 0)
        {
            _done = true;
            return 0;
        }

        using var chainScope = _serviceScopeFactory.CreateScope();
        if (chainScope.ServiceProvider.GetService<IBitcoinChainService>() is not { } chain
         || chainScope.ServiceProvider.GetService<IOnchainChannelWatcher>() is not { } watcher)
        {
            _done = true;
            return 0;
        }

        var handed = 0;
        var complete = true;
        foreach (var (channelId, fundingTxId, vout, spentBy, height) in spends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Block? block;
            try
            {
                block = await chain.GetBlockAsync(height);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not read block {Height} to replay the recorded spend of funding "
                                    + "{FundingTxId} of channel {ChannelId}; retrying in the next round", height,
                                   Display(fundingTxId), channelId);
                complete = false;
                continue;
            }

            if (block is null)
            {
                _logger.LogWarning("Block {Height} is not available (pruned?) to replay the recorded spend {TxId} of "
                                 + "funding {FundingTxId} of channel {ChannelId}; retrying in the next round", height,
                                   Display(spentBy), Display(fundingTxId), channelId);
                complete = false;
                continue;
            }

            var index = block.Transactions.FindIndex(t => new TxId(t.GetHash().ToBytes()) == spentBy);
            if (index < 0)
            {
                // The monitor's record is stale (its reorg rollback clears it): nothing to replay
                _logger.LogWarning("The recorded spend {TxId} of funding {FundingTxId} of channel {ChannelId} is not in "
                                 + "block {Height}", Display(spentBy), Display(fundingTxId),
                                   channelId, height);
                continue;
            }

            var transaction = block.Transactions[index];
            _logger.LogInformation("Replaying the recorded spend {TxId} of funding {FundingTxId} of channel {ChannelId} "
                                 + "at height {Height}", Display(spentBy), Display(fundingTxId),
                                   channelId, height);
            try
            {
                await watcher.HandleFundingSpentAsync(
                    new OutpointSpentEventArgs(channelId,
                                               new SignedTransaction(spentBy, transaction.ToBytes()),
                                               height, (uint)index, fundingTxId, vout,
                                               new Hash(block.GetHash().ToBytes())), cancellationToken);
                _handed.Add((channelId, fundingTxId, vout, spentBy));
                handed++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Replaying the recorded spend {TxId} of funding {FundingTxId} of channel "
                                  + "{ChannelId} failed; retrying in the next round", Display(spentBy),
                                 Display(fundingTxId), channelId);
                complete = false;
            }
        }

        if (complete)
        {
            _done = true;
            return handed;
        }

        if (++_failedRounds >= MaxRounds)
        {
            _logger.LogCritical("Gave up replaying the recorded funding spends after {Rounds} rounds: a funding spend "
                              + "recorded before a crash may be unhandled; restart the node once the blocks and the "
                              + "watcher are available again (NL-493)", _failedRounds);
            _done = true;
        }

        return handed;
    }

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}