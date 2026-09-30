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
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Application.Protocol.Factories;
using Application.Tests.Payments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Models;
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

    [Fact]
    public async Task Given_AnOutboundConnectAndAnInboundOneAtTheSameTime_When_BothSaveThePeerRow_Then_TheRowIsWrittenOnce()
    {
        // Arrange (NL-524): both saves run on the node that dialed and is dialed back (bob), so bob gets the fake
        // peer table. The fake parks bob's outbound save of carol's row between its read (no row) and its insert,
        // exactly where the inbound save of a simultaneous connection runs in production.
        var peerDbRepository = new FakePeerDbRepository();
        var bob = await StartNodeAsync(peerDbRepository);
        var carol = await StartNodeAsync();
        var ct = TestContext.Current.CancellationToken;

        // Act: bob dials carol; his save parks with the row still unwritten
        peerDbRepository.ParkKey = carol.NodeId;
        var outbound = bob.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(carol.Address));
        await peerDbRepository.ParkEntered.Task;
        await WaitUntilAsync(() => carol.PeerManager.GetPeer(bob.NodeId) is not null,
                             "the first connection installed on carol", ct);

        // Carol drops that connection and dials bob again: her new inbound connection runs bob's inbound save
        // while bob's outbound save is still parked
        carol.PeerManager.DisconnectPeer(bob.NodeId);
        await WaitUntilAsync(() => !bob.IsConnectedTo(carol) && !carol.IsConnectedTo(bob),
                             "the first connection dropped", ct);
        var inbound = carol.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(bob.Address));
        await WaitUntilAsync(() => bob.IsConnectedTo(carol), "the second connection installed", ct);
        Assert.Equal(0, peerDbRepository.SavedRowCount);

        peerDbRepository.ReleaseParkedInsert();
        await outbound;

        // Assert: the outbound row is committed first, so the inbound save reads it and updates it; nothing
        // violated the (unique) node id, and the dialable row was not replaced by an inbound-only one
        await WaitUntilAsync(() => peerDbRepository.WriteCount(carol.NodeId) >= 2, "the inbound save ran", ct);
        await inbound;
        Assert.Empty(peerDbRepository.ConstraintViolations);
        Assert.Equal(1, peerDbRepository.InsertCount(carol.NodeId));
        Assert.Equal(2, peerDbRepository.WriteCount(carol.NodeId));
        Assert.False(peerDbRepository.Row(carol.NodeId)!.IsInboundOnly);
    }

    [Fact]
    public async Task Given_ATorOnlyNode_When_ItDialsAnOnionPeer_Then_TheHandshakeRunsThroughTheSocksTunnel()
    {
        // Arrange: carol is reachable as an onion service only; the proxy plays Tor, rendezvous included (the
        // connection arrives at carol from a loopback address, as from a local Tor)
        const string onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";
        var ct = TestContext.Current.CancellationToken;
        var carol = await StartNodeAsync();
        await using var proxy = new FakeSocks5Proxy
        {
            RequireAuthentication = true,
            Route = (host, port) => host == onion && port == 9735 ? new IPEndPoint(IPAddress.Loopback, carol.Port) : null
        };
        var bob = await StartNodeAsync(new TorOptions
        {
            Mode = TorMode.TorOnly,
            SocksProxy = proxy.EndPoint,
            OnionServiceEnabled = false
        });

        // Act
        var peer = await bob.PeerManager.ConnectToPeerAsync(
            new PeerAddressInfo($"{Convert.ToHexString(carol.NodeId).ToLowerInvariant()}@{onion}:9735"));

        // Assert: the BOLT 8 handshake and init ran through the tunnel, and both ends keep the connection
        Assert.Equal(carol.NodeId, peer.NodeId);
        Assert.Equal(onion, peer.Host);
        Assert.Equal("TorV3", peer.Type);
        Assert.Equal(new PeerAddressInfo($"{Convert.ToHexString(carol.NodeId).ToLowerInvariant()}@{onion}:9735"),
                     peer.PeerAddressInfo);
        await AssertStableConnectionAsync(bob, carol, "through Tor", ct);
        var request = Assert.Single(proxy.Requests);
        Assert.Equal((onion, 9735, (byte)3), (request.Host, request.Port, request.AddressType));
        Assert.StartsWith("nltg-", request.Username);
    }

    [Fact]
    public async Task Given_ANodeWithoutTor_When_ItDialsAnOnionPeer_Then_TheConnectFailsWithTheTorSetting()
    {
        // Arrange
        var bob = await StartNodeAsync();

        // Act & Assert
        var e = await Assert.ThrowsAsync<Domain.Exceptions.ConnectionException>(
            () => bob.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(
                "028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7@"
              + "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion:9735")));
        Assert.Contains("Node:Tor:Mode", e.Message);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    private async Task<TestNode> StartNodeAsync(TorOptions? torOptions = null)
    {
        var node = await TestNode.StartAsync(_serializer, torOptions: torOptions);
        _nodes.Add(node);
        return node;
    }

    private async Task<TestNode> StartNodeAsync(FakePeerDbRepository peerDbRepository)
    {
        var node = await TestNode.StartAsync(_serializer, peerDbRepository);
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
        private static int s_nextNodeSeed;

        private readonly ServiceProvider _serviceProvider;
        private readonly int _port;

        public PeerManager PeerManager { get; }
        public CompactPubKey NodeId { get; }
        public int Port => _port;
        public string Address => $"{Convert.ToHexString(NodeId).ToLowerInvariant()}@127.0.0.1:{_port}";

        private TestNode(ServiceProvider serviceProvider, PeerManager peerManager, CompactPubKey nodeId, int port)
        {
            _serviceProvider = serviceProvider;
            PeerManager = peerManager;
            NodeId = nodeId;
            _port = port;
        }

        public bool IsConnectedTo(TestNode other) => PeerManager.GetPeer(other.NodeId) is not null;

        private static byte NextNodeSeed() => (byte)Interlocked.Increment(ref s_nextNodeSeed);

        public static async Task<TestNode> StartAsync(IMessageSerializer serializer,
                                                      IPeerDbRepository? peerDbRepository = null,
                                                      TorOptions? torOptions = null)
        {
            var port = GetFreePort();
            var nodeOptions = new NodeOptions
            {
                ListenAddresses = [$"127.0.0.1:{port}"],
                NetworkTimeout = TimeSpan.FromSeconds(10),
                Tor = torOptions ?? new TorOptions()
            };

            // A hand-written key manager: the handshake's static ECDH needs ComputeNodeSharedSecret, which Moq
            // cannot set up (span parameters)
            var keyManager = new TestNodeKeyManager(NextNodeSeed());
            var nodeId = keyManager.NodeId;

            var peerDbRepositoryMock = new Mock<IPeerDbRepository>();
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(u => u.PeerDbRepository)
                      .Returns(peerDbRepository ?? peerDbRepositoryMock.Object);
            unitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(Options.Create(nodeOptions));
            services.AddSingleton(serializer);
            services.AddSingleton<IEcdh, TestEcdh>();
            services.AddSingleton<ISecureKeyManager>(keyManager);
            services.AddSingleton<IMessageFactory, MessageFactory>();
            services.AddScoped(_ => unitOfWork.Object);
            services.AddInfrastructureServices();
            var serviceProvider = services.BuildServiceProvider();

            var channelMemoryRepository = new Mock<IChannelMemoryRepository>();
            channelMemoryRepository.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
            var peerManager = new PeerManager(new Mock<IChannelManager>().Object, channelMemoryRepository.Object,
                                              NullLogger<PeerManager>.Instance,
                                              serviceProvider.GetRequiredService<IPeerServiceFactory>(),
                                              keyManager, serviceProvider.GetRequiredService<ITcpService>(),
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

    /// <summary>
    /// The seam the two connect paths of NL-524 race on: a peer-row table that commits inside
    /// <see cref="AddOrUpdateAsync"/>, so the test can park a save between its read (no row) and its insert. An
    /// insert of a row that appeared in between throws, as the database's UNIQUE constraint on Peers.NodeId does
    /// when the second save flushes.
    /// </summary>
    private sealed class FakePeerDbRepository : IPeerDbRepository
    {
        private readonly object _lock = new();
        private readonly Dictionary<CompactPubKey, PeerModel> _rows = [];
        private readonly Dictionary<CompactPubKey, int> _inserts = [];
        private readonly Dictionary<CompactPubKey, int> _writes = [];
        private readonly List<Exception> _constraintViolations = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _parked;

        /// <summary>The node id whose first insert parks between its read and its write; set before the connect.</summary>
        public CompactPubKey? ParkKey { get; set; }

        /// <summary>Completed when the parked insert has read (no row) and is waiting to write.</summary>
        public TaskCompletionSource ParkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Exception> ConstraintViolations
        {
            get { lock (_lock) return [.. _constraintViolations]; }
        }

        public int SavedRowCount
        {
            get { lock (_lock) return _rows.Count; }
        }

        public void ReleaseParkedInsert() => _release.TrySetResult();

        public int InsertCount(CompactPubKey nodeId)
        {
            lock (_lock) return _inserts.GetValueOrDefault(nodeId);
        }

        public int WriteCount(CompactPubKey nodeId)
        {
            lock (_lock) return _writes.GetValueOrDefault(nodeId);
        }

        public PeerModel? Row(CompactPubKey nodeId)
        {
            lock (_lock) return _rows.GetValueOrDefault(nodeId);
        }

        public async Task AddOrUpdateAsync(PeerModel peerModel)
        {
            bool park;
            lock (_lock)
            {
                park = !_rows.ContainsKey(peerModel.NodeId) && peerModel.NodeId == ParkKey && !_parked;
                if (park)
                    _parked = true;
            }

            if (park)
            {
                ParkEntered.TrySetResult();
                await _release.Task;
            }

            lock (_lock)
            {
                if (_rows.ContainsKey(peerModel.NodeId))
                {
                    // The row appeared while this save was parked between its read and its write
                    var violation = new InvalidOperationException("UNIQUE constraint failed: Peers.NodeId");
                    _constraintViolations.Add(violation);
                    throw violation;
                }

                _rows[peerModel.NodeId] = peerModel;
                _inserts[peerModel.NodeId] = _inserts.GetValueOrDefault(peerModel.NodeId) + 1;
                _writes[peerModel.NodeId] = _writes.GetValueOrDefault(peerModel.NodeId) + 1;
            }
        }

        public void Update(PeerModel peerModel)
        {
            lock (_lock)
            {
                _rows[peerModel.NodeId] = peerModel;
                _writes[peerModel.NodeId] = _writes.GetValueOrDefault(peerModel.NodeId) + 1;
            }
        }

        public Task<IEnumerable<PeerModel>> GetAllAsync()
        {
            lock (_lock)
                return Task.FromResult<IEnumerable<PeerModel>>([.. _rows.Values]);
        }

        public Task<PeerModel?> GetByNodeIdAsync(CompactPubKey nodeId)
        {
            lock (_lock)
                return Task.FromResult(_rows.GetValueOrDefault(nodeId));
        }

        public Task UpdatePeerLastSeenAsync(CompactPubKey peerCompactPubKey)
        {
            lock (_lock)
            {
                if (_rows.TryGetValue(peerCompactPubKey, out var row))
                    row.LastSeenAt = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }
    }
}