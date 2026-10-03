namespace NLightning.Domain.LiquidityAds.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// Stores the liquidity purchases we made and sold (plan <c>docs/agents/LIQUIDITY_ADS_PLAN.md</c> LA3, migration
/// <c>AddLiquidityPurchases</c>, table <c>LiquidityPurchases</c>); reached through
/// <c>IUnitOfWork.LiquidityPurchaseDbRepository</c>.
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>, which also fills in the
/// <see cref="LiquidityPurchaseModel.Id"/> of every purchase added through this repository. Reads see what is saved, not
/// what this unit of work staged. One purchase per (channel id, funding txid): a second one fails the save.
/// </remarks>
public interface ILiquidityPurchaseDbRepository
{
    /// <summary>
    /// Stages a new purchase. Its <see cref="LiquidityPurchaseModel.Id"/> must be 0; it is set by the save.
    /// </summary>
    void Add(LiquidityPurchaseModel purchase);

    /// <summary>
    /// Stages the purchase's mutable fields (status, lease start, close). A purchase added through this repository and
    /// not saved yet is updated in place; otherwise its <see cref="LiquidityPurchaseModel.Id"/> names the row.
    /// </summary>
    void Update(LiquidityPurchaseModel purchase);

    /// <summary>
    /// The purchases of a channel, oldest first (every attempt: the open, its RBF attempts and the splices).
    /// </summary>
    Task<IReadOnlyList<LiquidityPurchaseModel>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// The purchase that rides on the funding attempt <paramref name="fundingTxId"/> of the channel, or null.
    /// </summary>
    Task<LiquidityPurchaseModel?> GetByFundingTxIdAsync(ChannelId channelId, TxId fundingTxId);

    /// <summary>
    /// Purchases, newest first.
    /// </summary>
    /// <param name="role">Only this role, or both when null.</param>
    /// <param name="status">Only this status, or every status when null.</param>
    /// <param name="skip">How many of the newest to skip.</param>
    /// <param name="take">How many to return at most.</param>
    Task<IReadOnlyList<LiquidityPurchaseModel>> ListAsync(LiquidityPurchaseRole? role, LiquidityPurchaseStatus? status,
                                                          int skip, int take);

    /// <summary>
    /// How many sales are <see cref="LiquidityPurchaseStatus.Pending"/>: negotiated attempts that have not confirmed and
    /// may still hold our wallet inputs (the node-wide griefing cap, D-L5).
    /// </summary>
    Task<int> CountPendingSalesAsync();

    /// <summary>
    /// How many sales to <paramref name="peerNodeId"/> are <see cref="LiquidityPurchaseStatus.Pending"/> (the per-peer
    /// griefing cap, D-L5).
    /// </summary>
    Task<int> CountPendingSalesByPeerAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// The newest sale on the channel whose lease still binds us at <paramref name="currentHeight"/>
    /// (<see cref="LiquidityPurchaseModel.IsLeaseInForce"/>: pending, or active and not past its lease end), or null:
    /// the cooperative close guard (D-L4).
    /// </summary>
    Task<LiquidityPurchaseModel?> GetActiveSaleLeaseAsync(ChannelId channelId, uint currentHeight);
}