using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// BOLT 7 G2-T6: listnodes (ClientCommand 17) and listgraphchannels (18) over IPC, from a real graph store through
/// the daemon's client handlers and the MessagePack contract.
/// </summary>
public class GraphIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_alice = PubKey(0x02, 1);
    private static readonly CompactPubKey s_bob = PubKey(0x02, 2);
    private static readonly CompactPubKey s_carol = PubKey(0x03, 3);
    private static readonly ShortChannelId s_ab = new(110, 1, 0);
    private static readonly ShortChannelId s_bc = new(105, 7, 1);

    public GraphIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_AGraph_When_ListNodes_Then_EveryAnnouncedNodeCrossesTheWireOrderedWithItsChannelCount()
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new ListNodesIpcHandler(NullLogger<ListNodesIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(ClientCommand.ListNodes, new ListNodesIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(ClientCommand.ListNodes, response.Command);
        var payload = MessagePackSerializer.Deserialize<ListNodesIpcResponse>(response.Payload, s_options,
                                                                              TestContext.Current.CancellationToken);
        Assert.Equal([s_alice, s_carol], payload.Nodes.Select(n => n.NodeId));
        var alice = payload.Nodes[0];
        Assert.Equal("alice", alice.Alias);
        Assert.Equal("#0a0b0c", alice.Color);
        Assert.Equal(["127.0.0.1:9735", "[::1]:9736"], alice.Addresses);
        Assert.Equal("0102", alice.Features);
        Assert.Equal(1_700_000_000U, alice.Timestamp);
        Assert.Equal(1, alice.ChannelCount);
        Assert.Equal(1, payload.Nodes[1].ChannelCount);
        Assert.Empty(payload.Nodes[1].Addresses);
    }

    [Fact]
    public async Task Given_ANodeFilter_When_ListNodes_Then_OnlyThatNode()
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new ListNodesIpcHandler(NullLogger<ListNodesIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(ClientCommand.ListNodes,
                                                          new ListNodesIpcRequest { NodeId = s_carol }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<ListNodesIpcResponse>(response.Payload, s_options,
                                                                              TestContext.Current.CancellationToken);
        var node = Assert.Single(payload.Nodes);
        Assert.Equal(s_carol, node.NodeId);
        Assert.Equal("carol", node.Alias);
    }

    [Fact]
    public async Task Given_AGraph_When_ListGraphChannels_Then_PoliciesSpendAndVerificationCrossTheWireOrderedByScid()
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new ListGraphChannelsIpcHandler(NullLogger<ListGraphChannelsIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(ClientCommand.ListGraphChannels,
                                                          new ListGraphChannelsIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ListGraphChannelsIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal([ToNumber(s_bc), ToNumber(s_ab)], payload.Channels.Select(c => c.ShortChannelId));
        var ab = payload.Channels[1];
        Assert.Equal(s_alice, ab.NodeId1);
        Assert.Equal(s_bob, ab.NodeId2);
        Assert.Equal(1_000_000UL, ab.CapacitySat);
        Assert.Equal("Verified", ab.Verification);
        Assert.Equal(250U, ab.SpentAtHeight);
        Assert.Equal("", ab.Features);
        Assert.NotNull(ab.Policy1);
        Assert.Equal(1_700_000_100U, ab.Policy1.Timestamp);
        Assert.Equal((ushort)40, ab.Policy1.CltvExpiryDelta);
        Assert.Equal(1_000UL, ab.Policy1.HtlcMinimumMsat);
        Assert.Equal(990_000_000UL, ab.Policy1.HtlcMaximumMsat);
        Assert.Equal(1_000U, ab.Policy1.FeeBaseMsat);
        Assert.Equal(100U, ab.Policy1.FeeProportionalMillionths);
        Assert.False(ab.Policy1.IsDisabled);
        Assert.NotNull(ab.Policy2);
        Assert.True(ab.Policy2.IsDisabled);
        var bc = payload.Channels[0];
        Assert.Null(bc.CapacitySat);
        Assert.Equal("Unverified", bc.Verification);
        Assert.Null(bc.SpentAtHeight);
        Assert.Null(bc.Policy1);
        Assert.Null(bc.Policy2);
    }

    [Fact]
    public async Task Given_ScidAndNodeFilters_When_ListGraphChannels_Then_OnlyTheMatchingChannels()
    {
        // Arrange
        using var provider = BuildProvider(CreateGraph());
        var handler = new ListGraphChannelsIpcHandler(NullLogger<ListGraphChannelsIpcHandler>.Instance, provider);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var byScid = await handler.HandleAsync(Envelope(ClientCommand.ListGraphChannels,
                                                        new ListGraphChannelsIpcRequest
                                                        {
                                                            ShortChannelId = ToNumber(s_bc)
                                                        }), ct);
        var byNode = await handler.HandleAsync(Envelope(ClientCommand.ListGraphChannels,
                                                        new ListGraphChannelsIpcRequest { NodeId = s_alice }), ct);
        var none = await handler.HandleAsync(Envelope(ClientCommand.ListGraphChannels,
                                                      new ListGraphChannelsIpcRequest
                                                      {
                                                          ShortChannelId = ToNumber(s_bc),
                                                          NodeId = s_alice
                                                      }), ct);

        // Assert
        Assert.Equal(ToNumber(s_bc), Assert.Single(Read(byScid).Channels).ShortChannelId);
        Assert.Equal(ToNumber(s_ab), Assert.Single(Read(byNode).Channels).ShortChannelId);
        Assert.Empty(Read(none).Channels);
    }

    [Fact]
    public async Task Given_TheGraphDisabled_When_Listed_Then_InvalidOperation()
    {
        // Arrange (plan D12: mainnet without Gossip:Enabled)
        using var provider = BuildProvider(CreateGraph(), network: "mainnet");
        var nodes = new ListNodesIpcHandler(NullLogger<ListNodesIpcHandler>.Instance, provider);
        var channels = new ListGraphChannelsIpcHandler(NullLogger<ListGraphChannelsIpcHandler>.Instance, provider);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var nodesResponse = await nodes.HandleAsync(Envelope(ClientCommand.ListNodes, new ListNodesIpcRequest()), ct);
        var channelsResponse = await channels.HandleAsync(Envelope(ClientCommand.ListGraphChannels,
                                                                   new ListGraphChannelsIpcRequest()), ct);

        // Assert
        foreach (var response in new[] { nodesResponse, channelsResponse })
        {
            Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
            var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options, ct);
            Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
            Assert.Contains("Gossip:Enabled", error.Message);
        }
    }

    [Fact]
    public void Given_GraphRequests_When_RoundTripped_Then_TheFiltersArePreserved()
    {
        // Arrange
        var nodesRequest = new ListNodesIpcRequest { NodeId = s_bob };
        var channelsRequest = new ListGraphChannelsIpcRequest { ShortChannelId = ToNumber(s_ab), NodeId = s_carol };

        // Act
        var nodes = MessagePackSerializer.Deserialize<ListNodesIpcRequest>(
            MessagePackSerializer.Serialize(nodesRequest, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken).ToClientRequest();
        var channels = MessagePackSerializer.Deserialize<ListGraphChannelsIpcRequest>(
            MessagePackSerializer.Serialize(channelsRequest, s_options, TestContext.Current.CancellationToken),
            s_options, TestContext.Current.CancellationToken).ToClientRequest();
        var empty = new ListGraphChannelsIpcRequest().ToClientRequest();

        // Assert
        Assert.Equal(s_bob, nodes.NodeId);
        Assert.Equal(s_ab, channels.ShortChannelId);
        Assert.Equal(s_carol, channels.NodeId);
        Assert.Null(empty.ShortChannelId);
        Assert.Null(empty.NodeId);
    }

    /// <summary>
    /// alice-bob (110x1x0: verified, both policies, the second disabled, spent at 250), bob-carol (105x7x1: unverified,
    /// no policy); alice (two addresses) and carol announced, bob not.
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
        Assert.True(store.TryApplyNode(new GraphNode(s_carol, 1_700_000_000, default, Alias("carol"), [1, 2, 3])));
        Assert.True(store.TryApplyNode(new GraphNode(s_alice, 1_700_000_000, new byte[] { 1, 2 }, Alias("alice"),
                                                     [10, 11, 12],
                                                     [
                                                         AddressDescriptor.FromHost(AddressDescriptorType.IPv4,
                                                                                    "127.0.0.1", 9735),
                                                         AddressDescriptor.FromHost(AddressDescriptorType.IPv6, "::1",
                                                                                    9736)
                                                     ])));
        return store;
    }

    private static ServiceProvider BuildProvider(IGraphStore store, string network = "regtest")
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Options.Create(new GossipGraphOptions()));
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve(network) }));
        services.AddScoped<IClientCommandHandler<ListNodesClientRequest, ListNodesClientResponse>,
            ListNodesClientHandler>();
        services.AddScoped<IClientCommandHandler<ListGraphChannelsClientRequest, ListGraphChannelsClientResponse>,
            ListGraphChannelsClientHandler>();
        return services.BuildServiceProvider();
    }

    private static IpcEnvelope Envelope<T>(ClientCommand command, T request) =>
        new()
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

    private static ListGraphChannelsIpcResponse Read(IpcEnvelope envelope) =>
        MessagePackSerializer.Deserialize<ListGraphChannelsIpcResponse>(envelope.Payload, s_options,
                                                                        TestContext.Current.CancellationToken);

    private static ulong ToNumber(ShortChannelId shortChannelId) =>
        ListGraphChannelsIpcResponse.ToNumber(shortChannelId);

    private static byte[] Alias(string text)
    {
        var alias = new byte[GraphNode.AliasLength];
        System.Text.Encoding.UTF8.GetBytes(text).CopyTo(alias, 0);
        return alias;
    }

    private static CompactPubKey PubKey(byte prefix, byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = prefix;
        return new CompactPubKey(bytes);
    }
}