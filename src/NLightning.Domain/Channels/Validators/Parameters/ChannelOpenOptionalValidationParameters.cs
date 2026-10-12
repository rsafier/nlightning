namespace NLightning.Domain.Channels.Validators.Parameters;

using Money;
using Protocol.Payloads;
using Protocol.ValueObjects;

public sealed class ChannelOpenOptionalValidationParameters
{
    public ChainHash? ChainHash { get; init; }
    public LightningMoney? FundingAmount { get; init; }
    public LightningMoney? PushAmount { get; init; }
    public required LightningMoney HtlcMinimumAmount { get; init; }
    public LightningMoney? MaxHtlcValueInFlight { get; init; }
    public required LightningMoney ChannelReserveAmount { get; init; }

    /// <summary>
    /// The channel's capacity: the peer's <c>funding_satoshis</c> for <c>open_channel</c>, our own for
    /// <c>accept_channel</c>. The reserve and <c>htlc_minimum_msat</c> limits are relative to it (NL-562).
    /// </summary>
    public required LightningMoney ChannelAmount { get; init; }

    public required ushort MaxAcceptedHtlcs { get; init; }
    public required LightningMoney DustLimitAmount { get; init; }
    public required ushort ToSelfDelay { get; init; }
    public LightningMoney? FeeRatePerKw { get; init; }

    /// <summary>
    /// Creates validation parameters from an incoming OpenChannel1Payload.
    /// </summary>
    public static ChannelOpenOptionalValidationParameters FromOpenChannel1Payload(OpenChannel1Payload payload)
    {
        return new ChannelOpenOptionalValidationParameters
        {
            ChainHash = payload.ChainHash,
            FundingAmount = payload.FundingAmount,
            PushAmount = payload.PushAmount,
            HtlcMinimumAmount = payload.HtlcMinimumAmount,
            MaxHtlcValueInFlight = payload.MaxHtlcValueInFlight,
            ChannelReserveAmount = payload.ChannelReserveAmount,
            ChannelAmount = payload.FundingAmount,
            MaxAcceptedHtlcs = payload.MaxAcceptedHtlcs,
            DustLimitAmount = payload.DustLimitAmount,
            ToSelfDelay = payload.ToSelfDelay,
            FeeRatePerKw = payload.FeeRatePerKw
        };
    }

    public static ChannelOpenOptionalValidationParameters FromAcceptChannel1Payload(
        AcceptChannel1Payload payload, LightningMoney channelAmount)
    {
        return new ChannelOpenOptionalValidationParameters
        {
            HtlcMinimumAmount = payload.HtlcMinimumAmount,
            ChannelReserveAmount = payload.ChannelReserveAmount,
            ChannelAmount = channelAmount,
            MaxAcceptedHtlcs = payload.MaxAcceptedHtlcs,
            DustLimitAmount = payload.DustLimitAmount,
            ToSelfDelay = payload.ToSelfDelay
        };
    }
}