namespace NLightning.Domain.Client.Responses;

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
}