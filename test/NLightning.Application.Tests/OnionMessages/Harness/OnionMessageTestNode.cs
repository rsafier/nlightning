using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.OnionMessages.Harness;

using Application.Gossip.Graph.Interfaces;
using Application.OnionMessages;
using Application.Tests.Payments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin;

/// <summary>
/// One in-process onion-message node: the production <see cref="OnionMessageService"/> built by
/// <see cref="OnionMessageServiceCollectionExtensions.AddOnionMessageServices"/> over the real Sphinx and route
/// blinding (<c>AddBitcoinInfrastructure</c>) with its own node key, the harness packet builder, and a peer manager
/// that holds <see cref="LinkedPeerService"/> connections.
/// </summary>
internal sealed class OnionMessageTestNode : IDisposable, IPeerOnionMessageOutbox
{
    private readonly ServiceProvider _provider;
    private readonly ConcurrentDictionary<CompactPubKey, PeerModel> _peers = new();

    public OnionMessageTestNode(string name, byte seed, IEnumerable<IOnionMessageHandler>? handlers = null,
                                IOnionMessageRateLimiter? rateLimiter = null, bool advertiseOnionMessages = true,
                                IGraphStore? graphStore = null, OnionMessageOptions? options = null,
                                TimeProvider? timeProvider = null, bool registerOutbox = true)
    {
        Name = name;
        Options = options ?? new OnionMessageOptions();
        KeyManager = new TestNodeKeyManager(seed);
        var nodeOptions = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest };
        nodeOptions.Features.AllowExperimentalFeatures = true;
        nodeOptions.Features.OptionOnionMessages = advertiseOnionMessages ? FeatureSupport.Optional : FeatureSupport.No;

        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(m => m.GetPeer(It.IsAny<CompactPubKey>()))
                   .Returns((CompactPubKey id) => _peers.GetValueOrDefault(id));
        peerManager.Setup(m => m.ListPeers()).Returns(() => _peers.Values.ToList());
        var channels = new Mock<IChannelMemoryRepository>();
        channels.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                .Returns((Func<ChannelModel, bool> predicate) =>
                {
                    lock (Channels)
                        return Channels.Where(predicate).ToList();
                });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(nodeOptions));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        if (registerOutbox)
            services.AddSingleton<IPeerOnionMessageOutbox>(this);
        services.AddSingleton(peerManager.Object);
        services.AddSingleton(channels.Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<IOnionMessagePacketBuilder>(sp => new HarnessOnionMessagePacketBuilder(
                                                              sp.GetRequiredService<ISphinxService>(),
                                                              sp.GetRequiredService<IRouteBlindingService>()));
        if (rateLimiter is not null)
            services.AddSingleton(rateLimiter);
        if (graphStore is not null)
            services.AddSingleton(graphStore);
        if (timeProvider is not null)
            services.AddSingleton(timeProvider);
        foreach (var handler in handlers ?? [])
            services.AddSingleton(handler);
        services.AddOnionMessageServices();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<OnionMessageService>();
        foreach (var handler in handlers?.OfType<RecordingHandler>() ?? [])
            handler.Service = Service;
    }

    public string Name { get; }

    /// <summary>The node's onion message options (the outbox cap of its links comes from them).</summary>
    public OnionMessageOptions Options { get; }

    /// <summary>The channels <see cref="IChannelMemoryRepository"/> holds (none by default).</summary>
    public List<ChannelModel> Channels { get; } = [];
    public TestNodeKeyManager KeyManager { get; }
    public CompactPubKey NodeId => KeyManager.NodeId;
    public OnionMessageService Service { get; }
    public OnionMessageMetrics Metrics => _provider.GetRequiredService<OnionMessageMetrics>();
    public IRouteBlindingService RouteBlinding => _provider.GetRequiredService<IRouteBlindingService>();
    public ISphinxService Sphinx => _provider.GetRequiredService<ISphinxService>();
    public MessagePathFactory PathFactory => new(RouteBlinding);

    public HarnessOnionMessagePacketBuilder Builder =>
        (HarnessOnionMessagePacketBuilder)_provider.GetRequiredService<IOnionMessagePacketBuilder>();

    /// <summary>Our end of the connection to <paramref name="other"/>.</summary>
    public LinkedPeerService LinkTo(OnionMessageTestNode other)
    {
        _peers[other.NodeId].TryGetPeerService(out var service);
        return (LinkedPeerService)service!;
    }

    /// <summary>Adds our end of a connection.</summary>
    public void AddPeer(LinkedPeerService link)
    {
        var peer = new PeerModel(link.Remote.NodeId, "127.0.0.1", 9735, "harness");
        peer.SetPeerService(link);
        _peers[link.Remote.NodeId] = peer;
    }

    /// <summary>Drops our end of the connection to <paramref name="other"/>.</summary>
    public void RemovePeer(OnionMessageTestNode other)
    {
        if (_peers.TryRemove(other.NodeId, out var peer) && peer.TryGetPeerService(out var service))
            service.Dispose();
    }

    /// <summary>
    /// Connects two nodes; <paramref name="onionMessages"/> is whether the connection negotiated
    /// <c>option_onion_messages</c>.
    /// </summary>
    public static void Connect(OnionMessageTestNode a, OnionMessageTestNode b, bool onionMessages = true)
    {
        var features = new FeatureOptions
        {
            OptionOnionMessages = onionMessages ? FeatureSupport.Optional : FeatureSupport.No
        };
        var aToB = new LinkedPeerService(b, features, a.Options.MaxOutboxPerPeer);
        var bToA = new LinkedPeerService(a, features, b.Options.MaxOutboxPerPeer);
        aToB.Reverse = bToA;
        bToA.Reverse = aToB;
        a.AddPeer(aToB);
        b.AddPeer(bToA);
    }

    /// <summary>The send path, as <c>PeerManager</c> implements it: only a connected peer that negotiated it.</summary>
    public bool CanSendOnionMessage(CompactPubKey peerNodeId) =>
        TryGetLink(peerNodeId) is { Features.OptionOnionMessages: not FeatureSupport.No };

    /// <summary>Queues on the link's outbox, never blocking.</summary>
    public bool TryEnqueueOnionMessage(CompactPubKey peerNodeId, OnionMessageMessage message) =>
        CanSendOnionMessage(peerNodeId) && TryGetLink(peerNodeId)!.TryEnqueueOnionMessage(message);

    public void Dispose()
    {
        foreach (var peer in _peers.Values)
        {
            if (peer.TryGetPeerService(out var service))
                service.Dispose();
        }

        _provider.Dispose();
    }

    private LinkedPeerService? TryGetLink(CompactPubKey nodeId) =>
        _peers.TryGetValue(nodeId, out var peer) && peer.TryGetPeerService(out var service)
            ? service as LinkedPeerService
            : null;
}