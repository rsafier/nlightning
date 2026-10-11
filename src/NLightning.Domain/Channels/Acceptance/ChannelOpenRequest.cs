namespace NLightning.Domain.Channels.Acceptance;

using Crypto.ValueObjects;
using Money;
using Node;
using Protocol.ValueObjects;
using ValueObjects;

/// <summary>
/// A peer's channel open as an external decider sees it (LND's <c>ChannelAcceptRequest</c>, NL-1180): the values of its
/// <c>open_channel</c> or <c>open_channel2</c>, before we commit anything to it.
/// </summary>
/// <param name="NodeId">The opener.</param>
/// <param name="ChainHash">The chain of the channel.</param>
/// <param name="PendingChannelId">The temporary channel id; the decision is matched to the open by it.</param>
/// <param name="FundingAmount">The opener's funding (its contribution in a dual-funded open).</param>
/// <param name="PushAmount">What the opener pushes to us (zero in a dual-funded open).</param>
/// <param name="DustLimit">The opener's dust limit.</param>
/// <param name="MaxValueInFlight">The opener's <c>max_htlc_value_in_flight_msat</c>.</param>
/// <param name="ChannelReserve">The reserve the opener requires us to keep (zero in a dual-funded open: BOLT 2 fixes
/// it at 1 % of the total).</param>
/// <param name="HtlcMinimum">The opener's <c>htlc_minimum_msat</c>.</param>
/// <param name="FeeRatePerKw">The commitment feerate.</param>
/// <param name="ToSelfDelay">The opener's <c>to_self_delay</c> (on our outputs).</param>
/// <param name="MaxAcceptedHtlcs">The opener's <c>max_accepted_htlcs</c>.</param>
/// <param name="ChannelFlags">The <c>channel_flags</c> byte (bit 0: announce).</param>
/// <param name="ChannelType">The opener's <c>channel_type</c>, if any.</param>
/// <param name="DualFunded">Whether the open is <c>open_channel2</c>.</param>
public sealed record ChannelOpenRequest(
    CompactPubKey NodeId,
    ChainHash ChainHash,
    ChannelId PendingChannelId,
    LightningMoney FundingAmount,
    LightningMoney PushAmount,
    LightningMoney DustLimit,
    LightningMoney MaxValueInFlight,
    LightningMoney ChannelReserve,
    LightningMoney HtlcMinimum,
    ulong FeeRatePerKw,
    ushort ToSelfDelay,
    ushort MaxAcceptedHtlcs,
    byte ChannelFlags,
    FeatureSet? ChannelType,
    bool DualFunded);