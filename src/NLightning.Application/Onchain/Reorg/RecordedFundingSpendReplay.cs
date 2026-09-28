using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
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
/// <para>The spending transaction is read from its block through <see cref="IBitcoinChainService"/>; without one, or
/// when the block cannot be read, the replay is tried again in the next round.</para>
/// </remarks>
internal sealed class RecordedFundingSpendReplay
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private bool _done;

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

        var spends = new List<(Domain.Channels.ValueObjects.ChannelId ChannelId, TxId FundingTxId, uint Vout, TxId
            SpentBy, uint Height)>();
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (unitOfWork.WatchedOutpointDbRepository is not { } watches)
            {
                _done = true;
                return 0;
            }

            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
                var close = unitOfWork.OnchainResolutionDbRepository is { } resolutions
                                ? await resolutions.GetCloseAsync(channel.ChannelId)
                                : null;
                foreach (var funding in fundings)
                {
                    var watch = await watches.GetAsync(funding.FundingTxId, funding.OutputIndex);
                    if (watch is not { SpentByTransactionId: { } spentBy, SpentAtHeight: { } height }
                     || close?.CommitmentTransactionId == spentBy)
                        continue;

                    var spender = fundings.FirstOrDefault(f => f.FundingTxId == spentBy);
                    if (spender is not null
                     && (spender.Status is ChannelFundingStatus.Current or ChannelFundingStatus.Replaced
                      || channel.State is not (ChannelState.Failed or ChannelState.OnchainResolving)))
                        continue;

                    spends.Add((channel.ChannelId, funding.FundingTxId, funding.OutputIndex, spentBy, height));
                }
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
                return handed;
            }

            if (block is null)
                return handed;

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
                handed++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Replaying the recorded spend {TxId} of funding {FundingTxId} of channel "
                                  + "{ChannelId} failed", Display(spentBy), Display(fundingTxId),
                                 channelId);
            }
        }

        _done = true;
        return handed;
    }

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}