namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Money;

/// <summary>
/// The wallet's anchors reserve at one moment (NL-379).
/// </summary>
/// <param name="AnchorsChannelCount">The <c>option_anchors</c> channels that need the reserve (every one not yet
/// Closed).</param>
/// <param name="RequiredReserve">What the reserve holds for them (<c>Node:Anchors</c>).</param>
/// <param name="AvailableBalance">The confirmed wallet balance free to spend: outputs neither locked to a channel
/// funding, reserved for a fee nor spent by one of our pending broadcasts.</param>
/// <param name="SpendableBalance"><paramref name="AvailableBalance"/> minus <paramref name="RequiredReserve"/> (never
/// negative): what a channel funding or another ordinary spend may use.</param>
public sealed record AnchorReserveStatus(
    int AnchorsChannelCount,
    LightningMoney RequiredReserve,
    LightningMoney AvailableBalance,
    LightningMoney SpendableBalance)
{
    /// <summary>True when the available balance does not cover the reserve.</summary>
    public bool IsBelowReserve => AvailableBalance < RequiredReserve;
}