namespace NLightning.Domain.Client.Responses;

using Bitcoin.Transactions.Enums;
using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;

/// <summary>
/// One channel as listed by <c>ListChannels</c>.
/// </summary>
public sealed class ChannelInfoClientResponse
{
    public required ChannelId ChannelId { get; init; }
    public required CompactPubKey PeerId { get; init; }
    public required ChannelState State { get; init; }
    public bool IsInitiator { get; init; }

    /// <summary>
    /// The channel type's commitment format: simple taproot, anchors or static_remotekey (NL-987).
    /// </summary>
    public CommitmentFormat ChannelType { get; init; }
    public bool IsPeerConnected { get; init; }

    /// <summary>
    /// The real short channel id, or null until the funding transaction is confirmed.
    /// </summary>
    public ShortChannelId? ShortChannelId { get; init; }

    public TxId? FundingTxId { get; init; }
    public ushort? FundingOutputIndex { get; init; }
    public required LightningMoney Capacity { get; init; }

    /// <summary>
    /// Our balance (gross: it includes the HTLCs we offered that are still pending).
    /// </summary>
    public required LightningMoney LocalBalance { get; init; }

    /// <summary>
    /// The peer's balance (gross: it includes the HTLCs the peer offered that are still pending).
    /// </summary>
    public required LightningMoney RemoteBalance { get; init; }

    /// <summary>
    /// The number of our current commitment transaction.
    /// </summary>
    public ulong LocalCommitmentNumber { get; init; }

    /// <summary>
    /// The number of the peer's current commitment transaction.
    /// </summary>
    public ulong RemoteCommitmentNumber { get; init; }

    /// <summary>
    /// Pending HTLCs we offered.
    /// </summary>
    public int OfferedHtlcCount { get; init; }

    /// <summary>
    /// Pending HTLCs the peer offered.
    /// </summary>
    public int ReceivedHtlcCount { get; init; }

    /// <summary>
    /// True when the peer proved during channel_reestablish that we lost state (option_data_loss_protect).
    /// Always false until reestablish is implemented (BOLT2 plan N7-T4).
    /// </summary>
    public bool DataLossDetected { get; init; }

    /// <summary>
    /// True when <c>channel_reestablish</c> was exchanged on the current connection, so the channel accepts updates
    /// (it is usable for HTLCs when it is also <c>Open</c> and the peer is connected). Always false until reestablish
    /// is implemented (BOLT2 plan N7).
    /// </summary>
    public bool IsReestablished { get; init; }

    /// <summary>
    /// Our forwarding <c>fee_base_msat</c> on this channel (what a route hint through us must use).
    /// </summary>
    public uint FeeBaseMsat { get; init; }

    /// <summary>
    /// Our forwarding <c>fee_proportional_millionths</c> on this channel.
    /// </summary>
    public uint FeePpm { get; init; }

    /// <summary>
    /// Our <c>cltv_expiry_delta</c> on this channel (wave sp1 lane SP1-G).
    /// </summary>
    public ushort CltvExpiryDelta { get; init; }

    /// <summary>
    /// The <c>htlc_minimum_msat</c> this channel announces and our forwarding enforces.
    /// </summary>
    public ulong HtlcMinimumMsat { get; init; }

    /// <summary>
    /// The <c>htlc_maximum_msat</c> this channel announces and our forwarding enforces.
    /// </summary>
    public ulong HtlcMaximumMsat { get; init; }

    /// <summary>
    /// True when the channel has a <c>setchannelpolicy</c> override (some of its values are not <c>Node:Routing</c>'s).
    /// </summary>
    public bool HasPolicyOverride { get; init; }

    /// <summary>
    /// The channel's fundings: current first, then the pending splices (splicing plan §3.10, SP2-0; lane SP2-D). Empty
    /// until filled.
    /// </summary>
    public IReadOnlyList<ChannelFundingInfoClientResponse> Fundings { get; init; } = [];

    /// <summary>
    /// Short channel ids retired by splice locks that still resolve (D12), oldest first (SP2-0; lane SP2-D).
    /// </summary>
    public IReadOnlyList<RetiredScidInfoClientResponse> RetiredShortChannelIds { get; init; } = [];

    /// <summary>
    /// The operator's label given at the open (NL-602 A3-T1, <c>openchannel --label</c>), or null.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c>, sorted by key (NL-602 A3-T1); empty for none.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}