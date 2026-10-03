using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Onchain;

using Domain.Channels.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The spend watch of a splice's new funding output (splicing plan §3.6, SP2-C): staged in the save that precedes our
/// splice <c>commitment_signed</c> (from then on the peer holds our signature of a commitment on it, and once our
/// <c>tx_signatures</c> follow it can publish the splice), so a commitment spending a pending splice reaches the
/// on-chain watcher before the lock and across a restart (the chain monitor tracks every stored watch at startup).
/// </summary>
internal static class SpliceFundingWatch
{
    /// <summary>
    /// Stages a <see cref="WatchedOutpointPurpose.FundingOutput"/> watch of <paramref name="funding"/>'s outpoint on
    /// <paramref name="unitOfWork"/> when there is none; returns it, or null (already watched, or a unit of work
    /// without watches).
    /// </summary>
    public static async Task<WatchedOutpointModel?> StageAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                               ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(funding);
        try
        {
            if (unitOfWork.WatchedOutpointDbRepository is not { } watches
             || await watches.GetAsync(funding.FundingTxId, funding.OutputIndex) is not null)
                return null;

            var watch = new WatchedOutpointModel(funding.FundingTxId, funding.OutputIndex, channelId,
                                                 WatchedOutpointPurpose.FundingOutput);
            watches.Add(watch);
            return watch;
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return null;
        }
    }

    /// <summary>
    /// After the caller's save (it holds the channel's lock while it saves, so this waits for the lock): tracks the
    /// watch in the chain monitor when its row was stored; nothing when the save failed.
    /// </summary>
    public static async Task TrackAfterSaveAsync(IServiceProvider serviceProvider,
                                                 IChannelLockProvider channelLockProvider,
                                                 WatchedOutpointModel watch, ILogger logger)
    {
        try
        {
            await Task.Yield();
            using var channelLock = await channelLockProvider.AcquireAsync(watch.ChannelId);
            using var scope = serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(watch.TransactionId, watch.OutputIndex) is null)
                return;

            (serviceProvider.GetService<IBlockchainMonitor>() ?? serviceProvider.GetService<IOutpointWatcher>())
              ?.TrackWatchedOutpoint(watch);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The funding output watch of splice {TxId} of channel {ChannelId} is tracked only "
                               + "after a restart", watch.TransactionId, watch.ChannelId);
        }
    }
}