using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// NL-292 (BOLT 5 plan O6-T3): a funding transaction whose confirming block was disconnected is watched again by the
/// chain monitor, which raises its confirmation again from its position on the new branch. The channel manager handles
/// only the first confirmation of a channel, so this moves a channel's short channel id (and funding height) to the new
/// position: under the channel's lock, staged on the database's copy and saved, then applied to the shared model.
/// </summary>
/// <remarks>
/// The first confirmation (no short channel id yet) is the channel manager's; a confirmation at the recorded position
/// changes nothing. A channel that is not loaded is left alone (its funding confirmation is replayed at startup).
/// </remarks>
public sealed class FundingReconfirmationHandler
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public FundingReconfirmationHandler(IChannelLockProvider channelLockProvider,
                                        IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                                        IServiceScopeFactory serviceScopeFactory)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>
    /// Moves the short channel id of the channel whose funding transaction <paramref name="watch"/> confirmed at another
    /// position than recorded. Returns true when it changed it.
    /// </summary>
    public async Task<bool> HandleAsync(WatchedTransactionModel watch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        if (watch is not { FirstSeenAtHeight: { } height, TransactionIndex: { } index })
            return false;

        try
        {
            using (await _channelLockProvider.AcquireAsync(watch.ChannelId, cancellationToken))
            {
                if (!_channelMemoryRepository.TryGetChannel(watch.ChannelId, out var channel)
                 || channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } outputIndex }
                 || fundingTxId != watch.TransactionId
                 || channel.ShortChannelId.BlockHeight == 0)
                    return false;

                var moved = new ShortChannelId(height, index, outputIndex);
                var previous = channel.ShortChannelId;
                if (previous.Equals(moved))
                    return false;

                using var scope = _serviceScopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(watch.ChannelId);
                if (stored is null)
                    return false;

                stored.ShortChannelId = moved;
                stored.FundingCreatedAtBlockHeight = height;
                await unitOfWork.ChannelDbRepository.UpdateAsync(stored);
                await unitOfWork.SaveChangesAsync();

                channel.ShortChannelId = moved;
                channel.FundingCreatedAtBlockHeight = height;
                _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} confirmed again at {Scid} "
                                 + "after a reorg (was {Previous}); short channel id updated",
                                   new uint256(fundingTxId), watch.ChannelId, moved, previous);
                return true;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Moving the short channel id of channel {ChannelId} after a reorg failed",
                             watch.ChannelId);
            return false;
        }
    }
}