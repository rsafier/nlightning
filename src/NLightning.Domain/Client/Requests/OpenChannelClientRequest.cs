namespace NLightning.Domain.Client.Requests;

using Money;

public sealed class OpenChannelClientRequest
{
    public string NodeInfo { get; set; }
    public LightningMoney FundingAmount { get; set; }
    public LightningMoney? HtlcMinimumAmount { get; set; }
    public LightningMoney? MaxHtlcValueInFlight { get; set; }
    public LightningMoney? ChannelReserveAmount { get; set; }
    public ushort? MaxAcceptedHtlcs { get; set; }
    public LightningMoney? DustLimitAmount { get; set; }
    public LightningMoney? PushAmount { get; set; }
    public ushort? ToSelfDelay { get; set; }
    public LightningMoney? FeeRatePerKw { get; set; }
    public bool IsZeroConfChannel { get; set; }

    /// <summary>
    /// Open a public channel: <c>announce_channel</c> is set in <c>open_channel.channel_flags</c> and the channel type
    /// leaves out <c>option_scid_alias</c> (BOLT 2); the channel is announced (BOLT 7) once it is deep enough. Refused
    /// together with <see cref="IsZeroConfChannel"/>.
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Open a dual-funded (v2) channel with <c>open_channel2</c> (BOLT 2 "Channel Establishment v2",
    /// <c>IDualFundedOpenService</c>, wave DF): <see cref="FundingAmount"/> is our contribution and the peer may add
    /// its own; no push. Refused when <c>option_dual_fund</c> is not negotiated with the peer. Without it (and without
    /// <see cref="ForceV1"/>) the open is still v2 when the peer supports dual funding and nothing needs v1 (NL-551).
    /// </summary>
    public bool IsDualFunded { get; set; }

    /// <summary>
    /// Open a v1 channel (<c>open_channel</c>) even when <c>option_dual_fund</c> is negotiated with the peer
    /// (<c>openchannel --v1</c>, NL-551). Refused together with <see cref="IsDualFunded"/>.
    /// </summary>
    public bool ForceV1 { get; set; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    public OpenChannelClientRequest(string nodeInfo, LightningMoney fundingAmount)
    {
        NodeInfo = nodeInfo;
        FundingAmount = fundingAmount;
    }
}