using Microsoft.Extensions.Logging;

namespace NLightning.Application.LiquidityAds;

using Domain.Channels.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// The leases of liquidity purchases (liquidity ads decision D-L4, NL-850): the cooperative close guard of a channel we
/// sold liquidity on, and the purchases of a channel marked closed in the save that makes the channel Closed.
/// </summary>
/// <remarks>
/// A unit of work that stores no purchases (a test double: the interface default throws
/// <see cref="NotSupportedException"/>, a mock answers null) has no lease and nothing to mark.
/// </remarks>
public static class LiquidityLeases
{
    /// <summary>
    /// The newest sale on <paramref name="channelId"/> whose lease binds us at <paramref name="currentHeight"/>
    /// (pending, or active before its end), or null.
    /// </summary>
    public static async Task<LiquidityPurchaseModel?> GetActiveSaleLeaseAsync(IUnitOfWork unitOfWork,
                                                                             ChannelId channelId,
                                                                             uint currentHeight)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var repository = TryGetRepository(unitOfWork);
        return repository is null ? null : await repository.GetActiveSaleLeaseAsync(channelId, currentHeight);
    }

    /// <summary>
    /// Why our cooperative close of the channel is refused while <paramref name="lease"/> is in force.
    /// </summary>
    public static string DescribeCloseRefusal(ChannelId channelId, LiquidityPurchaseModel lease, uint currentHeight)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var until = lease.LeaseEndHeight is { } end
                        ? $"until block {end} ({end - Math.Min(end, currentHeight)} blocks left)"
                        : $"for {lease.LeaseBlocks} blocks once its funding confirms";
        return $"Channel {channelId} carries {lease.ContributedSat} sat of inbound liquidity we sold to "
             + $"{lease.PeerNodeId}, leased {until}; closing it now breaks the lease. Use closechannel --force to "
             + "close it anyway (forceclosechannel is never refused)";
    }

    /// <summary>
    /// Stages the channel's pending and active purchases as closed at <paramref name="height"/>
    /// (<see cref="LiquidityPurchaseModel.MarkClosed"/>, which notes a close inside the lease), in the caller's save.
    /// Never throws: a failure is logged and the channel closes anyway.
    /// </summary>
    public static async Task StageChannelClosedAsync(IUnitOfWork unitOfWork, ChannelId channelId, uint height,
                                                     ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var repository = TryGetRepository(unitOfWork);
            if (repository is null)
                return;

            var purchases = await repository.GetByChannelIdAsync(channelId);
            foreach (var purchase in purchases ?? [])
            {
                if (purchase.Status is not (LiquidityPurchaseStatus.Pending or LiquidityPurchaseStatus.Active))
                    continue;

                purchase.MarkClosed(height);
                repository.Update(purchase);
                if (purchase.ClosedEarly)
                    logger.LogWarning("The liquidity {Role} {Id} of channel {ChannelId} with {Peer} closed at height "
                                    + "{Height}, before its lease ended", purchase.Role, purchase.Id, channelId,
                                      purchase.PeerNodeId, height);
                else
                    logger.LogInformation("The liquidity {Role} {Id} of channel {ChannelId} closed at height {Height}",
                                          purchase.Role, purchase.Id, channelId, height);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not mark the liquidity purchases of channel {ChannelId} closed", channelId);
        }
    }

    private static ILiquidityPurchaseDbRepository? TryGetRepository(IUnitOfWork unitOfWork)
    {
        try
        {
            return unitOfWork.LiquidityPurchaseDbRepository;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}