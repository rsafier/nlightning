using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Node.Services;
using Daemon.Contracts.Control;
using Daemon.Extensions;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Daemon.Services;
using Daemon.Tests.Services;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The operator commands of NL-152 over IPC: <c>disconnect</c> (refused with HTLCs in flight unless forced),
/// <c>listpeers</c> and <c>info</c> with the channel counts; and <c>shutdown</c> (NL-591).
/// </summary>
public class OperatorIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_alice = CreatePubKey(1);
    private static readonly CompactPubKey s_bob = CreatePubKey(2);

    private readonly Mock<IPeerManager> _peerManagerMock = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();
    private readonly List<ChannelModel> _channels = [];
    private readonly ShutdownDrainWaiterTests.FakeBusyMonitor _busyStateMonitor = new();

    public OperatorIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns((Func<ChannelModel, bool> predicate) =>
                                                 _channels.Where(predicate).ToList());
    }

    [Fact]
    public async Task Given_ConnectedPeerWithoutHtlcs_When_Disconnect_Then_ItIsDisconnectedAndReported()
    {
        // Arrange
        ConnectPeer(s_alice);
        _channels.Add(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open));
        _channels.Add(CreateChannel(CreateChannelId(2), s_alice, ChannelState.Closed));
        var handler = GetDisconnectHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.DisconnectPeer,
                                                                new DisconnectPeerIpcRequest { NodeId = s_alice }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<DisconnectPeerIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(s_alice, payload.NodeId);
        Assert.Equal(1, payload.ChannelCount);
        Assert.Equal(0, payload.HtlcsInFlight);
        _peerManagerMock.Verify(x => x.DisconnectPeer(s_alice, null), Times.Once);
    }

    [Fact]
    public async Task Given_PeerWithHtlcsInFlight_When_DisconnectWithoutForce_Then_RefusedAndStillConnected()
    {
        // Arrange: one pending HTLC each way, one settled (not in flight)
        ConnectPeer(s_alice);
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        var handler = GetDisconnectHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.DisconnectPeer,
                                                                new DisconnectPeerIpcRequest { NodeId = s_alice }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("2 HTLC(s) in flight", error.Message);
        Assert.Contains("--force", error.Message);
        _peerManagerMock.Verify(x => x.DisconnectPeer(It.IsAny<CompactPubKey>(), It.IsAny<Exception?>()),
                                Times.Never);
    }

    [Fact]
    public async Task Given_PeerWithHtlcsInFlight_When_DisconnectWithForce_Then_Disconnected()
    {
        // Arrange
        ConnectPeer(s_alice);
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(2), s_bob));
        var handler = GetDisconnectHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.DisconnectPeer,
                                          new DisconnectPeerIpcRequest { NodeId = s_alice, Force = true }),
                           TestContext.Current.CancellationToken);

        // Assert: only alice's channel counts
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<DisconnectPeerIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(1, payload.ChannelCount);
        Assert.Equal(2, payload.HtlcsInFlight);
        _peerManagerMock.Verify(x => x.DisconnectPeer(s_alice, null), Times.Once);
    }

    [Fact]
    public async Task Given_HtlcsOnlyOnAChannelResolvingOnChain_When_DisconnectWithoutForce_Then_Disconnected()
    {
        // Arrange - NL-1006: the HTLCs of a force-closed channel are settled on chain, never over the link
        ConnectPeer(s_alice);
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice, ChannelState.OnchainResolving));
        var handler = GetDisconnectHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.DisconnectPeer,
                                                                new DisconnectPeerIpcRequest { NodeId = s_alice }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        _peerManagerMock.Verify(x => x.DisconnectPeer(s_alice, null), Times.Once);
    }

    [Fact]
    public async Task Given_PeerNotConnected_When_Disconnect_Then_InvalidOperation()
    {
        // Arrange
        var handler = GetDisconnectHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.DisconnectPeer,
                                                                new DisconnectPeerIpcRequest { NodeId = s_bob }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not connected", error.Message);
        _peerManagerMock.Verify(x => x.DisconnectPeer(It.IsAny<CompactPubKey>(), It.IsAny<Exception?>()),
                                Times.Never);
    }

    [Fact]
    public void Given_OperatorServices_When_Registered_Then_TheRouterHasOneDisconnectAndOneShutdownHandler()
    {
        // Arrange
        var services = BuildServices();

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).Order().ToList();
        Assert.Equal([ClientCommand.DisconnectPeer, ClientCommand.Shutdown], commands);
    }

    [Fact]
    public async Task Given_ConnectedPeers_When_ListPeers_Then_ChannelsAndHtlcsComeFromMemory()
    {
        // Arrange: the peer model's stored channel list is stale (empty); memory has the truth
        var alice = ConnectPeer(s_alice);
        ConnectPeer(s_bob);
        _peerManagerMock.Setup(x => x.ListPeers()).Returns([alice, _peerManagerMock.Object.GetPeer(s_bob)!]);
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        _channels.Add(CreateChannel(CreateChannelId(2), s_alice, ChannelState.V1FundingSigned));
        _channels.Add(CreateChannel(CreateChannelId(3), s_alice, ChannelState.Stale));
        // NL-1006: listpeers keeps showing the HTLCs of a channel resolving on chain
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(4), s_alice, ChannelState.OnchainResolving));
        var handler = new ListPeersIpcHandler(_peerManagerMock.Object, _channelMemoryRepositoryMock.Object,
                                              NullLogger<ListPeersIpcHandler>.Instance);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.ListPeers, new ListPeersIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ListPeersIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.NotNull(payload.Peers);
        var aliceInfo = Assert.Single(payload.Peers, p => p.Id == s_alice);
        Assert.Equal(3U, aliceInfo.ChannelQty);
        Assert.Equal(4U, aliceInfo.HtlcsInFlight);
        Assert.Equal("127.0.0.1:9735", aliceInfo.Address);
        Assert.True(aliceInfo.Connected);
        var bobInfo = Assert.Single(payload.Peers, p => p.Id == s_bob);
        Assert.Equal(0U, bobInfo.ChannelQty);
        Assert.Equal(0U, bobInfo.HtlcsInFlight);
    }

    [Fact]
    public async Task Given_PeersAndChannels_When_NodeInfo_Then_CountsAreReported()
    {
        // Arrange
        var alice = ConnectPeer(s_alice);
        _peerManagerMock.Setup(x => x.ListPeers()).Returns([alice]);
        _channels.Add(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open));
        _channels.Add(CreateChannel(CreateChannelId(2), s_alice, ChannelState.Open));
        _channels.Add(CreateChannel(CreateChannelId(3), s_bob, ChannelState.V1FundingSigned));
        _channels.Add(CreateChannel(CreateChannelId(4), s_bob, ChannelState.ShuttingDown));
        _channels.Add(CreateChannel(CreateChannelId(5), s_bob, ChannelState.OnchainResolving));
        _channels.Add(CreateChannel(CreateChannelId(6), s_bob, ChannelState.Closed));
        var query = new Mock<INodeInfoQueryService>();
        query.Setup(x => x.QueryAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new NodeInfoResponse
             {
                 PubKey = s_alice.ToString(),
                 ListeningTo = "127.0.0.1:9735",
                 BestBlockHash = new string('0', 64)
             });
        var handler = new NodeInfoIpcHandler(query.Object, NullLogger<NodeInfoIpcHandler>.Instance,
                                             _peerManagerMock.Object, _channelMemoryRepositoryMock.Object);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.NodeInfo, new ListPeersIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<NodeInfoIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(1, payload.PeerCount);
        Assert.Equal(2, payload.ActiveChannelCount);
        Assert.Equal(1, payload.PendingChannelCount);
        Assert.Equal(2, payload.ClosingChannelCount);
    }

    [Fact]
    public async Task Given_HtlcsInFlight_When_Shutdown_Then_RefusedAndTheNodeGoesOn()
    {
        // Arrange - NL-591: the first pass refuses rather than waiting for the HTLCs
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        // NL-595: the message names the channels that carry the HTLCs (one here), not the node's total
        Assert.Contains("2 HTLC(s) are in flight on 1 channel(s)", error.Message);
        Assert.Contains($"{CreateChannelId(1)} (2)", error.Message);
        Assert.DoesNotContain("7 channel", error.Message);
        Assert.False(provider.GetRequiredService<INodeDrainState>().IsDraining);
        Assert.False(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
    }

    [Fact]
    public async Task Given_NoHtlcsInFlight_When_Shutdown_Then_DrainingAndTheHostStopsAfterTheAnswer()
    {
        // Arrange
        _channels.Add(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open));
        _channels.Add(CreateChannel(CreateChannelId(2), s_bob, ChannelState.Closed));
        var lifetime = new Mock<IHostApplicationLifetime>();
        var services = BuildServices();
        services.AddSingleton(lifetime.Object);
        var provider = services.BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert: the drain holds, and the host stops only once the IPC server wrote the answer
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(1, payload.ChannelCount);
        Assert.True(provider.GetRequiredService<INodeDrainState>().IsDraining);
        var trigger = provider.GetRequiredService<NodeShutdownTrigger>();
        Assert.True(trigger.IsStopRequested);
        lifetime.Verify(x => x.StopApplication(), Times.Never);
        trigger.StopIfRequested();
        trigger.StopIfRequested();
        lifetime.Verify(x => x.StopApplication(), Times.Once);
    }

    [Theory]
    [InlineData(ChannelState.OnchainResolving)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_HtlcsOnlyOnAChannelResolvingOnChain_When_Shutdown_Then_StoppedAndTheyAreReported(
        ChannelState state)
    {
        // Arrange - NL-1006 (FAFO2, 2026-10-04): a force-closed channel whose HTLC the peer had claimed on chain
        // refused "1 HTLC(s) are in flight on 1 channel(s)"; no drain settles it, the resolvers resume after a restart
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice, state));
        _channels.Add(CreateChannel(CreateChannelId(2), s_bob, ChannelState.Open));
        _busyStateMonitor.Default = ShutdownDrainWaiterTests.FakeBusyMonitor.Idle(2) with
        {
            HtlcsResolvingOnChain = 2,
            NearestCltvExpiry = 500,
            BlocksUntilDeadline = 40
        };
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(ShutdownOutcome.Stopped, payload.Outcome);
        Assert.Equal(2, payload.ChannelCount);
        Assert.Equal(0, payload.HtlcsInFlight);
        Assert.Equal(2, payload.HtlcsResolvingOnChain);
        Assert.Equal(500u, payload.NearestCltvExpiry);
        Assert.Equal(40, payload.BlocksUntilDeadline);
        Assert.True(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
    }

    [Fact]
    public async Task Given_HtlcsOnAnOpenAndAnOnchainChannel_When_Shutdown_Then_RefusedNamingOnlyTheOpenOne()
    {
        // Arrange
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice, ChannelState.OnchainResolving));
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(2), s_bob));
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Contains("2 HTLC(s) are in flight on 1 channel(s)", error.Message);
        Assert.Contains($"{CreateChannelId(2)} (2)", error.Message);
        Assert.DoesNotContain(CreateChannelId(1).ToString(), error.Message);
    }

    [Fact]
    public async Task Given_AShutdownRunning_When_ShutdownAgain_Then_Refused()
    {
        // Arrange
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);
        await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                  TestContext.Current.CancellationToken);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Contains("already shutting down", error.Message);
    }

    [Fact]
    public async Task Given_HtlcsStillInFlight_When_ShutdownWithWaitAndShortTimeout_Then_TimedOutAndTheGateReopens()
    {
        // Arrange: the monitor stays busy; the request's smallest timeout (1 s) runs out
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        _busyStateMonitor.Default = ShutdownDrainWaiterTests.FakeBusyMonitor.Busy(1, 2);
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(
            CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest { Wait = true, TimeoutSeconds = 1 }),
            TestContext.Current.CancellationToken);

        // Assert: timed out, the busy state is reported, the node goes on
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(ShutdownOutcome.TimedOut, payload.Outcome);
        Assert.Equal(2, payload.HtlcsInFlight);
        Assert.Equal(1, payload.NegotiationCount);
        Assert.Single(payload.BusyChannels);
        Assert.True(provider.GetRequiredService<INodeDrainState>().IsDraining is false);
        Assert.False(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
    }

    [Fact]
    public async Task Given_HtlcsInFlight_When_ShutdownWithForce_Then_ForcedWithTheNearestExpiry()
    {
        // Arrange
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        _busyStateMonitor.Default = ShutdownDrainWaiterTests.FakeBusyMonitor.Busy(0, 2);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var services = BuildServices();
        services.AddSingleton(lifetime.Object);
        var provider = services.BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(
            CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest { Force = true }),
            TestContext.Current.CancellationToken);

        // Assert: the node stops (nothing broadcast) and the answer reports the HTLCs and the deadline
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(ShutdownOutcome.Forced, payload.Outcome);
        Assert.Equal(2, payload.HtlcsInFlight);
        Assert.Equal(500u, payload.NearestCltvExpiry);
        Assert.Equal(40, payload.BlocksUntilDeadline);
        Assert.True(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
    }

    [Fact]
    public async Task Given_WaitRunsOutWithForce_When_Shutdown_Then_ForcedOnTheTimeout()
    {
        // Arrange: --force rides on --wait: the drain times out, then the stop happens anyway
        _channels.Add(CreateChannelWithHtlcs(CreateChannelId(1), s_alice));
        _busyStateMonitor.Default = ShutdownDrainWaiterTests.FakeBusyMonitor.Busy(1, 2);
        var provider = BuildServices().BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(
            CreateEnvelope(ClientCommand.Shutdown,
                           new ShutdownIpcRequest { Wait = true, TimeoutSeconds = 1, Force = true }),
            TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(ShutdownOutcome.Forced, payload.Outcome);
        Assert.Equal(2, payload.HtlcsInFlight);
        Assert.Equal(1, payload.NegotiationCount);
        Assert.True(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
    }

    [Fact]
    public async Task Given_ANodeThatDrainsToIdle_When_ShutdownWithWait_Then_Stopped()
    {
        // Arrange: nothing busy from the start; the settle period holds and the node stops
        _channels.Add(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open));
        _busyStateMonitor.Default = ShutdownDrainWaiterTests.FakeBusyMonitor.Idle(1);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var services = BuildServices();
        services.AddSingleton(lifetime.Object);
        var provider = services.BuildServiceProvider();
        var handler = GetHandler(provider, ClientCommand.Shutdown);

        // Act
        var response = await handler.HandleAsync(
            CreateEnvelope(ClientCommand.Shutdown, new ShutdownIpcRequest { Wait = true, TimeoutSeconds = 5 }),
            TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<ShutdownIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(ShutdownOutcome.Stopped, payload.Outcome);
        Assert.Equal(1, payload.ChannelCount);
        Assert.Equal(0, payload.HtlcsInFlight);
        Assert.True(provider.GetRequiredService<NodeShutdownTrigger>().IsStopRequested);
        Assert.True(provider.GetRequiredService<INodeDrainState>().IsDraining);
    }

    private static IIpcCommandHandler GetHandler(IServiceProvider provider, ClientCommand command) =>
        provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private IIpcCommandHandler GetDisconnectHandler()
    {
        var provider = BuildServices().BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.DisconnectPeer);
    }

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_peerManagerMock.Object);
        services.AddSingleton(_channelMemoryRepositoryMock.Object);
        services.AddSingleton<INodeDrainState, NodeDrainState>();
        services.AddSingleton<INodeBusyStateMonitor>(_busyStateMonitor);
        services.AddSingleton(new ShutdownDrainWaiter(_busyStateMonitor,
                                                      NullLogger<ShutdownDrainWaiter>.Instance)
        {
            Settle = TimeSpan.FromMilliseconds(30),
            Poll = TimeSpan.FromMilliseconds(5)
        });
        services.AddOperatorIpcServices();
        return services;
    }

    private PeerModel ConnectPeer(CompactPubKey nodeId)
    {
        var peer = new PeerModel(nodeId, "127.0.0.1", 9735, "ipv4");
        var peerService = new Mock<IPeerService>();
        peerService.SetupGet(x => x.Features).Returns(new FeatureOptions());
        peer.SetPeerService(peerService.Object);
        _peerManagerMock.Setup(x => x.GetPeer(nodeId)).Returns(peer);
        return peer;
    }

    private static ChannelModel CreateChannelWithHtlcs(ChannelId channelId, CompactPubKey peerId,
                                                       ChannelState state = ChannelState.Open)
    {
        var channel = CreateChannel(channelId, peerId, state);
        channel.UpdateCommitments(CreateSnapshot(channelId,
        [
            Record(HtlcDirection.Outgoing, 0, HtlcState.SentAddAckRevocation),
            Record(HtlcDirection.Outgoing, 1, HtlcState.RcvdRemoveAckRevocation,
                   HtlcRemoval.Fulfill(new Secret(new byte[32]))),
            Record(HtlcDirection.Incoming, 0, HtlcState.RcvdAddAckRevocation)
        ]));
        return channel;
    }

    private static HtlcRecord Record(HtlcDirection direction, ulong id, HtlcState state, HtlcRemoval? removal = null) =>
        new(direction, id, 10_000, new Hash(Enumerable.Repeat((byte)(id + 1), 32).ToArray()), 500, state, removal);

    private static ChannelCommitments CreateSnapshot(ChannelId channelId, IReadOnlyList<HtlcRecord> htlcs)
    {
        var party = new CommitmentParty(354, 10_000, 1_000, 30, 1_000_000_000);
        var @params = new CommitmentParams(true, 1_000_000, false, party, party);
        const ulong localMsat = 700_000_000;
        const ulong remoteMsat = 300_000_000;
        var nextOutgoing = htlcs.Where(h => h.Direction == HtlcDirection.Outgoing).Select(h => h.Id + 1)
                                .DefaultIfEmpty().Max();
        var nextIncoming = htlcs.Where(h => h.Direction == HtlcDirection.Incoming).Select(h => h.Id + 1)
                                .DefaultIfEmpty().Max();
        return ChannelCommitments.Restore(channelId, @params, localMsat, remoteMsat, htlcs,
                                          [new FeeUpdate(0, 1_000, HtlcState.SentAddAckRevocation)], nextOutgoing,
                                          nextIncoming,
                                          new LocalCommit(1, new CommitmentSpec(CommitmentSide.Local, 1_000, localMsat,
                                                                                remoteMsat, []), null),
                                          new RemoteCommit(1, new CommitmentSpec(CommitmentSide.Remote, 1_000,
                                                                                 localMsat, remoteMsat, []),
                                                           CreatePubKey(9)),
                                          null, CreatePubKey(10));
    }

    private static ChannelModel CreateChannel(ChannelId channelId, CompactPubKey peerId, ChannelState state)
    {
        return new ChannelModel(new ChannelParams(), channelId, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000),
                                new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                LightningMoney.Zero, null, 0, peerId, 0, state, ChannelVersion.V1);
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request)
    {
        return new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }

    private static ChannelId CreateChannelId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}