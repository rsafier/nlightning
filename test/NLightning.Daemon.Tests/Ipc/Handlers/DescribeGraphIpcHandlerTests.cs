using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Gossip.Graph;
using Application.Gossip.Sync;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// BOLT 7 G5-T4: describegraph (ClientCommand 20) over IPC, from a real graph store, ingress and sync manager through
/// the daemon's client handler and the MessagePack contract, with its paged listings.
/// </summary>
public class DescribeGraphIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_alice = PubKey(0x02, 1);
    private static readonly CompactPubKey s_bob = PubKey(0x02, 2);
    private static readonly CompactPubKey s_carol = PubKey(0x03, 3);
    private static readonly ShortChannelId s_ab = new(110, 1, 0);
    private static readonly ShortChannelId s_bc = new(105, 7, 1);

    public DescribeGraphIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_AGraph_When_DescribeGraph_Then_TheSummaryCrossesTheWire()
    {
        // Arrange
        var store = CreateGraph();
        using var provider = BuildProvider(store);
        var handler = new DescribeGraphIpcHandler(NullLogger<DescribeGraphIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(ClientCommand.DescribeGraph, response.Command);
        var payload = Read(response);
        Assert.False(payload.IsLoaded);
        Assert.Equal(2, payload.Channels);
        Assert.Equal(1, payload.SpentChannels);
        Assert.Equal(1, payload.UnverifiedChannels);
        Assert.Equal(0, payload.OwnChannels);
        Assert.Equal(1, payload.ChannelsWithoutPolicy);
        Assert.Equal(2, payload.Policies);
        Assert.Equal(1, payload.DisabledPolicies);
        Assert.Equal(2, payload.AnnouncedNodes);
        Assert.Equal(3, payload.GraphNodes);
        Assert.Equal(0UL, payload.CapacitySat); // ab is spent and bc unverified: neither counts
        Assert.Equal(store.PendingChanges, payload.PendingWrites);
        Assert.Equal(store.GetMemoryEstimate().StoreBytes, payload.EstimatedStoreBytes);
        Assert.Equal(store.GetMemoryEstimate().SnapshotBytes, payload.EstimatedSnapshotBytes);
        Assert.Equal(0, payload.IngressQueued);
        Assert.Equal(0L, payload.IngressDropped);
        Assert.Equal(0, payload.Orphans);
        Assert.False(payload.HasCompletedInitialSync);
        Assert.Empty(payload.Peers);
        Assert.Empty(payload.ChannelPage);
        Assert.Empty(payload.NodePage);
        Assert.Null(payload.NextChannelOffset);
        Assert.Null(payload.NextNodeOffset);
    }

    [Fact]
    public async Task Given_PagedRequests_When_DescribeGraph_Then_EachPageAndTheNextOffsetCrossTheWire()
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new DescribeGraphIpcHandler(NullLogger<DescribeGraphIpcHandler>.Instance, provider);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = Read(await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeChannels = true,
            IncludeNodes = true,
            Limit = 1
        }), ct));
        var secondChannels = Read(await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeChannels = true,
            Offset = first.NextChannelOffset!.Value,
            Limit = 1
        }), ct));
        var secondNodes = Read(await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeNodes = true,
            Offset = first.NextNodeOffset!.Value,
            Limit = 1
        }), ct));
        var beyond = Read(await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeChannels = true,
            Offset = 5
        }), ct));

        // Assert: channels by short channel id, nodes by node id
        Assert.Equal(ToNumber(s_bc), Assert.Single(first.ChannelPage).ShortChannelId);
        Assert.Equal(s_alice, Assert.Single(first.NodePage).NodeId);
        Assert.Equal(1, first.NextChannelOffset);
        Assert.Equal(1, first.NextNodeOffset);
        var ab = Assert.Single(secondChannels.ChannelPage);
        Assert.Empty(secondChannels.NodePage);
        Assert.Empty(secondNodes.ChannelPage);
        Assert.Equal(ToNumber(s_ab), ab.ShortChannelId);
        Assert.Equal(250U, ab.SpentAtHeight);
        Assert.True(ab.Policy2!.IsDisabled);
        var carol = Assert.Single(secondNodes.NodePage);
        Assert.Equal(s_carol, carol.NodeId);
        Assert.Equal(1, carol.ChannelCount);
        Assert.Null(secondChannels.NextChannelOffset);
        Assert.Null(secondNodes.NextNodeOffset);
        Assert.Empty(beyond.ChannelPage);
        Assert.Null(beyond.NextChannelOffset);
        Assert.Empty(beyond.NodePage);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    [InlineData(-1, 10)]
    public async Task Given_ALimitOrOffsetOutOfRange_When_DescribeGraph_Then_InvalidOperation(int offset, int limit)
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new DescribeGraphIpcHandler(NullLogger<DescribeGraphIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeChannels = true,
            Offset = offset,
            Limit = limit
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
    }

    [Fact]
    public async Task Given_AnOffsetWithBothListings_When_DescribeGraph_Then_InvalidOperation()
    {
        // Arrange: the two listings end at different offsets, so one offset cannot page both
        using var provider = BuildProvider(CreateGraph());
        var handler = new DescribeGraphIpcHandler(NullLogger<DescribeGraphIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest
        {
            IncludeChannels = true,
            IncludeNodes = true,
            Offset = 1,
            Limit = 1
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not both", error.Message);
    }

    [Fact]
    public async Task Given_TheGraphDisabled_When_DescribeGraph_Then_InvalidOperation()
    {
        // Arrange (plan D12: mainnet without Gossip:Enabled)
        using var provider = BuildProvider(CreateGraph(), "mainnet");
        var handler = new DescribeGraphIpcHandler(NullLogger<DescribeGraphIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new DescribeGraphIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Gossip:Enabled", error.Message);
    }

    [Fact]
    public void Given_AResponseWithSyncPeers_When_RoundTripped_Then_EveryFieldSurvives()
    {
        // Arrange
        var lastSync = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        var clientResponse = new DescribeGraphClientResponse
        {
            IsLoaded = true,
            Channels = 200_000,
            SpentChannels = 3,
            UnverifiedChannels = 4,
            OwnChannels = 5,
            ChannelsWithoutPolicy = 6,
            Policies = 399_000,
            DisabledPolicies = 7,
            AnnouncedNodes = 49_000,
            GraphNodes = 50_000,
            CapacitySat = 123_456_789_000,
            PendingWrites = 8,
            EstimatedStoreBytes = 500_000_000,
            EstimatedSnapshotBytes = 41_000_000,
            IngressQueued = 9,
            IngressDropped = 10,
            Orphans = 11,
            HasCompletedInitialSync = true,
            Peers =
            [
                new GraphPeerSyncInfo(s_bob, true, true, true, false, lastSync, 1_700_000_000, uint.MaxValue,
                                      1_600_000_000, uint.MaxValue, false, 2),
                new GraphPeerSyncInfo(s_carol, false, false, false, false, null, null, null, uint.MaxValue, 0, true, 0)
            ]
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(DescribeGraphIpcResponse.FromClientResponse(clientResponse),
                                                    s_options, TestContext.Current.CancellationToken);
        var payload = MessagePackSerializer.Deserialize<DescribeGraphIpcResponse>(
            bytes, s_options, TestContext.Current.CancellationToken);
        var request = MessagePackSerializer.Deserialize<DescribeGraphIpcRequest>(
            MessagePackSerializer.Serialize(new DescribeGraphIpcRequest { IncludeNodes = true, Offset = 7 },
                                            s_options, TestContext.Current.CancellationToken),
            s_options, TestContext.Current.CancellationToken).ToClientRequest();

        // Assert
        Assert.True(payload.IsLoaded);
        Assert.Equal((200_000, 3, 4, 5, 6, 399_000, 7, 49_000, 50_000),
                     (payload.Channels, payload.SpentChannels, payload.UnverifiedChannels, payload.OwnChannels,
                      payload.ChannelsWithoutPolicy, payload.Policies, payload.DisabledPolicies,
                      payload.AnnouncedNodes, payload.GraphNodes));
        Assert.Equal(123_456_789_000UL, payload.CapacitySat);
        Assert.Equal((8, 500_000_000L, 41_000_000L), (payload.PendingWrites, payload.EstimatedStoreBytes,
                                                      payload.EstimatedSnapshotBytes));
        Assert.Equal((9, 10L, 11, true), (payload.IngressQueued, payload.IngressDropped, payload.Orphans,
                                          payload.HasCompletedInitialSync));
        Assert.Equal(2, payload.Peers.Count);
        var bob = payload.Peers[0];
        Assert.Equal(s_bob, bob.PeerId);
        Assert.True(bob.SupportsQueries && bob.SupportsQueriesEx && bob.IsSyncPeer);
        Assert.Equal((1_600_000_000U, uint.MaxValue), (bob.OurFilterFirstTimestamp!.Value,
                                                       bob.OurFilterTimestampRange!.Value));
        Assert.False(bob.IsRangeSyncRunning || bob.QueryingStopped);
        Assert.Equal(lastSync.ToUnixTimeSeconds(), bob.LastRangeSyncAt);
        Assert.Equal((1_700_000_000U, uint.MaxValue), (bob.PeerFilterFirstTimestamp!.Value,
                                                       bob.PeerFilterTimestampRange!.Value));
        Assert.Equal(2, bob.PendingWork);
        var carol = payload.Peers[1];
        Assert.Null(carol.LastRangeSyncAt);
        Assert.Null(carol.PeerFilterFirstTimestamp);
        Assert.True(carol.QueryingStopped);
        Assert.Equal((uint.MaxValue, 0U), (carol.OurFilterFirstTimestamp!.Value, carol.OurFilterTimestampRange!.Value));
        Assert.Equal((false, true, 7, DescribeGraphClientRequest.DefaultLimit),
                     (request.IncludeChannels, request.IncludeNodes, request.Offset, request.Limit));
    }

    /// <summary>
    /// alice-bob (110x1x0: verified, both policies, the second disabled, spent at 250), bob-carol (105x7x1: unverified,
    /// no policy); alice and carol announced, bob not.
    /// </summary>
    private static GraphStore CreateGraph()
    {
        var store = new GraphStore(new Mock<IServiceScopeFactory>().Object, NullLogger<GraphStore>.Instance);
        Assert.True(store.TryAddChannel(new GraphChannel(s_ab, s_alice, s_bob, PubKey(0x02, 11), PubKey(0x02, 12),
                                                         1_000_000)));
        Assert.True(store.TryAddChannel(new GraphChannel(s_bc, s_bob, s_carol, PubKey(0x02, 12), PubKey(0x03, 13),
                                                         null, default, GraphChannelVerification.Unverified)));
        Assert.True(store.TryApplyPolicy(s_ab, new GraphPolicy(1_700_000_100, 1, 0, 40, 1_000, 990_000_000, 1_000,
                                                               100)));
        Assert.True(store.TryApplyPolicy(s_ab, new GraphPolicy(1_700_000_200, 1, 3, 80, 1, 990_000_000, 0, 1)));
        Assert.True(store.MarkSpent(s_ab, 250));
        Assert.True(store.TryApplyNode(new GraphNode(s_carol, 1_700_000_000, default, new byte[32], [1, 2, 3])));
        Assert.True(store.TryApplyNode(new GraphNode(s_alice, 1_700_000_000, default, new byte[32], [4, 5, 6])));
        return store;
    }

    private static ServiceProvider BuildProvider(GraphStore store, string network = "regtest")
    {
        var nodeOptions = Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) });
        var graphOptions = Options.Create(new GossipGraphOptions());
        var ingress = new GossipIngress(store, new Mock<IGossipSignatureVerifier>().Object,
                                        new Mock<IFundingOutputLookup>().Object, graphOptions, nodeOptions,
                                        NullLogger<GossipIngress>.Instance);
        var sync = new GossipSyncManager(store, Options.Create(new GossipSyncOptions()), nodeOptions,
                                         NullLogger<GossipSyncManager>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(graphOptions);
        services.AddSingleton(nodeOptions);
        services.AddSingleton(new GossipGraphDescriber(store, ingress, sync));
        services.AddScoped<IClientCommandHandler<DescribeGraphClientRequest, DescribeGraphClientResponse>,
            DescribeGraphClientHandler>();
        return services.BuildServiceProvider();
    }

    private static IpcEnvelope Envelope(DescribeGraphIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = ClientCommand.DescribeGraph,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

    private static DescribeGraphIpcResponse Read(IpcEnvelope envelope)
    {
        Assert.Equal(IpcEnvelopeKind.Response, envelope.Kind);
        return MessagePackSerializer.Deserialize<DescribeGraphIpcResponse>(envelope.Payload, s_options,
                                                                           TestContext.Current.CancellationToken);
    }

    private static ulong ToNumber(ShortChannelId shortChannelId) =>
        ListGraphChannelsIpcResponse.ToNumber(shortChannelId);

    private static CompactPubKey PubKey(byte prefix, byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = prefix;
        return new CompactPubKey(bytes);
    }
}