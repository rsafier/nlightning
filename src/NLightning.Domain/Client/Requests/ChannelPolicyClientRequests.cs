namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

/// <summary>
/// Names one channel of a <c>setchannelpolicy</c>/<c>getchannelpolicy</c> request: by channel id or by short channel id
/// (the real one or one of its aliases), exactly one of them.
/// </summary>
public sealed class ChannelReference
{
    public ChannelReference(ChannelId channelId)
    {
        ChannelId = channelId;
    }

    public ChannelReference(ShortChannelId shortChannelId)
    {
        ShortChannelId = shortChannelId;
    }

    public ChannelId? ChannelId { get; }
    public ShortChannelId? ShortChannelId { get; }

    public override string ToString() => ChannelId?.ToString() ?? ShortChannelId?.ToString() ?? "-";
}

/// <summary>
/// Sets or resets a channel's routing policy (<c>ClientCommand.SetChannelPolicy</c>, wave sp1 lane SP1-G). Null values
/// keep the stored ones (or the node-wide <c>Node:Routing</c> values); <see cref="Reset"/> removes the channel's
/// override instead and allows no value.
/// </summary>
public sealed class SetChannelPolicyClientRequest
{
    public SetChannelPolicyClientRequest(ChannelReference channel)
    {
        Channel = channel;
    }

    public ChannelReference Channel { get; }

    /// <summary>BOLT 7 <c>fee_base_msat</c> (u32).</summary>
    public uint? FeeBaseMsat { get; init; }

    /// <summary>BOLT 7 <c>fee_proportional_millionths</c> (u32).</summary>
    public uint? FeeProportionalMillionths { get; init; }

    /// <summary>BOLT 7 <c>cltv_expiry_delta</c> (u16).</summary>
    public ushort? CltvExpiryDelta { get; init; }

    /// <summary>BOLT 7 <c>htlc_minimum_msat</c> (u64).</summary>
    public ulong? HtlcMinimumMsat { get; init; }

    /// <summary>BOLT 7 <c>htlc_maximum_msat</c> (u64, at most the capacity).</summary>
    public ulong? HtlcMaximumMsat { get; init; }

    /// <summary>Remove the channel's override: the node-wide values apply again.</summary>
    public bool Reset { get; init; }

    /// <summary>Whether the request sets at least one value.</summary>
    public bool HasValues => FeeBaseMsat is not null || FeeProportionalMillionths is not null
                          || CltvExpiryDelta is not null || HtlcMinimumMsat is not null || HtlcMaximumMsat is not null;
}

/// <summary>
/// Reads a channel's routing policy in force (<c>ClientCommand.GetChannelPolicy</c>, wave sp1 lane SP1-G).
/// </summary>
public sealed class GetChannelPolicyClientRequest
{
    public GetChannelPolicyClientRequest(ChannelReference channel)
    {
        Channel = channel;
    }

    public ChannelReference Channel { get; }
}