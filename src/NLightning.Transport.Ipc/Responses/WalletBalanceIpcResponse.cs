using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Money;

/// <summary>
/// Response for Wallet Balance command
/// </summary>
[MessagePackObject]
public sealed class WalletBalanceIpcResponse
{
    [Key(0)] public required LightningMoney ConfirmedBalance { get; init; }
    [Key(1)] public required LightningMoney UnconfirmedBalance { get; init; }

    /// <summary>The reserve kept for <c>option_anchors</c> channels (NL-379; <c>Node:Anchors</c>).</summary>
    [Key(2)] public required LightningMoney AnchorReserve { get; init; }

    /// <summary>The <c>option_anchors</c> channels the reserve is kept for.</summary>
    [Key(3)] public int AnchorsChannelCount { get; init; }

    /// <summary>
    /// The confirmed balance free to spend (not locked to a funding, reserved for a fee or spent by a pending
    /// broadcast).
    /// </summary>
    [Key(4)] public required LightningMoney AvailableBalance { get; init; }

    /// <summary>
    /// <see cref="AvailableBalance"/> minus <see cref="AnchorReserve"/>: what a channel funding may use.
    /// </summary>
    [Key(5)] public required LightningMoney SpendableBalance { get; init; }
}