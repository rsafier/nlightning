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

    public OpenChannelClientRequest ToClientRequest()
    {
        return new OpenChannelClientRequest(NodeInfo, Amount)
        {
            PushAmount = PushAmount,
            IsPublic = IsPublic,
            IsDualFunded = IsDualFunded,
            ForceV1 = ForceV1
        };
    }
}