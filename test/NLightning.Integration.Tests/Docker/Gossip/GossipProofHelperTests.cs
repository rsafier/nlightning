using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Container-free tests of the helpers the BOLT 7 Docker proofs assert through: <see cref="GossipWire"/> reads the
/// same fields as the node's own serializer on the captured LND and CLN messages, and
/// <see cref="GossipTestNodes.CheckOption"/> reports a bound option that lacks the configured value.
/// </summary>
public class GossipProofHelperTests
{
    private readonly IMessageSerializer _serializer;

    public GossipProofHelperTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializationInfrastructureServices();
        _serializer = services.BuildServiceProvider().GetRequiredService<IMessageSerializer>();
    }

    [Fact]
    public async Task Given_CapturedGossip_When_Summarized_Then_FieldsMatchTheParsedMessages()
    {
        // Arrange
        var vectors = Bolt7Vectors.All.Where(v => v.Type is 256 or 257 or 258).ToList();
        var expectedChannels = new HashSet<ulong>();
        var expectedNodes = new HashSet<string>();
        var expectedUpdates = new HashSet<(ulong, int)>();
        foreach (var vector in vectors)
        {
            using var stream = new MemoryStream(vector.Wire);
            switch (await _serializer.DeserializeMessageAsync(stream))
            {
                case ChannelAnnouncementMessage announcement:
                    expectedChannels.Add(ToUInt64(announcement.Payload.ShortChannelId));
                    break;
                case NodeAnnouncementMessage node:
                    expectedNodes.Add(Convert.ToHexStringLower((byte[])node.Payload.NodeId));
                    break;
                case ChannelUpdateMessage update:
                    expectedUpdates.Add((ToUInt64(update.Payload.ShortChannelId), update.Payload.Direction ? 1 : 0));
                    break;
            }
        }

        // Act
        var summary = GossipWire.Summarize(vectors.Select(v => v.Wire));

        // Assert
        Assert.NotEmpty(expectedChannels);
        Assert.NotEmpty(expectedNodes);
        Assert.NotEmpty(expectedUpdates);
        Assert.Equal(expectedChannels, summary.AnnouncedChannels);
        Assert.Equal(expectedNodes, summary.AnnouncedNodes);
        Assert.Equal(expectedUpdates, summary.Updates);
    }

    [Fact]
    public void Given_OneDirectionAndNoNodes_When_Missing_Then_TheOtherDirectionAndTheNodeAreReported()
    {
        // Arrange
        var nodeId = new string('a', 66);
        var summary = new GossipWire.GossipSummary(new HashSet<ulong> { 7 }, new HashSet<string>(),
                                                   new HashSet<(ulong, int)> { (7, 0) });

        // Act
        var missing = GossipWire.Missing(summary, [7], [nodeId]);

        // Assert
        Assert.Equal(["channel_update 7/1", $"node_announcement {nodeId[..16]}…"], missing);
    }

    [Fact]
    public void Given_EverythingReceived_When_Missing_Then_Empty()
    {
        // Arrange
        var nodeId = new string('b', 66);
        var summary = new GossipWire.GossipSummary(new HashSet<ulong> { 9 }, new HashSet<string> { nodeId },
                                                   new HashSet<(ulong, int)> { (9, 0), (9, 1) });

        // Act
        var missing = GossipWire.Missing(summary, [9], [nodeId]);

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void Given_ShortWire_When_Summarized_Then_Skipped()
    {
        // Arrange: a channel_announcement cut inside its signatures, and a lone type byte
        byte[][] wires = [[0x01, 0x00, 0x00], [0x01]];

        // Act
        var summary = GossipWire.Summarize(wires);

        // Assert
        Assert.Empty(summary.AnnouncedChannels);
        Assert.Empty(summary.AnnouncedNodes);
        Assert.Empty(summary.Updates);
    }

    [Fact]
    public void Given_FlagBoundFalse_When_Checked_Then_Mismatch()
    {
        // Arrange
        var mismatches = new List<string>();

        // Act
        GossipTestNodes.CheckOption(new FakeGossipOptions { SyncEnabled = false }, "SyncEnabled", true, mismatches);

        // Assert
        Assert.Single(mismatches);
    }

    [Fact]
    public void Given_BoundAsConfigured_When_Checked_Then_NoMismatch()
    {
        // Arrange
        var mismatches = new List<string>();
        var options = new FakeGossipOptions
        {
            SyncEnabled = true,
            Color = "1F7A4D",
            ColorBytes = [0x1f, 0x7a, 0x4d],
            Alias = "nltg-g0",
            AliasBytes = [.. "nltg-g0"u8, .. new byte[25]]
        };

        // Act
        GossipTestNodes.CheckOption(options, "SyncEnabled", true, mismatches);
        GossipTestNodes.CheckOption(options, "Color", GossipTestNodes.Color, mismatches);
        GossipTestNodes.CheckOption(options, "ColorBytes", GossipTestNodes.Color, mismatches);
        GossipTestNodes.CheckOption(options, "Alias", "nltg-g0", mismatches);
        GossipTestNodes.CheckOption(options, "AliasBytes", "nltg-g0", mismatches);

        // Assert
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Given_WrongColorOrAlias_When_Checked_Then_Mismatch()
    {
        // Arrange
        var mismatches = new List<string>();
        var options = new FakeGossipOptions { Color = "#3399ff", Alias = "other" };

        // Act
        GossipTestNodes.CheckOption(options, "Color", GossipTestNodes.Color, mismatches);
        GossipTestNodes.CheckOption(options, "Alias", "nltg-g0", mismatches);

        // Assert
        Assert.Equal(2, mismatches.Count);
    }

    [Fact]
    public void Given_PropertyNotInBuild_When_Checked_Then_Skipped()
    {
        // Arrange
        var mismatches = new List<string>();

        // Act
        GossipTestNodes.CheckOption(new FakeGossipOptions(), "RelayEnabled", true, mismatches);

        // Assert
        Assert.Empty(mismatches);
    }

    private static ulong ToUInt64(ShortChannelId scid) => BinaryPrimitives.ReadUInt64BigEndian((byte[])scid);

    private sealed class FakeGossipOptions
    {
        public bool SyncEnabled { get; init; }
        public string? Color { get; init; }
        public byte[]? ColorBytes { get; init; }
        public string? Alias { get; init; }
        public byte[]? AliasBytes { get; init; }
    }
}