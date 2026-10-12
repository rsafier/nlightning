using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.DualFunding;

using Accounting;
using Domain.Accounting.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.Persistence.Interfaces;

/// <summary>
/// The liquidity purchases of a dual-funded open at its funding confirmation (liquidity ads, NL-850): the confirmed
/// attempt's purchase starts its lease (<c>MarkActive</c>), the other attempts' pending purchases are replaced, and the
/// fee is booked (<c>LiquidityFeePaid</c>/<c>LiquidityFeeEarned</c>) in the same save as the channel's
/// <c>ChannelFunded</c>, with the same fee, so the reconcile never sees the channels account drift.
/// </summary>
internal static class DualFundLiquidityAccounting
{
    /// <summary>
    /// Stages, on <paramref name="unitOfWork"/>, the purchase updates and the fee event of the attempt the channel's
    /// funding outpoint names (it reached its depth at <paramref name="height"/>); returns the fee from our side in msat
    /// (+ we paid, − we earned) for <c>ChannelFunded</c>'s <c>liquidityFeeMsat</c>, 0 when the attempt bought nothing.
    /// Nothing for a v1 channel. Never throws.
    /// </summary>
    public static async Task<long> StageFundingConfirmedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                              uint height, ShortChannelId? shortChannelId,
                                                              DateTimeOffset occurredAt, ILogger logger)
    {
        if (channel.Version != ChannelVersion.V2 || channel.FundingOutput?.TransactionId is not { } fundingTxId)
            return 0;

        try
        {
            if (unitOfWork.LiquidityPurchaseDbRepository is not { } repository)
                return 0;

            var purchases = await repository.GetByChannelIdAsync(channel.ChannelId) ?? [];
            var feeMsat = 0L;
            foreach (var purchase in purchases.Where(p => p.Kind is LiquidityPurchaseKind.ChannelOpen
                                                                 or LiquidityPurchaseKind.OpenRbf))
            {
                if (purchase.FundingTxId != fundingTxId)
                {
                    // Another attempt of the open confirmed: this one never will
                    if (purchase.Status != LiquidityPurchaseStatus.Pending)
                        continue;

                    purchase.MarkReplaced();
                    repository.Update(purchase);
                    continue;
                }

                if (purchase.Status is not (LiquidityPurchaseStatus.Pending or LiquidityPurchaseStatus.Active))
                    continue;

                purchase.MarkActive(height);
                repository.Update(purchase);
                feeMsat = DualFundLiquidity.GetLocalFeeMsat(purchase);
                await ChannelAccountingEvents.RecordLiquidityPurchaseAsync(
                    unitOfWork, channel.ChannelId, fundingTxId, purchase.Role == LiquidityPurchaseRole.Buyer,
                    purchase.PeerNodeId, purchase.RequestedSat, purchase.ContributedSat, purchase.MiningFeeSat,
                    purchase.ServiceFeeSat,
                    purchase.Kind == LiquidityPurchaseKind.ChannelOpen
                        ? AccountingLiquidityKind.Open
                        : AccountingLiquidityKind.Rbf, occurredAt, logger, height, shortChannelId);
                logger.LogInformation("Liquidity {Role} on channel {ChannelId} active from block {Height} for {Blocks} "
                                    + "blocks", purchase.Role, channel.ChannelId, height, purchase.LeaseBlocks);
            }

            return feeMsat;
        }
        catch (NotSupportedException)
        {
            // A unit of work without the purchase table: nothing was bought or sold through it
            return 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the liquidity purchases of channel {ChannelId} at its funding "
                             + "confirmation", channel.ChannelId);
            return 0;
        }
    }
}