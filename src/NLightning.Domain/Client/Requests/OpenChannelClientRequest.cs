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
    /// <c>IDualFundedOpenService</c>, wave DF lane SP1-F). Not acted on until that lane; <c>option_dual_fund</c> stays
    /// experimental.
    /// </summary>
    public bool IsDualFunded { get; set; }

    public OpenChannelClientRequest(string nodeInfo, LightningMoney fundingAmount)
    {
        NodeInfo = nodeInfo;
        FundingAmount = fundingAmount;
    }
}