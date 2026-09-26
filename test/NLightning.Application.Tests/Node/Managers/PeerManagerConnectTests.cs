using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Application.Protocol.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure;
using Infrastructure.Crypto.Interfaces;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// Two real peer managers talking over loopback TCP with the real BOLT 8 transport, message, peer-communication and
/// peer services (only the message bytes are faked): the NLightning-to-NLightning connect bugs NL-239 (the responder
/// lost the initiator's init) and NL-240 (a simultaneous connect left no live connection).
/// </summary>
public sealed class PeerManagerConnectTests : IAsyncLifetime
{
    private const int Rounds = 20;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_stableWindow = TimeSpan.FromMilliseconds(300);

    private readonly InProcessMessageSerializer _serializer = new();
    private readonly List<TestNode> _nodes = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_TwoNodes_When_OneConnectsToTheOther_Then_BothKeepOneLiveConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync();
        var carol = await StartNodeAsync();

        for (var round = 1; round <= Rounds; round++)
        {
            // Act: alternate the direction
            var (from, to) = round % 2 == 0 ? (bob, carol) : (carol, bob);
            var peer = await from.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(to.Address));

            // Assert: the connect returns only after the init exchange, and the responder installs it too
            Assert.Equal(to.NodeId, peer.NodeId);
            Assert.NotNull(from.PeerManager.GetPeer(to.NodeId));
            await AssertStableConnectionAsync(bob, carol, $"round {round}", ct);

            await DisconnectAsync(bob, carol, ct);
        }
    }

    [Fact]
    public async Task Given_TwoNodes_When_ConnectingToEachOtherAtTheSameTime_Then_BothKeepTheSameConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync();
        var carol = await StartNodeAsync();

        for (var round = 1; round <= Rounds; round++)
        {
            // Act
            var outcomes = await Task.WhenAll(TryConnectAsync(bob, carol), TryConnectAsync(carol, bob));

            // Assert: a lost tie-break or a connection the other end closed is fine; anything else is not
            Assert.All(outcomes, o => Assert.Null(o));

            // Had the two ends kept different connections, each would close the one the other kept
            await AssertStableConnectionAsync(bob, carol, $"round {round}", ct);
            Assert.Single(bob.PeerManager.ListPeers(), p => p.NodeId == carol.NodeId);
            Assert.Single(carol.PeerManager.ListPeers(), p => p.NodeId == bob.NodeId);

            await DisconnectAsync(bob, carol, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    private async Task<TestNode> StartNodeAsync()
    {
        var node = await TestNode.StartAsync(_serializer);
        _nodes.Add(node);
        return node;
    }

    /// <returns>null when the outcome is acceptable, otherwise the unexpected exception.</returns>
    private static async Task<Exception?> TryConnectAsync(TestNode from, TestNode to)
    {
        try
        {
            await from.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(to.Address));
            return null;
        }
        catch (InvalidOperationException)
        {
            // The other direction won the tie-break, or was installed first
            return null;
        }
        catch (Domain.Exceptions.ConnectionException)
        {
            // The other end kept its own connection and closed this one during init
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static async Task AssertStableConnectionAsync(TestNode a, TestNode b, string what,
                                                          CancellationToken cancellationToken)
    {
        await WaitUntilAsync(() => a.IsConnectedTo(b) && b.IsConnectedTo(a), $"{what}: both ends connected",
                             cancellationToken);

        var until = DateTime.UtcNow + s_stableWindow;
        while (DateTime.UtcNow < until)
        {
            Assert.True(a.IsConnectedTo(b) && b.IsConnectedTo(a), $"{what}: the connection dropped");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task DisconnectAsync(TestNode a, TestNode b, CancellationToken cancellationToken)
    {
        if (a.IsConnectedTo(b))
            a.PeerManager.DisconnectPeer(b.NodeId);
        if (b.IsConnectedTo(a))
            b.PeerManager.DisconnectPeer(a.NodeId);

        await WaitUntilAsync(() => !a.IsConnectedTo(b) && !b.IsConnectedTo(a), "both ends disconnected",
                             cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for: {what}");

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>
    /// One node: a peer manager on the real infrastructure stack, listening on its own loopback port.
    /// </summary>
    private sealed class TestNode : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly int _port;

        public PeerManager PeerManager { get; }
        public CompactPubKey NodeId { get; }
        public string Address => $"{Convert.ToHexString(NodeId).ToLowerInvariant()}@127.0.0.1:{_port}";

        private TestNode(ServiceProvider serviceProvider, PeerManager peerManager, CompactPubKey nodeId, int port)
        {
            _serviceProvider = serviceProvider;
            PeerManager = peerManager;
            NodeId = nodeId;
            _port = port;
        }

        public bool IsConnectedTo(TestNode other) => PeerManager.GetPeer(other.NodeId) is not null;

        public static async Task<TestNode> StartAsync(IMessageSerializer serializer)
        {
            var port = GetFreePort();
            var nodeOptions = new NodeOptions
            {
                ListenAddresses = [$"127.0.0.1:{port}"],
                NetworkTimeout = TimeSpan.FromSeconds(10)
            };

            var key = new Key();
            var nodeId = new CompactPubKey(key.PubKey.ToBytes());
            var keyManager = new Mock<ISecureKeyManager>();
            keyManager.Setup(k => k.GetNodeKeyPair()).Returns(new CryptoKeyPair(key.ToBytes(), nodeId));
            keyManager.Setup(k => k.GetNodePubKey()).Returns(nodeId);

            var peerDbRepository = new Mock<IPeerDbRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(u => u.PeerDbRepository).Returns(peerDbRepository.Object);
            unitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Options.Create(nodeOptions));
            services.AddSingleton(serializer);
            services.AddSingleton<IEcdh, TestEcdh>();
            services.AddSingleton(keyManager.Object);
            services.AddSingleton<IMessageFactory, MessageFactory>();
            services.AddScoped(_ => unitOfWork.Object);
            services.AddInfrastructureServices();
            var serviceProvider = services.BuildServiceProvider();

            var channelMemoryRepository = new Mock<IChannelMemoryRepository>();
            channelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
            var peerManager = new PeerManager(new Mock<IChannelManager>().Object, channelMemoryRepository.Object,
                                              NullLogger<PeerManager>.Instance,
                                              serviceProvider.GetRequiredService<IPeerServiceFactory>(),
                                              keyManager.Object, serviceProvider.GetRequiredService<ITcpService>(),
                                              serviceProvider, Options.Create(nodeOptions));
            await peerManager.StartAsync(CancellationToken.None);

            return new TestNode(serviceProvider, peerManager, nodeId, port);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await PeerManager.StopAsync();
            }
            finally
            {
                await _serviceProvider.DisposeAsync();
            }
        }

        /// <summary>
        /// A port the OS picked, not one of <c>PortPoolUtil</c>'s: that pool is per process, and other test projects
        /// running at the same time listen on its ports.
        /// </summary>
        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>
    /// BOLT 8 ECDH with NBitcoin: SHA256 of the compressed shared point.
    /// </summary>
    private sealed class TestEcdh : IEcdh
    {
        public void SecP256K1Dh(PrivKey k, ReadOnlySpan<byte> rk, Span<byte> sharedKey)
        {
            using var key = new Key(k);
            var sharedPubKey = new PubKey(rk.ToArray()).GetSharedPubkey(key);
            SHA256.HashData(sharedPubKey.Compress().ToBytes(), sharedKey);
        }

        public CryptoKeyPair GenerateKeyPair()
        {
            using var key = new Key();
            return new CryptoKeyPair(key.ToBytes(), key.PubKey.ToBytes());
        }

        public CryptoKeyPair GenerateKeyPair(ReadOnlySpan<byte> privateKey)
        {
            using var key = new Key(privateKey.ToArray());
            return new CryptoKeyPair(key.ToBytes(), key.PubKey.ToBytes());
        }
    }

    /// <summary>
    /// Both nodes live in this process, so a message travels as an 8-byte handle to the object itself: the transport
    /// still encrypts, frames and orders real bytes, and any message type (init, ping, pong, warning) round-trips.
    /// </summary>
    private sealed class InProcessMessageSerializer : IMessageSerializer
    {
        private readonly ConcurrentDictionary<long, IMessage> _messages = new();
        private long _nextId;

        public Task SerializeAsync(IMessage message, Stream stream)
        {
            var id = Interlocked.Increment(ref _nextId);
            _messages[id] = message;
            var bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, id);
            return stream.WriteAsync(bytes).AsTask();
        }

        public async Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream)
            where TMessage : class, IMessage
        {
            return await DeserializeMessageAsync(stream) as TMessage;
        }

        public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
        {
            var bytes = new byte[sizeof(long)];
            await stream.ReadExactlyAsync(bytes);
            return _messages.TryRemove(BinaryPrimitives.ReadInt64BigEndian(bytes), out var message)
                       ? message
                       : throw new InvalidOperationException("Unknown message handle");
        }
    }
}