using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Accounting;
using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using LiquidityAds;

/// <summary>
/// Liquidity ads in a splice and its RBF attempts (BOLT PR #1153 as Eclair 0.14.3 speaks it, NL-850; plan
/// <c>docs/agents/LIQUIDITY_ADS_PLAN.md</c> LA3/LA4): the buyer is the sender of <c>splice_init</c> (or
/// <c>tx_init_rbf</c>) with <c>request_funding</c>, the seller the side that answers <c>provide_funding</c> in
/// <c>splice_ack</c> (<c>tx_ack_rbf</c>) and contributes exactly the requested amount from its wallet.
/// </summary>
/// <remarks>
/// <para>The fee (mining plus service, no channel creation fee for a splice) moves from the buyer's balance to the
/// seller's on the new funding: it is in the <see cref="ChannelFunding"/> balance deltas (the buyer's delta is its
/// contribution less the fee, the seller's its contribution plus the fee), so both commitments on the new funding, the
/// lock and the on-chain resolution carry it; the shared funding's shares stay the contributions. The deltas are
/// stored with the funding row, so a restart keeps the fee; what needs the contributions themselves (a resumed
/// negotiation, an RBF rebuilt from the latest attempt) takes the fee back out with the attempt's purchase row
/// (<see cref="GetContributions"/>).</para>
/// <para>One purchase row per attempt (<c>LiquidityPurchases</c>, kind Splice or SpliceRbf), staged in the save of our
/// splice <c>commitment_signed</c>; the lock's save marks the locked attempt's row active and its siblings' replaced, and
/// books the fee (<see cref="ChannelAccountingEvents.RecordLiquidityPurchaseAsync"/>) next to the
/// <c>SpliceLocked</c> event, which leaves it out of the balance change.</para>
/// <para>An RBF of an attempt that carried a purchase must carry <c>request_funding</c> again (#1153): our bump repeats
/// the purchase when no new one is given, a peer's <c>tx_init_rbf</c> without it gets <c>tx_abort</c>, and only the
/// buyer may bump. The seller checks and signs each attempt again (the funding script is the same, the fee follows the
/// attempt's feerate).</para>
/// </remarks>
public sealed partial class SpliceService
{
    private LiquidityAdsService? GetLiquidityAds() => _serviceProvider.GetService<LiquidityAdsService>();

    /// <summary>The purchases of the unit of work, or null when it stores none (test doubles).</summary>
    private static ILiquidityPurchaseDbRepository? GetPurchases(IUnitOfWork unitOfWork)
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

    /// <summary>The saved purchase of a funding attempt, or null (none, or a unit of work without purchases).</summary>
    private async Task<LiquidityPurchaseModel?> GetPurchaseAsync(ChannelId channelId, TxId fundingTxId,
                                                                 IUnitOfWork? unitOfWork)
    {
        if (unitOfWork is not null)
            return await GetPurchaseAsync(GetPurchases(unitOfWork), channelId, fundingTxId);

        using var scope = _serviceProvider.CreateScope();
        return await GetPurchaseAsync(GetPurchases(scope.ServiceProvider.GetRequiredService<IUnitOfWork>()), channelId,
                                      fundingTxId);
    }

    private static async Task<LiquidityPurchaseModel?> GetPurchaseAsync(ILiquidityPurchaseDbRepository? purchases,
                                                                        ChannelId channelId, TxId fundingTxId)
    {
        if (purchases is null)
            return null;

        try
        {
            return await purchases.GetByFundingTxIdAsync(channelId, fundingTxId);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Our and the peer's signed contributions to a funding attempt, in satoshis: its balance deltas without the
    /// liquidity fee its purchase moved (<see cref="SpliceLiquidity.GetFeeMsat"/>).
    /// </summary>
    internal static (long Local, long Remote) GetContributions(ChannelFunding funding,
                                                               LiquidityPurchaseModel? purchase)
    {
        ArgumentNullException.ThrowIfNull(funding);
        var feeMsat = SpliceLiquidity.GetFeeMsat(purchase);
        return (checked(funding.LocalBalanceDeltaMsat + feeMsat) / 1_000,
                checked(funding.RemoteBalanceDeltaMsat - feeMsat) / 1_000);
    }

    #region Buyer

    /// <summary>
    /// Our purchase for a splice (<c>splice_init</c>): the seller's rate (given, else its cheapest one that sells the
    /// amount), at the splice's feerate, without a channel creation fee.
    /// </summary>
    /// <exception cref="InvalidOperationException">Liquidity ads are not available, or the peer sells nothing that
    /// fits.</exception>
    private SpliceLiquidity CreatePurchaseRequest(CompactPubKey seller, LiquidityRequest liquidity,
                                                  uint feeratePerKw)
    {
        var service = GetLiquidityAds()
                   ?? throw new InvalidOperationException("Liquidity ads are not available on this node");
        return new SpliceLiquidity(LiquidityPurchaseRole.Buyer,
                                   service.CreateRequest(seller, liquidity, feeratePerKw, false),
                                   liquidity.MaxFeeSat);
    }

    /// <summary>
    /// The purchase an RBF of the pending splice carries (BOLT PR #1153: an RBF after a purchase MUST request funding
    /// again): <paramref name="liquidity"/> when given, else the latest attempt's purchase repeated (same amount, rate
    /// and fee limit, the fee at the new feerate); null when neither. Only the buyer may bump an attempt that carries a purchase.
    /// </summary>
    /// <exception cref="InvalidOperationException">We sold the latest attempt's liquidity, or the new request cannot be
    /// made.</exception>
    private SpliceLiquidity? CreateRbfPurchaseRequest(ChannelModel channel, LiquidityRequest? liquidity,
                                                      LiquidityPurchaseModel? latestPurchase, uint feeratePerKw)
    {
        if (latestPurchase is { Role: LiquidityPurchaseRole.Seller })
            throw new InvalidOperationException(
                $"[LA-RBF-01] The pending splice of channel {channel.ChannelId} sold liquidity to the peer: only the buyer "
              + "may bump it (its RBF must request the funding again)");

        if (liquidity is not null)
            return CreatePurchaseRequest(channel.RemoteNodeId, liquidity, feeratePerKw);

        // The repeated purchase keeps the buyer's own fee limit of the attempt it replaces (NL-871): the fee follows the
        // new feerate, and a bump (bumpsplice, the auto-bumper) never names a limit of its own
        return latestPurchase is null
                   ? null
                   : new SpliceLiquidity(LiquidityPurchaseRole.Buyer,
                                         new RequestFunding(latestPurchase.RequestedSat, latestPurchase.Rate,
                                                            LiquidityPaymentDetails.FromChannelBalance),
                                         latestPurchase.MaxFeeSat);
    }

    /// <summary>
    /// LA-RES-01 before we ask: the fee of <paramref name="liquidity"/> at <paramref name="feeratePerKw"/>, assuming
    /// the seller contributes exactly the requested amount, leaves us the reserve of the new capacity.
    /// </summary>
    private static SpliceRuleViolation? CheckBuyerCanPay(ChannelModel channel, FundingSet fundings,
                                                         SpliceLiquidity liquidity, long ourContributionSatoshis,
                                                         uint feeratePerKw)
    {
        var request = liquidity.Request;
        var fees = LiquidityAdsRules.ComputeFees(request.Rate, feeratePerKw, request.RequestedSat,
                                                 request.RequestedSat, false);
        var capacity = SpliceRules.GetNewCapacitySatoshis(fundings.Current.CapacitySatoshis, ourContributionSatoshis,
                                                          checked((long)request.RequestedSat)) ?? 0;
        return SpliceRules.CheckLiquidityFeeReserve(channel.Commitments?.LocalCommit.Spec.LocalMsat ?? 0,
                                                    ourContributionSatoshis, fees.TotalMsat,
                                                    (ulong)channel.ChannelParams.Remote.ChannelReserveAmount.Satoshi,
                                                    capacity, true);
    }

    /// <summary>
    /// The seller's answer to our request (<c>splice_ack</c>/<c>tx_ack_rbf</c>), once the new funding script is known:
    /// Eclair's <c>validateRemoteFunding</c> (present, signed by the seller over the new funding script, at least the
    /// requested amount, the requested rate), our fee limit, and LA-RES-01 with the fee. Records the answer and the fee
    /// on the purchase; returns why it is refused, or null.
    /// </summary>
    private string? ValidateSellerAnswer(SpliceNegotiation negotiation, WillFund? willFund,
                                         long sellerContributionSatoshis)
    {
        if (negotiation.Liquidity is not { Role: LiquidityPurchaseRole.Buyer } liquidity)
            return null;

        var service = GetLiquidityAds();
        var script = negotiation.NewFundingScript;
        if (service is null || script is null)
            return "liquidity ads: the purchase cannot be checked";

        var fees = default(LiquidityFees);
        var refusal = sellerContributionSatoshis < 0
                          ? LiquidityAdsRefusal.AmountTooLow
                          : service.ValidateWillFund(negotiation.PeerPubKey, liquidity.Request, willFund,
                                                     ((byte[])script.Value).AsSpan(),
                                                     (ulong)sellerContributionSatoshis, negotiation.Model.FeeratePerKw,
                                                     false, liquidity.MaxFeeSat, out fees);
        if (refusal != LiquidityAdsRefusal.None)
            return $"liquidity ads: the seller's answer is refused ({refusal})";

        var capacity = negotiation.SharedFunding is { } shared ? (ulong)shared.SharedOutputAmount.Satoshi : 0;
        if (SpliceRules.CheckLiquidityFeeReserve(negotiation.LocalMainMsat, negotiation.Model.LocalContributionSatoshis,
                                                 fees.TotalMsat, negotiation.LocalReserveSatoshis, capacity, true) is
            { } violation)
            return $"[{violation.RequirementId}] {violation.Reason}";

        liquidity.WillFund = willFund;
        liquidity.Fees = fees;
        _logger.LogInformation("Buying {Amount} sat of inbound liquidity from {Seller} on channel {ChannelId}: fee "
                             + "{Fee} sat (mining {Mining}, service {Service})", liquidity.Request.RequestedSat,
                               negotiation.PeerPubKey, negotiation.ChannelId, fees.TotalSat, fees.MiningFeeSat,
                               fees.ServiceFeeSat);
        return null;
    }

    #endregion

    #region Seller

    /// <summary>
    /// A buyer's <c>request_funding</c> in its <c>splice_init</c>/<c>tx_init_rbf</c> (Eclair <c>validateRequest</c>):
    /// we sell (rates configured), the request is valid for our rates, a griefing-cap slot is free (D-L5), and the buyer
    /// keeps its reserve after paying the fee (LA-RES-01). Returns why it is refused, or null with the sale (holding a
    /// slot; the caller ends it when the negotiation ends).
    /// </summary>
    private string? TryStartSpliceSale(ChannelModel channel, CompactPubKey buyer, RequestFunding request,
                                       uint feeratePerKw, long buyerContributionSatoshis, FundingSet fundings,
                                       out SpliceLiquidity? sale)
    {
        sale = null;
        if (GetLiquidityAds() is not { } service)
            return "we do not sell liquidity";

        if (service.TryStartSale(buyer, request, out var slot) is { } refusal)
            return refusal;

        var fees = LiquidityAdsRules.ComputeFees(request.Rate, feeratePerKw, request.RequestedSat,
                                                 request.RequestedSat, false);
        var capacity = SpliceRules.GetNewCapacitySatoshis(fundings.Current.CapacitySatoshis, buyerContributionSatoshis,
                                                          checked((long)request.RequestedSat));
        var violation = capacity is null
                            ? null
                            : SpliceRules.CheckLiquidityFeeReserve(
                                channel.Commitments?.LocalCommit.Spec.RemoteMsat ?? 0, buyerContributionSatoshis,
                                fees.TotalMsat, (ulong)channel.ChannelParams.Local.ChannelReserveAmount.Satoshi,
                                capacity.Value, false);
        if (capacity is null || violation is not null)
        {
            slot!.Dispose();
            return violation is null
                       ? "the contributions leave no channel capacity"
                       : $"[{violation.RequirementId}] {violation.Reason}";
        }

        sale = new SpliceLiquidity(LiquidityPurchaseRole.Seller, request) { Fees = fees, Sale = slot };
        return null;
    }

    /// <summary>
    /// Our wallet contribution of exactly the amount we sell, as the interactive-tx non-initiator (we pay only for our
    /// own inputs and outputs), reserved the way a splice-in is.
    /// </summary>
    private async Task<InteractiveTxContribution> ReserveSaleContributionAsync(ChannelId channelId, ulong amountSat,
                                                                               uint feeratePerKw,
                                                                               CancellationToken cancellationToken)
    {
        var contributor = _serviceProvider.GetService<IInteractiveTxContributor>()
                       ?? throw new InvalidOperationException("No wallet contributor is available");
        return await contributor.ContributeAsync(
                   new InteractiveTxContributionRequest(channelId, InteractiveTxPurpose.Splice,
                                                        LightningMoney.Satoshis(amountSat), [], feeratePerKw, 0, true),
                   cancellationToken);
    }

    /// <summary>Our signed <c>will_fund</c> over the new funding output's script (once both funding keys are known).
    /// </summary>
    private void SignSale(SpliceNegotiation negotiation)
    {
        if (negotiation.Liquidity is not { Role: LiquidityPurchaseRole.Seller } sale
         || negotiation.NewFundingScript is not { } script)
            return;

        sale.WillFund = GetLiquidityAds()!.CreateWillFund(sale.Request.Rate, ((byte[])script).ToArray());
        _logger.LogInformation("Selling {Amount} sat of inbound liquidity to {Buyer} on channel {ChannelId}: fee {Fee} "
                             + "sat (mining {Mining}, service {Service})", sale.Request.RequestedSat,
                               negotiation.PeerPubKey, negotiation.ChannelId, sale.Fees?.TotalSat,
                               sale.Fees?.MiningFeeSat, sale.Fees?.ServiceFeeSat);
    }

    #endregion

    #region Records

    /// <summary>
    /// In the save of our splice <c>commitment_signed</c>: the attempt's purchase row (Pending), once per attempt.
    /// </summary>
    private async Task StagePurchaseAsync(SpliceNegotiation negotiation, TxId fundingTxId, IUnitOfWork unitOfWork)
    {
        if (negotiation.Liquidity is not { WillFund: { } willFund, Fees: { } fees } liquidity
         || GetPurchases(unitOfWork) is not { } purchases
         || GetLiquidityAds() is not { } service)
            return;

        if (await GetPurchaseAsync(purchases, negotiation.ChannelId, fundingTxId) is { } stored)
        {
            liquidity.Purchase = stored;
            return;
        }

        var contributed = liquidity.Role == LiquidityPurchaseRole.Seller
                              ? negotiation.Model.LocalContributionSatoshis
                              : negotiation.Model.RemoteContributionSatoshis ?? 0;
        var purchase = service.CreatePurchase(negotiation.ChannelId, fundingTxId, liquidity.Role,
                                              negotiation.Model.IsRbf
                                                  ? LiquidityPurchaseKind.SpliceRbf
                                                  : LiquidityPurchaseKind.Splice,
                                              liquidity.Request, (ulong)Math.Max(0, contributed), fees, willFund,
                                              negotiation.PeerPubKey, liquidity.MaxFeeSat);
        purchases.Add(purchase);
        liquidity.Purchase = purchase;
    }

    /// <summary>
    /// In the save that discards an aborted splice attempt (<c>tx_abort</c> or a reconnection without
    /// <c>next_funding</c> before our <c>tx_signatures</c>): the attempt's purchase row, still Pending, is marked
    /// replaced, so it no longer holds the seller's lease guard (D-L4) or a griefing-cap slot (D-L5). Nothing can confirm
    /// the attempt any more, and no lock will ever retire it (NL-870). Returns whether a row was staged.
    /// </summary>
    private async Task<bool> StageAbandonedPurchaseAsync(ChannelId channelId, TxId fundingTxId, IUnitOfWork unitOfWork)
    {
        if (GetPurchases(unitOfWork) is not { } purchases
         || await GetPurchaseAsync(purchases, channelId, fundingTxId) is not
         { Status: LiquidityPurchaseStatus.Pending } purchase)
            return false;

        purchase.MarkReplaced();
        purchases.Update(purchase);
        _logger.LogInformation("Liquidity purchase of channel {ChannelId} in aborted splice {TxId} abandoned ({Role})",
                               channelId, fundingTxId, purchase.Role);
        return true;
    }

    /// <summary>
    /// In the lock's save (SP-LK-03): the locked attempt's purchase becomes active at the splice's height and the
    /// discarded siblings' are replaced; the fee is booked (<see cref="ChannelAccountingEvents.RecordLiquidityPurchaseAsync"/>).
    /// Returns the locked attempt's fee from our point of view (+ paid, − earned; 0 without a purchase), for the
    /// <c>SpliceLocked</c> event.
    /// </summary>
    private async Task<long> StageLiquidityAtLockAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                       ChannelFunding locked, IReadOnlyList<ChannelFunding> retired,
                                                       DateTimeOffset occurredAt)
    {
        if (GetPurchases(unitOfWork) is not { } purchases)
            return 0;

        IReadOnlyList<LiquidityPurchaseModel> rows;
        try
        {
            rows = await purchases.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (NotSupportedException)
        {
            return 0;
        }

        var discarded = retired.Where(f => f.Status == ChannelFundingStatus.Discarded)
                               .Select(f => f.FundingTxId)
                               .ToHashSet();
        foreach (var sibling in rows.Where(r => r.Status == LiquidityPurchaseStatus.Pending
                                             && discarded.Contains(r.FundingTxId)))
        {
            sibling.MarkReplaced();
            purchases.Update(sibling);
        }

        if (rows.FirstOrDefault(r => r.FundingTxId == locked.FundingTxId) is not
            { Status: LiquidityPurchaseStatus.Pending or LiquidityPurchaseStatus.Active } purchase)
            return 0;

        var height = locked.ConfirmedHeight ?? locked.ShortChannelId?.BlockHeight ?? GetTip();
        purchase.MarkActive(height);
        purchases.Update(purchase);
        await ChannelAccountingEvents.RecordLiquidityPurchaseAsync(
            unitOfWork, channel.ChannelId, locked.FundingTxId, purchase.Role == LiquidityPurchaseRole.Buyer,
            purchase.PeerNodeId, purchase.RequestedSat, purchase.ContributedSat, purchase.MiningFeeSat,
            purchase.ServiceFeeSat, AccountingLiquidityKind.Splice, occurredAt, _logger, height,
            locked.ShortChannelId);
        _logger.LogInformation("Liquidity purchase of channel {ChannelId} in splice {TxId} active from height "
                             + "{Height} ({Role}, fee {Fee} msat)", channel.ChannelId, locked.FundingTxId, height,
                               purchase.Role, purchase.TotalFeeMsat);
        return SpliceLiquidity.GetFeeMsat(purchase);
    }

    #endregion
}