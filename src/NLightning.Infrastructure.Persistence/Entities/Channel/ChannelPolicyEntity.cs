// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// The routing policy override of one channel (wave sp1 lane SP1-G, <c>ChannelPolicyOverride</c>; migration
/// <c>AddSpliceFundings</c>). A null value means the node-wide <c>Node:Routing</c> value. No foreign key to
/// <c>Channels</c>: the row is the operator's setting and may be written before the channel row is.
/// </summary>
public class ChannelPolicyEntity
{
    public required ChannelId ChannelId { get; set; }

    public uint? FeeBaseMsat { get; set; }

    public uint? FeeProportionalMillionths { get; set; }

    public ushort? CltvExpiryDelta { get; set; }

    public ulong? HtlcMinimumMsat { get; set; }

    public ulong? HtlcMaximumMsat { get; set; }

    /// <summary>When the override was last written (UTC ticks).</summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    // Default constructor for EF Core
    internal ChannelPolicyEntity() { }
}