namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;

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

    /// <summary>Whether an anchors channel counts toward the anchors reserve while it is being opened.</summary>
    public bool HoldsAnchorReserve { get; set; }

    public LightningMoney Total => LightningMoney.MilliSatoshis(LocalShare.MilliSatoshi + RemoteShare.MilliSatoshi);

    /// <summary>Completes whoever waits (the open, or the bump) with <paramref name="result"/>.</summary>
    public void Complete(DualFundedOpenResult result)
    {
        BumpCompletion?.TrySetResult(result);
        BumpCompletion = null;
        OpenCompletion?.TrySetResult(result);
    }

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