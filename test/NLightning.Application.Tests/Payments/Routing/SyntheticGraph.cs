using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Protocol.Payloads;

/// <summary>
/// Builds small gossip graphs for the routing tests: channels with the policy of each direction given as
/// (fee base, fee ppm, CLTV delta, htlc max), node ids ordered as BOLT 7 wants.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SyntheticGraph
{
    private readonly List<GraphChannel> _channels = [];

    /// <summary>The timestamp of every policy.</summary>
    public uint Timestamp { get; init; } = 1_700_000_000;

    /// <summary>
    /// Adds a channel of <paramref name="capacitySat"/> between <paramref name="a"/> and <paramref name="b"/>;
    /// <paramref name="aToB"/> is <paramref name="a"/>'s policy (towards <paramref name="b"/>), null for none.
    /// </summary>
    public SyntheticGraph Channel(ShortChannelId scid, CompactPubKey a, CompactPubKey b, ulong capacitySat,
                                  Policy? aToB, Policy? bToA)
    {
        var aIsNode1 = GraphChannel.CompareNodeIds(a, b) < 0;
        var (node1, node2) = aIsNode1 ? (a, b) : (b, a);
        var channel = new GraphChannel(scid, node1, node2, node1, node2, capacitySat);
        if (aToB is { } forward)
            channel = channel.WithPolicy(ToPolicy(forward, aIsNode1 ? (byte)0 : (byte)1));
        if (bToA is { } backward)
            channel = channel.WithPolicy(ToPolicy(backward, aIsNode1 ? (byte)1 : (byte)0));
        _channels.Add(channel);
        return this;
    }

    public GraphSnapshot Build() => new(_channels, []);

    private GraphPolicy ToPolicy(Policy policy, byte direction) =>
        new(Timestamp, ChannelUpdatePayload.MessageFlagMustBeOne,
            (byte)(direction | (policy.Disabled ? ChannelUpdatePayload.ChannelFlagDisable : 0)), policy.CltvDelta,
            policy.HtlcMinimumMsat, policy.HtlcMaximumMsat, policy.FeeBaseMsat, policy.FeePpm);

    /// <summary>One direction's policy.</summary>
    internal readonly record struct Policy(
        uint FeeBaseMsat,
        uint FeePpm,
        ushort CltvDelta,
        ulong HtlcMaximumMsat = 5_000_000,
        ulong HtlcMinimumMsat = 1,
        bool Disabled = false);
}