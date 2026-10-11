using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Node;

using Application.Gossip.Graph.Interfaces;
using Docker.Mock;
using Docker.Utils;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Reestablish;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Interfaces;
using Persistence;
using Scale;

/// <summary>
/// NL-1385 end to end: two nodes of the daemon's composition with real channels between them. The hub stores the peer
/// as inbound-only (it only ever connected to us, NL-497), the peer waits an hour before it redials; when the hub
/// restarts it dials the peer at the address of the peer's <c>node_announcement</c> in its graph, both ends reestablish
/// every channel, and the address that connected is saved over the inbound-only row.
/// </summary>
public sealed class InboundOnlyPeerRedialTests : IAsyncDisposable
{
    private static readonly TimeSpan s_reestablishTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The address the peer announces (TEST-NET-3); the hub's dialer routes it to the peer's listener.</summary>
    private const string AnnouncedHost = "203.0.113.7";

    private const ushort AnnouncedPort = 9735;

    private readonly List<string> _files = [];

    [Fact]
    public async Task Given_AnInboundOnlyPeerThatAnnouncedItself_When_TheHubRestarts_Then_TheHubRedialsItAndReestablishes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var hubKeys = new FakeSecureKeyManager();
        var peerKeys = new FakeSecureKeyManager();
        var chain = new ScaleChain(ChannelScaleSeeder.FirstFundingHeight - 1);
        var hubPort = await PortPoolUtil.GetAvailablePortAsync();
        var peerPort = await PortPoolUtil.GetAvailablePortAsync();
        var hubDb = TestNodeDatabase.Sqlite(NewDbPath());
        var peerDb = TestNodeDatabase.Sqlite(NewDbPath());
        try
        {
            // Both ends of three channels; the hub's row of the peer is inbound-only (no address), the peer's row of
            // the hub is dialable
            await using (var hubSeed = new ScaleNode("hub-seed", hubDb, hubKeys, chain, hubPort))
            await using (var peerSeed = new ScaleNode("peer-seed", peerDb, peerKeys, chain, peerPort))
            {
                await hubSeed.BuildAsync(ct);
                await peerSeed.BuildAsync(ct);
                var peer = new ScalePeer(peerKeys.GetNodePubKey(), string.Empty, 0,
                                         new SignerKeySource(peerSeed.Services.GetRequiredService<ILightningSigner>()),
                                         Live: true);
                var hubSource = new SignerKeySource(hubSeed.Services.GetRequiredService<ILightningSigner>());
                var seeder = new ChannelScaleSeeder { HtlcChannelShare = 0 };
                var channels = seeder.Build(hubKeys.GetNodePubKey(), hubSource, [peer], 3, chain,
                                            hubSeed.Services.GetRequiredService<IChannelIdFactory>());
                await seeder.WriteAsync(hubSeed.Services, channels, hubSide: true,
                                        c => new PeerModel(c.Peer.NodeId, string.Empty, 0, "IPv4")
                                        {
                                            IsInboundOnly = true,
                                            LastSeenAt = DateTime.UtcNow - TimeSpan.FromHours(1)
                                        }, ct);
                await seeder.WriteAsync(peerSeed.Services, channels, hubSide: false,
                                        _ => new PeerModel(hubKeys.GetNodePubKey(), "127.0.0.1", (uint)hubPort, "IPv4"),
                                        ct);
            }

            for (var i = 0; i < 6; i++)
                chain.Mine([]);

            // First starts (signing enrollment, accounting cutover), one node at a time
            await using (var first = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort))
                await first.StartAsync(ct);
            await using (var first = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort))
                await first.StartAsync(ct);

            // The peer runs and never redials the hub on its own (its first dial fails: the hub is down)
            await using var peerNode = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort,
                                                     o =>
                                                     {
                                                         o.ReconnectInitialDelay = TimeSpan.FromHours(1);
                                                         o.ReconnectMaxDelay = TimeSpan.FromHours(2);
                                                     });
            await peerNode.StartAsync(ct);

            // The hub's graph knows the peer's announcement; its host dialer routes the announced address to the
            // peer's listener (every dial goes through it, NL-1369)
            var dialer = new AnnouncedAddressDialer(new IPEndPoint(IPAddress.Loopback, peerPort));
            await using var hub = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort)
            {
                ConfigureServices = services => services.AddSingleton<IPeerConnectionDialer>(dialer),
                BeforePeerManagerStart = services =>
                {
                    var announcement = new GraphNode(peerKeys.GetNodePubKey(), 1, ReadOnlyMemory<byte>.Empty,
                                                     new byte[GraphNode.AliasLength], new byte[GraphNode.ColorLength],
                                                     [
                                                         AddressDescriptor.FromHost(AddressDescriptorType.IPv4,
                                                                                    AnnouncedHost, AnnouncedPort)
                                                     ]);
                    Assert.True(services.GetRequiredService<IGraphStore>().TryApplyNode(announcement));
                    return Task.CompletedTask;
                }
            };

            // Act
            await hub.StartAsync(ct);

            // Assert: every channel reestablished on both ends, over the hub's dial of the announced address
            var channelIds = hub.Channels.FindChannels(c => c.RemoteNodeId == peerKeys.GetNodePubKey())
                                .Select(c => c.ChannelId)
                                .ToList();
            Assert.Equal(3, channelIds.Count);
            var hubTracker = hub.Services.GetRequiredService<IReestablishTracker>();
            var peerTracker = peerNode.Services.GetRequiredService<IReestablishTracker>();
            using (var timeout = new CancellationTokenSource(s_reestablishTimeout))
                while (!channelIds.All(id => hubTracker.IsReestablished(id) && peerTracker.IsReestablished(id)))
                    await Task.Delay(50, timeout.Token);

            var (dialedNodeId, dialedHost, dialedPort) = Assert.Single(dialer.Dials);
            Assert.Equal(peerKeys.GetNodePubKey(), dialedNodeId);
            Assert.Equal(AnnouncedHost, dialedHost);
            Assert.Equal(AnnouncedPort, dialedPort);

            // The address that connected replaced the inbound-only row (saved after the session was installed), so
            // the next start dials it without the graph
            PeerModel? row;
            using (var timeout = new CancellationTokenSource(s_reestablishTimeout))
                while ((row = await ReadPeerRowAsync(hub, peerKeys.GetNodePubKey())) is null or { IsInboundOnly: true })
                    await Task.Delay(50, timeout.Token);
            Assert.Equal(AnnouncedHost, row.Host);
            Assert.Equal(AnnouncedPort, row.Port);
        }
        finally
        {
            PortPoolUtil.ReleasePort(hubPort);
            PortPoolUtil.ReleasePort(peerPort);
        }
    }

    private static async Task<PeerModel?> ReadPeerRowAsync(ScaleNode node, CompactPubKey nodeId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PeerDbRepository.GetByNodeIdAsync(nodeId);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in _files.SelectMany(f => new[] { f, f + "-wal", f + "-shm" }))
            try
            {
                SqliteTestPools.Clear(file);
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }

        return ValueTask.CompletedTask;
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nltg-inbound-redial-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    /// <summary>Connects a dial of the announced address to the peer's listener and records every dial.</summary>
    private sealed class AnnouncedAddressDialer(IPEndPoint peerListener) : IPeerConnectionDialer
    {
        private readonly ConcurrentQueue<(CompactPubKey NodeId, string Host, int Port)> _dials = new();

        public IReadOnlyList<(CompactPubKey NodeId, string Host, int Port)> Dials => [.. _dials];

        public async ValueTask<Stream?> DialAsync(CompactPubKey nodeId, PeerAddress address,
                                                  CancellationToken cancellationToken)
        {
            _dials.Enqueue((nodeId, address.Host, address.Port));
            if (address.Host != AnnouncedHost || address.Port != AnnouncedPort)
                throw new InvalidOperationException($"Unexpected dial of {address}");

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(peerListener, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}