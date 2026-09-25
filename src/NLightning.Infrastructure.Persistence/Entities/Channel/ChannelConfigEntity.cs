// ReSharper disable PropertyCanBeMadeInitOnly.Global

using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

/// <summary>
/// Represents the parameters of a Lightning Network channel: what each side announced in open_channel/accept_channel
/// (<c>Local*</c> = ours, <c>Remote*</c> = the peer's) and the shared values.
/// </summary>
/// <remarks>
/// See <see cref="ChannelParams"/> for which side each value binds. Rows written before migration
/// <c>SplitChannelParams</c> had one set of values; the migration copied it into both sides.
/// </remarks>
public class ChannelConfigEntity
{
    /// <summary>
    /// The unique channel identifier this configuration belongs to.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// The minimum number of confirmations required for the funding transaction.
    /// </summary>
    public required uint MinimumDepth { get; set; }

    /// <summary>
    /// The to_self_delay we announced: the delay of the peer's to_local output.
    /// </summary>
    public required ushort LocalToSelfDelay { get; set; }

    /// <summary>
    /// The to_self_delay the peer announced: the delay of our to_local output.
    /// </summary>
    public required ushort RemoteToSelfDelay { get; set; }

    /// <summary>
    /// The max_accepted_htlcs we announced: how many HTLCs the peer may offer us.
    /// </summary>
    public required ushort LocalMaxAcceptedHtlcs { get; set; }

    /// <summary>
    /// The max_accepted_htlcs the peer announced: how many HTLCs we may offer the peer.
    /// </summary>
    public required ushort RemoteMaxAcceptedHtlcs { get; set; }

    /// <summary>
    /// The dust limit of our commitment transaction.
    /// </summary>
    public required long LocalDustLimitAmountSats { get; set; }

    /// <summary>
    /// The dust limit of the peer's commitment transaction.
    /// </summary>
    public required long RemoteDustLimitAmountSats { get; set; }

    /// <summary>
    /// The htlc_minimum_msat we announced.
    /// </summary>
    public required ulong LocalHtlcMinimumMsat { get; set; }

    /// <summary>
    /// The htlc_minimum_msat the peer announced.
    /// </summary>
    public required ulong RemoteHtlcMinimumMsat { get; set; }

    /// <summary>
    /// The channel_reserve_satoshis we announced: what the peer must keep.
    /// </summary>
    public required long LocalChannelReserveAmountSats { get; set; }

    /// <summary>
    /// The channel_reserve_satoshis the peer announced: what we must keep.
    /// </summary>
    public required long RemoteChannelReserveAmountSats { get; set; }

    /// <summary>
    /// The max_htlc_value_in_flight_msat we announced.
    /// </summary>
    public required ulong LocalMaxHtlcValueInFlightMsat { get; set; }

    /// <summary>
    /// The max_htlc_value_in_flight_msat the peer announced.
    /// </summary>
    public required ulong RemoteMaxHtlcValueInFlightMsat { get; set; }

    /// <summary>
    /// The fee rate in satoshis per kiloweight to use for commitment transactions.
    /// </summary>
    public required long FeeRatePerKwSatoshis { get; set; }

    /// <summary>
    /// Whether anchor outputs are enabled for this channel.
    /// </summary>
    public required bool OptionAnchorOutputs { get; set; }

    /// <summary>
    /// The upfront shutdown script for the local node, if specified.
    /// </summary>
    public byte[]? LocalUpfrontShutdownScript { get; set; }

    /// <summary>
    /// The upfront shutdown script for the remote node, if specified.
    /// </summary>
    public byte[]? RemoteUpfrontShutdownScript { get; set; }

    public byte UseScidAlias { get; set; }

    /// <summary>
    /// Default constructor for EF Core.
    /// </summary>
    internal ChannelConfigEntity()
    {
    }
}