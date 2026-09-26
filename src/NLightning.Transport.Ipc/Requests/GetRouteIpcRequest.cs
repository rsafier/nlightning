using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Request for GetRoute (ClientCommand 19, BOLT 7 plan G4-T4). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class GetRouteIpcRequest
{
    /// <summary>The destination.</summary>
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>What the destination must receive, in msat.</summary>
    [Key(1)] public ulong AmountMsat { get; init; }

    /// <summary>The most the route may cost in fees, in msat; null for the node's payment default.</summary>
    [Key(2)] public ulong? MaxFeeMsat { get; init; }

    /// <summary>The destination's <c>min_final_cltv_expiry_delta</c>; null for 18.</summary>
    [Key(3)] public ushort? FinalCltvDelta { get; init; }

    public GetRouteClientRequest ToClientRequest() =>
        new(NodeId, LightningMoney.MilliSatoshis(AmountMsat))
        {
            MaxFee = MaxFeeMsat is { } maxFee ? LightningMoney.MilliSatoshis(maxFee) : null,
            FinalCltvDelta = FinalCltvDelta
        };
}