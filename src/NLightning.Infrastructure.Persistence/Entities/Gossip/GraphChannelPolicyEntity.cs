namespace NLightning.Infrastructure.Persistence.Entities.Gossip;

using Domain.Channels.ValueObjects;

/// <summary>
/// The latest BOLT 7 <c>channel_update</c> (or taproot gossip <c>channel_update_2</c>) of one direction of a graph
/// channel (migration <c>AddGossipGraph</c>, BOLT 7 plan G2-T3). Keyed by (short channel id, direction, version);
/// cascades from <see cref="GraphChannelEntity"/> without a
/// navigation.
/// </summary>
public class GraphChannelPolicyEntity
{
    public required ShortChannelId ShortChannelId { get; set; }

    /// <summary>0 = sent by <c>node_id_1</c>, 1 = by <c>node_id_2</c>.</summary>
    public required byte Direction { get; set; }

    public required uint Timestamp { get; set; }
    public required byte MessageFlags { get; set; }
    public required byte ChannelFlags { get; set; }
    public required ushort CltvExpiryDelta { get; set; }
    public required ulong HtlcMinimumMsat { get; set; }
    public required ulong HtlcMaximumMsat { get; set; }
    public required uint FeeBaseMsat { get; set; }
    public required uint FeePpm { get; set; }

    /// <summary>The whole payload (signature included, message type excluded).</summary>
    public required byte[] RawUpdate { get; set; }

    /// <summary>
    /// 1 = <c>channel_update</c>, 2 = <c>channel_update_2</c> (its <see cref="Timestamp"/> a block height, its
    /// <see cref="HtlcMaximumMsat"/> the resolved value); part of the key (migration <c>AddGossipV2</c>, NL-878).
    /// </summary>
    public byte Version { get; set; } = 1;

    /// <summary>The <c>channel_update_2</c> inbound base fee (0 for v1).</summary>
    public uint InboundFeeBaseMsat { get; set; }

    /// <summary>The <c>channel_update_2</c> inbound proportional fee (0 for v1).</summary>
    public uint InboundFeePpm { get; set; }

    internal GraphChannelPolicyEntity()
    {
    }
}