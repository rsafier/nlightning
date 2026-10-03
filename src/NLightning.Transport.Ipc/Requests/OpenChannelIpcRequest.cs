using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Empty request for OpenChannel.
/// </summary>
[MessagePackObject]
public sealed class OpenChannelIpcRequest
{
    [Key(0)] public required string NodeInfo { get; init; }
    [Key(2)] public required LightningMoney Amount { get; init; }

    /// <summary>
    /// The part of <see cref="Amount"/> given to the peer at open (push_msat); null or absent pushes nothing.
    /// </summary>
    [Key(3)] public LightningMoney? PushAmount { get; init; }

    /// <summary>
    /// Open a public channel (<c>openchannel --public</c>, BOLT 7 plan G1-T1); absent (an older client) means private.
    /// </summary>
    [Key(4)] public bool IsPublic { get; init; }

    /// <summary>
    /// Open a dual-funded (v2) channel (<c>openchannel --dual-fund</c>, wave DF lane SP1-F), refused when the peer does
    /// not support it; absent (an older client) leaves the choice to the daemon (NL-551).
    /// </summary>
    [Key(5)] public bool IsDualFunded { get; init; }

    /// <summary>
    /// Open a v1 channel even when the peer supports dual funding (<c>openchannel --v1</c>, NL-551); absent (an older
    /// client) means the daemon's default: v2 when <c>option_dual_fund</c> is negotiated and there is no push amount.
    /// </summary>
    [Key(6)] public bool ForceV1 { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(7)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(8)] public List<string>? Tags { get; init; }

    /// <summary>
    /// Inbound liquidity to buy from the peer with a dual-funded open (liquidity ads, NL-850,
    /// <c>--request-inbound</c>), in satoshis, or null; an older client sends none.
    /// </summary>
    [Key(9)] public ulong? RequestInboundSat { get; init; }

    /// <summary>The most we pay for the purchase, in satoshis (<c>--max-liquidity-fee</c>), or null.</summary>
    [Key(10)] public ulong? MaxLiquidityFeeSat { get; init; }

    public OpenChannelClientRequest ToClientRequest()
    {
        return new OpenChannelClientRequest(NodeInfo, Amount)
        {
            PushAmount = PushAmount,
            IsPublic = IsPublic,
            IsDualFunded = IsDualFunded,
            ForceV1 = ForceV1,
            Label = Label,
            Tags = Tags ?? [],
            RequestInboundSat = RequestInboundSat,
            MaxLiquidityFeeSat = MaxLiquidityFeeSat
        };
    }
}