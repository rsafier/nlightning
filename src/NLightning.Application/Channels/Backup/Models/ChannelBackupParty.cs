namespace NLightning.Application.Channels.Backup.Models;

using Domain.Channels.ValueObjects;
using Domain.Money;

/// <summary>
/// The channel parameters one side announced (<see cref="ChannelParty"/> without the upfront shutdown script).
/// </summary>
public sealed record ChannelBackupParty(
    ulong DustLimitSat,
    ulong ChannelReserveSat,
    ulong HtlcMinimumMsat,
    ushort MaxAcceptedHtlcs,
    ulong MaxHtlcValueInFlightMsat,
    ushort ToSelfDelay)
{
    /// <summary>The backed-up values of <paramref name="party"/>.</summary>
    public static ChannelBackupParty From(ChannelParty party) =>
        new((ulong)party.DustLimitAmount.Satoshi, (ulong)party.ChannelReserveAmount.Satoshi,
            party.HtlcMinimumAmount.MilliSatoshi, party.MaxAcceptedHtlcs, party.MaxHtlcValueInFlight.MilliSatoshi,
            party.ToSelfDelay);

    /// <summary>The <see cref="ChannelParty"/> these values describe.</summary>
    public ChannelParty ToChannelParty() =>
        new(LightningMoney.Satoshis(DustLimitSat), LightningMoney.Satoshis(ChannelReserveSat),
            LightningMoney.MilliSatoshis(HtlcMinimumMsat), MaxAcceptedHtlcs,
            LightningMoney.MilliSatoshis(MaxHtlcValueInFlightMsat), ToSelfDelay);
}