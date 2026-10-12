using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for SetChannelPolicy and GetChannelPolicy (ClientCommand 35/36, wave sp1 lane SP1-G): a channel's routing
/// policy in force and which of its values come from the channel's override (the others are <c>Node:Routing</c>'s).
/// </summary>
[MessagePackObject]
public sealed class ChannelPolicyIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The real short channel id as a BOLT 7 uint64, or null before the funding confirmed.</summary>
    [Key(1)] public ulong? ShortChannelId { get; init; }

    [Key(2)] public uint FeeBaseMsat { get; init; }
    [Key(3)] public uint FeeProportionalMillionths { get; init; }
    [Key(4)] public ushort CltvExpiryDelta { get; init; }
    [Key(5)] public ulong HtlcMinimumMsat { get; init; }
    [Key(6)] public ulong HtlcMaximumMsat { get; init; }
    [Key(7)] public bool IsFeeBaseMsatOverridden { get; init; }
    [Key(8)] public bool IsFeeProportionalMillionthsOverridden { get; init; }
    [Key(9)] public bool IsCltvExpiryDeltaOverridden { get; init; }
    [Key(10)] public bool IsHtlcMinimumMsatOverridden { get; init; }
    [Key(11)] public bool IsHtlcMaximumMsatOverridden { get; init; }

    /// <summary>When the override was last written (UNIX seconds), or null without an override.</summary>
    [Key(12)] public long? OverrideUpdatedAt { get; init; }

    /// <summary>True when the request reset the channel to the node-wide values.</summary>
    [Key(13)] public bool WasReset { get; init; }

    /// <summary>True when the node keeps overrides in memory only (forgotten on restart).</summary>
    [Key(14)] public bool IsMemoryOnly { get; init; }

    public static ChannelPolicyIpcResponse FromClientResponse(ChannelPolicyClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var policy = response.Policy;
        return new ChannelPolicyIpcResponse
        {
            ChannelId = policy.ChannelId,
            ShortChannelId = response.ShortChannelId is { } scid
                                 ? ((ulong)scid.BlockHeight << 40) | ((ulong)scid.TransactionIndex << 16)
                                                                   | scid.OutputIndex
                                 : null,
            FeeBaseMsat = policy.FeeBaseMsat,
            FeeProportionalMillionths = policy.FeeProportionalMillionths,
            CltvExpiryDelta = policy.CltvExpiryDelta,
            HtlcMinimumMsat = policy.HtlcMinimumMsat,
            HtlcMaximumMsat = policy.HtlcMaximumMsat,
            IsFeeBaseMsatOverridden = policy.IsFeeBaseMsatOverridden,
            IsFeeProportionalMillionthsOverridden = policy.IsFeeProportionalMillionthsOverridden,
            IsCltvExpiryDeltaOverridden = policy.IsCltvExpiryDeltaOverridden,
            IsHtlcMinimumMsatOverridden = policy.IsHtlcMinimumMsatOverridden,
            IsHtlcMaximumMsatOverridden = policy.IsHtlcMaximumMsatOverridden,
            OverrideUpdatedAt = policy.Override is { } policyOverride && policyOverride.UpdatedAt != default
                                    ? policyOverride.UpdatedAt.ToUnixTimeSeconds()
                                    : null,
            WasReset = response.WasReset,
            IsMemoryOnly = !response.IsPersisted
        };
    }
}