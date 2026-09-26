using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for GetRoute (ClientCommand 19): the route a payment would take now, hop by hop.
/// </summary>
[MessagePackObject]
public sealed class GetRouteIpcResponse
{
    /// <summary>Our channel of the first HTLC.</summary>
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>Our peer first, the destination last.</summary>
    [Key(1)] public required List<GetRouteHopIpcInfo> Hops { get; init; }

    /// <summary>What our first HTLC carries, in msat.</summary>
    [Key(2)] public ulong AmountMsat { get; init; }

    /// <summary>The fees of the whole route, in msat.</summary>
    [Key(3)] public ulong FeeMsat { get; init; }

    /// <summary>Our first HTLC's <c>cltv_expiry</c>.</summary>
    [Key(4)] public uint CltvExpiry { get; init; }

    /// <summary>The height the CLTVs were computed from.</summary>
    [Key(5)] public uint BlockHeight { get; init; }

    /// <summary>The estimated success probability (0 to 1).</summary>
    [Key(6)] public double Probability { get; init; }

    /// <summary>Which candidate was chosen (direct, route hint or graph).</summary>
    [Key(7)] public required string Description { get; init; }

    public static GetRouteIpcResponse FromClientResponse(GetRouteClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new GetRouteIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            Hops = clientResponse.Hops.Select(h => new GetRouteHopIpcInfo
            {
                NodeId = h.NodeId,
                ShortChannelId = ListGraphChannelsIpcResponse.ToNumber(h.ShortChannelId),
                AmountMsat = h.Amount.MilliSatoshi,
                CltvExpiry = h.CltvExpiry,
                FeeMsat = h.Fee.MilliSatoshi
            }).ToList(),
            AmountMsat = clientResponse.Amount.MilliSatoshi,
            FeeMsat = clientResponse.Fee.MilliSatoshi,
            CltvExpiry = clientResponse.CltvExpiry,
            BlockHeight = clientResponse.BlockHeight,
            Probability = clientResponse.Probability,
            Description = clientResponse.Description
        };
    }
}

/// <summary>One hop of a <see cref="GetRouteIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class GetRouteHopIpcInfo
{
    /// <summary>The node.</summary>
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>The channel its HTLC arrives on (the 8-byte short channel id as a number).</summary>
    [Key(1)] public ulong ShortChannelId { get; init; }

    /// <summary>What the HTLC carries, in msat.</summary>
    [Key(2)] public ulong AmountMsat { get; init; }

    /// <summary>The HTLC's <c>cltv_expiry</c>.</summary>
    [Key(3)] public uint CltvExpiry { get; init; }

    /// <summary>What the node keeps for forwarding, in msat (0 for the destination).</summary>
    [Key(4)] public ulong FeeMsat { get; init; }
}