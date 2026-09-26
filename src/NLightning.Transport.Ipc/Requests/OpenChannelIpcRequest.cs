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

    public OpenChannelClientRequest ToClientRequest()
    {
        return new OpenChannelClientRequest(NodeInfo, Amount) { PushAmount = PushAmount, IsPublic = IsPublic };
    }
}