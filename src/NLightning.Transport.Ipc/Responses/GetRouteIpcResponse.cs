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

    /// <summary>The trampoline layer of a quote through a trampoline node (NL-940); null for our own route.</summary>
    [Key(8)] public GetRouteTrampolineIpcInfo? Trampoline { get; init; }

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
            Description = clientResponse.Description,
            Trampoline = clientResponse.Trampoline is { } t
                             ? new GetRouteTrampolineIpcInfo
                             {
                                 TrampolineNode = t.TrampolineNode,
                                 Payee = t.Payee,
                                 AmountMsat = t.Amount.MilliSatoshi,
                                 PayeeCltvExpiry = t.PayeeCltvExpiry,
                                 FeeBaseMsat = t.FeeBaseMsat,
                                 FeeProportionalMillionths = t.FeeProportionalMillionths,
                                 CltvExpiryDelta = t.CltvExpiryDelta,
                                 FeeMsat = t.Fee.MilliSatoshi,
                                 PolicyLearnt = t.PolicyLearnt
                             }
                             : null
        };
    }
}

/// <summary>The trampoline layer of a <see cref="GetRouteIpcResponse"/> (NL-940).</summary>
[MessagePackObject]
public sealed class GetRouteTrampolineIpcInfo
{
    /// <summary>The trampoline node the outer route reaches.</summary>
    [Key(0)] public required CompactPubKey TrampolineNode { get; init; }

    /// <summary>The payee behind it (the request's destination).</summary>
    [Key(1)] public required CompactPubKey Payee { get; init; }

    /// <summary>What the payee must receive, in msat.</summary>
    [Key(2)] public ulong AmountMsat { get; init; }

    /// <summary>The payee's absolute <c>outgoing_cltv_value</c>.</summary>
    [Key(3)] public uint PayeeCltvExpiry { get; init; }

    /// <summary>The policy's <c>fee_base_msat</c>.</summary>
    [Key(4)] public uint FeeBaseMsat { get; init; }

    /// <summary>The policy's <c>fee_proportional_millionths</c>.</summary>
    [Key(5)] public uint FeeProportionalMillionths { get; init; }

    /// <summary>The policy's <c>cltv_expiry_delta</c>.</summary>
    [Key(6)] public ushort CltvExpiryDelta { get; init; }

    /// <summary>What the node keeps, in msat.</summary>
    [Key(7)] public ulong FeeMsat { get; init; }

    /// <summary>Whether the policy is one the node told us, or the send options' default.</summary>
    [Key(8)] public bool PolicyLearnt { get; init; }
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