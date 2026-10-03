namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using LiquidityAds;

/// <summary>
/// One dual-funded open (<c>open_channel2</c> to <c>channel_ready</c>) as <see cref="DualFundedOpenService"/> tracks it:
/// memory only, touched under the channel's lock (the temporary id's before <c>accept_channel2</c>, the v2 id's
/// after). After a restart it is rebuilt from the channel and its stored interactive-tx rows.
/// </summary>
internal sealed class DualFundNegotiation
{
    public DualFundNegotiation(ChannelId channelId, ChannelId temporaryChannelId, CompactPubKey peer, bool isOpener)
    {
        ChannelId = channelId;
        TemporaryChannelId = temporaryChannelId;
        Peer = peer;
        IsOpener = isOpener;
    }

    /// <summary>The v2 channel id (the temporary id until <c>accept_channel2</c>).</summary>
    public ChannelId ChannelId { get; set; }

    public ChannelId TemporaryChannelId { get; }
    public CompactPubKey Peer { get; }

    /// <summary>We sent <c>open_channel2</c> (we are the channel's funder of the commitment fees).</summary>
    public bool IsOpener { get; }

    /// <summary>The channel, from <c>accept_channel2</c> on (the opener's placeholder before it).</summary>
    public ChannelModel? Channel { get; set; }

    public LightningMoney LocalShare { get; set; } = LightningMoney.Zero;
    public LightningMoney RemoteShare { get; set; } = LightningMoney.Zero;
    public uint FundingFeeratePerKw { get; set; }
    public uint Locktime { get; set; }

    /// <summary>We sent <c>require_confirmed_inputs</c>.</summary>
    public bool LocalRequiresConfirmedInputs { get; set; }

    /// <summary>The peer sent <c>require_confirmed_inputs</c>.</summary>
    public bool RemoteRequiresConfirmedInputs { get; set; }

    /// <summary>The opener's <c>open_channel2</c> before <c>accept_channel2</c> arrives (our side of it).</summary>
    public PendingOpen? Pending { get; set; }

    /// <summary>The transaction of the attempt whose <c>commitment_signed</c> we sent and that is not complete yet.</summary>
    public TxId? PendingTxId { get; set; }

    /// <summary>The peer's valid <c>commitment_signed</c> for <see cref="PendingTxId"/> arrived.</summary>
    public bool CommitmentSignedReceived { get; set; }

    /// <summary>Our contribution to the last constructed attempt (an RBF re-adds its inputs, IT-RBF-01).</summary>
    public InteractiveTxContribution? LastContribution { get; set; }

    /// <summary>The feerate of the last constructed attempt (the RBF floor, IT-RBF-01).</summary>
    public uint LastFeeratePerKw { get; set; }

    /// <summary>The fully signed attempts, oldest first.</summary>
    public List<TxId> CompletedTxIds { get; } = [];

    /// <summary>The host this negotiation runs with (one per channel, kept across RBF attempts).</summary>
    public DualFundHost? Host { get; set; }

    /// <summary>What <c>OpenAsync</c> waits for.</summary>
    public TaskCompletionSource<DualFundedOpenResult>? OpenCompletion { get; set; }

    /// <summary>What <c>BumpAsync</c> waits for.</summary>
    public TaskCompletionSource<DualFundedOpenResult>? BumpCompletion { get; set; }

    /// <summary>
    /// The channel's funding outpoint and commitment signatures of the last fully signed attempt, kept while an RBF
    /// attempt has replaced them on the channel and is not fully signed: restored when that attempt ends without
    /// both <c>tx_signatures</c>. Memory only.
    /// </summary>
    public SignedFunding? LastSignedFunding { get; set; }

    /// <summary>
    /// A wallet contribution reserved for our own <c>tx_init_rbf</c> that no earlier attempt holds (an accepter that
    /// funded nothing before starts the RBF, NL-530): released when the RBF request ends before an attempt exists (the
    /// driver releases an attempt's own contribution). Memory only.
    /// </summary>
    public InteractiveTxContribution? FreshRbfContribution { get; set; }

    /// <summary>
    /// Our <c>request_funding</c> waiting for the seller's answer (<c>accept_channel2</c> of our open, <c>tx_ack_rbf</c>
    /// of our RBF; liquidity ads, NL-771). Memory only.
    /// </summary>
    public DualFundLiquidityRequest? LiquidityRequest { get; set; }

    /// <summary>
    /// The liquidity purchase of the attempt being negotiated, from the request and its answer until the attempt's
    /// commitment step records it (<see cref="Purchases"/>); null when the attempt buys nothing. Memory only.
    /// </summary>
    public DualFundLiquidity? AttemptLiquidity { get; set; }

    /// <summary>
    /// The liquidity purchases recorded with the open's attempts, by funding txid (rebuilt from
    /// <c>LiquidityPurchases</c> after a restart): the fee of an attempt moves its balances.
    /// </summary>
    public Dictionary<TxId, LiquidityPurchaseModel> Purchases { get; } = [];

    /// <summary>
    /// The sale slot (griefing cap, D-L5) the attempt we sell liquidity in holds until its negotiation ends. Memory only.
    /// </summary>
    public LiquidityAdsService.LiquiditySale? Sale { get; set; }

    /// <summary>Whether an anchors channel counts toward the anchors reserve while it is being opened.</summary>
    public bool HoldsAnchorReserve { get; set; }

    /// <summary>
    /// The shares of the last fully signed attempt while an RBF attempt runs with other ones (BOLT 2: either side may
    /// change its <c>funding_output_contribution</c> in <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, NL-521): put back when the
    /// attempt ends before both <c>tx_signatures</c>, dropped when it completes. Memory only.
    /// </summary>
    public (LightningMoney Local, LightningMoney Remote)? SharesBeforeRbf { get; set; }

    public LightningMoney Total => LightningMoney.MilliSatoshis(LocalShare.MilliSatoshi + RemoteShare.MilliSatoshi);

    /// <summary>
    /// Our fee (msat, + we buy, − we sell) of the attempt <paramref name="fundingTxId"/>: 0 when it bought nothing.
    /// </summary>
    public long GetLocalLiquidityFeeMsat(TxId? fundingTxId) =>
        fundingTxId is { } txId ? DualFundLiquidity.GetLocalFeeMsat(Purchases.GetValueOrDefault(txId)) : 0;

    /// <summary>
    /// The purchase of the latest fully signed attempt, or null when it bought nothing (BOLT PR #1153: an RBF of an
    /// attempt with a purchase must carry <c>request_funding</c> again).
    /// </summary>
    public LiquidityPurchaseModel? LatestSignedPurchase =>
        CompletedTxIds.Count == 0 ? null : Purchases.GetValueOrDefault(CompletedTxIds[^1]);

    /// <summary>Gives the sale slot of the attempt back (idempotent).</summary>
    public void EndSale()
    {
        Sale?.Dispose();
        Sale = null;
    }

    /// <summary>
    /// The attempt's shares become <paramref name="local"/> and <paramref name="remote"/>; the signed attempt's are
    /// kept in <see cref="SharesBeforeRbf"/> (once per attempt).
    /// </summary>
    public void ChangeShares(LightningMoney local, LightningMoney remote)
    {
        SharesBeforeRbf ??= (LocalShare, RemoteShare);
        LocalShare = local;
        RemoteShare = remote;
    }

    /// <summary>An RBF attempt ended before it was signed: the signed attempt's shares again.</summary>
    public void RestoreShares()
    {
        if (SharesBeforeRbf is not { } shares)
            return;

        LocalShare = shares.Local;
        RemoteShare = shares.Remote;
        SharesBeforeRbf = null;
    }

    /// <summary>Completes whoever waits (the open, or the bump) with <paramref name="result"/>.</summary>
    public void Complete(DualFundedOpenResult result)
    {
        BumpCompletion?.TrySetResult(result);
        BumpCompletion = null;
        OpenCompletion?.TrySetResult(result);
    }

    /// <summary>
    /// A fully signed attempt's funding outpoint, capacity, balances and parameters (the reserve and in-flight limits
    /// follow the capacity) and first-commitment signatures.
    /// </summary>
    internal sealed record SignedFunding(
        TxId TransactionId,
        ushort Index,
        LightningMoney Capacity,
        LightningMoney LocalBalance,
        LightningMoney RemoteBalance,
        ChannelParams ChannelParams,
        CompactSignature? LastSentSignature,
        CompactSignature? LastReceivedSignature);

    /// <summary>What the opener chose before the peer answered.</summary>
    internal sealed record PendingOpen(
        uint KeyIndex,
        ChannelBasepoints Basepoints,
        CompactPubKey FirstPerCommitmentPoint,
        ChannelParty LocalParams,
        byte[] ChannelType,
        uint CommitmentFeeratePerKw,
        bool IsPublic,
        bool OptionAnchors,
        Domain.Enums.FeatureSupport UseScidAlias);
}