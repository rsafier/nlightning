using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for SetChannelPolicy (ClientCommand 35, wave sp1 lane SP1-G). The channel is named by
/// <see cref="ChannelId"/> or by <see cref="ShortChannelId"/> (a BOLT 7 uint64), exactly one.
/// </summary>
[MessagePackObject]
public sealed class SetChannelPolicyIpcRequest
{
    [Key(0)] public ChannelId? ChannelId { get; init; }
    [Key(1)] public ulong? ShortChannelId { get; init; }
    [Key(2)] public uint? FeeBaseMsat { get; init; }
    [Key(3)] public uint? FeeProportionalMillionths { get; init; }
    [Key(4)] public ushort? CltvExpiryDelta { get; init; }
    [Key(5)] public ulong? HtlcMinimumMsat { get; init; }
    [Key(6)] public ulong? HtlcMaximumMsat { get; init; }

    /// <summary>Remove the channel's override (no value may be set with it).</summary>
    [Key(7)] public bool Reset { get; init; }

    public SetChannelPolicyClientRequest ToClientRequest() =>
        new(ChannelPolicyIpcReference.ToClientReference(ChannelId, ShortChannelId))
        {
            FeeBaseMsat = FeeBaseMsat,
            FeeProportionalMillionths = FeeProportionalMillionths,
            CltvExpiryDelta = CltvExpiryDelta,
            HtlcMinimumMsat = HtlcMinimumMsat,
            HtlcMaximumMsat = HtlcMaximumMsat,
            Reset = Reset
        };
}

/// <summary>
/// Request for GetChannelPolicy (ClientCommand 36, wave sp1 lane SP1-G): the channel by <see cref="ChannelId"/> or by
/// <see cref="ShortChannelId"/>, exactly one.
/// </summary>
[MessagePackObject]
public sealed class GetChannelPolicyIpcRequest
{
    [Key(0)] public ChannelId? ChannelId { get; init; }
    [Key(1)] public ulong? ShortChannelId { get; init; }

    public GetChannelPolicyClientRequest ToClientRequest() =>
        new(ChannelPolicyIpcReference.ToClientReference(ChannelId, ShortChannelId));
}

internal static class ChannelPolicyIpcReference
{
    /// <exception cref="ArgumentException">Both or neither are set.</exception>
    internal static ChannelReference ToClientReference(ChannelId? channelId, ulong? shortChannelId) =>
        (channelId, shortChannelId) switch
        {
            ({ } id, null) => new ChannelReference(id),
            (null, { } scid) => new ChannelReference(new ShortChannelId(scid)),
            _ => throw new ArgumentException("Name the channel by its channel id or by its short channel id, "
                                           + "exactly one.")
        };
}