using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.Payloads;

/// <summary>
/// Response for ListGraphChannels (ClientCommand 18): the channels of the gossip graph, ordered by short channel id.
/// </summary>
[MessagePackObject]
public sealed class ListGraphChannelsIpcResponse
{
    [Key(0)] public required List<GraphChannelIpcInfo> Channels { get; init; }

    public static ListGraphChannelsIpcResponse FromClientResponse(ListGraphChannelsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListGraphChannelsIpcResponse
        {
            Channels = clientResponse.Channels.Select(c => new GraphChannelIpcInfo
            {
                ShortChannelId = ToNumber(c.ShortChannelId),
                NodeId1 = c.NodeId1,
                NodeId2 = c.NodeId2,
                CapacitySat = c.CapacitySat,
                Verification = c.Verification.ToString(),
                SpentAtHeight = c.SpentAtHeight,
                Features = Convert.ToHexStringLower(c.Features.Span),
                Policy1 = ToInfo(c.Policy1),
                Policy2 = ToInfo(c.Policy2)
            }).ToList()
        };
    }

    /// <summary>The 8-byte short channel id as a number (block height, transaction index, output index).</summary>
    public static ulong ToNumber(ShortChannelId shortChannelId) =>
        ((ulong)shortChannelId.BlockHeight << 40) | ((ulong)shortChannelId.TransactionIndex << 16)
                                                  | shortChannelId.OutputIndex;

    private static GraphPolicyIpcInfo? ToInfo(GraphPolicy? policy) =>
        policy is null
            ? null
            : new GraphPolicyIpcInfo
            {
                Timestamp = policy.Timestamp,
                MessageFlags = policy.MessageFlags,
                ChannelFlags = policy.ChannelFlags,
                CltvExpiryDelta = policy.CltvExpiryDelta,
                HtlcMinimumMsat = policy.HtlcMinimumMsat,
                HtlcMaximumMsat = policy.HtlcMaximumMsat,
                FeeBaseMsat = policy.FeeBaseMsat,
                FeeProportionalMillionths = policy.FeeProportionalMillionths
            };
}

/// <summary>One channel of a <see cref="ListGraphChannelsIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class GraphChannelIpcInfo
{
    /// <summary>The 8-byte short channel id as a number.</summary>
    [Key(0)] public ulong ShortChannelId { get; init; }

    [Key(1)] public required CompactPubKey NodeId1 { get; init; }
    [Key(2)] public required CompactPubKey NodeId2 { get; init; }

    /// <summary>The funding output's amount; null for an unverified channel.</summary>
    [Key(3)] public ulong? CapacitySat { get; init; }

    /// <summary><c>Verified</c>, <c>Unverified</c> or <c>Own</c>.</summary>
    [Key(4)] public required string Verification { get; init; }

    /// <summary>The block that spent the funding output; the channel is removed 72 blocks later.</summary>
    [Key(5)] public uint? SpentAtHeight { get; init; }

    /// <summary>The announced feature bits, hex in wire order.</summary>
    [Key(6)] public required string Features { get; init; }

    /// <summary>The policy of direction 0 (node 1 forwarding towards node 2), when known.</summary>
    [Key(7)] public GraphPolicyIpcInfo? Policy1 { get; init; }

    /// <summary>The policy of direction 1 (node 2 forwarding towards node 1), when known.</summary>
    [Key(8)] public GraphPolicyIpcInfo? Policy2 { get; init; }
}

/// <summary>One direction's <c>channel_update</c> of a <see cref="GraphChannelIpcInfo"/>.</summary>
[MessagePackObject]
public sealed class GraphPolicyIpcInfo
{
    [Key(0)] public uint Timestamp { get; init; }
    [Key(1)] public byte MessageFlags { get; init; }
    [Key(2)] public byte ChannelFlags { get; init; }
    [Key(3)] public ushort CltvExpiryDelta { get; init; }
    [Key(4)] public ulong HtlcMinimumMsat { get; init; }
    [Key(5)] public ulong HtlcMaximumMsat { get; init; }
    [Key(6)] public uint FeeBaseMsat { get; init; }
    [Key(7)] public uint FeeProportionalMillionths { get; init; }

    /// <summary>The <c>disable</c> bit of <see cref="ChannelFlags"/>.</summary>
    [IgnoreMember] public bool IsDisabled => (ChannelFlags & ChannelUpdatePayload.ChannelFlagDisable) != 0;
}