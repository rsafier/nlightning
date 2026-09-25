using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Node.Managers;

using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Constants;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// Service for managing peers.
/// </summary>
/// <remarks>
/// This class is used to manage peers in the network.
/// </remarks>
/// <seealso cref="IPeerManager" />
public sealed class PeerManager : IPeerManager
{
    private readonly IChannelManager _channelManager;
    private readonly ILogger<PeerManager> _logger;
    private readonly IPeerServiceFactory _peerServiceFactory;
    private readonly ITcpService _tcpService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<CompactPubKey, PeerModel> _peers = [];

    private CancellationTokenSource? _cts;

    public PeerManager(IChannelManager channelManager, ILogger<PeerManager> logger,
                       IPeerServiceFactory peerServiceFactory, ITcpService tcpService, IServiceProvider serviceProvider)
    {
        _channelManager = channelManager;
        _logger = logger;
        _peerServiceFactory = peerServiceFactory;
        _tcpService = tcpService;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _tcpService.OnNewPeerConnected += HandleNewPeerConnected;

        _channelManager.OnResponseMessageReady += HandleResponseMessageReady;

        // Load peers and initialize the channel manager
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var peers = await uow.GetPeersForStartupAsync();
        foreach (var peer in peers)
        {
            // Register the peer's channels even if we can't reconnect now, so blockchain events (funding
            // confirmations, stale-channel handling) still apply to them
            RegisterExistingChannels(peer);

            try
            {
                _ = await ConnectToPeerAsync(peer.PeerAddressInfo, uow);
                if (!_peers.ContainsKey(peer.NodeId))
                    // TODO: Retry the connection with backoff
                    _logger.LogWarning("Unable to connect to peer {PeerId} on startup", peer.NodeId);
            }
            catch (ConnectionException)
            {
                _logger.LogWarning("Unable to connect to peer {PeerId} on startup", peer.NodeId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error connecting to peer {PeerId} on startup", peer.NodeId);
            }
        }

        await uow.SaveChangesAsync();

        await _tcpService.StartListeningAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException($"{nameof(PeerManager)} is not running");

        foreach (var peerKey in _peers.Keys)
            try
            {
                DisconnectPeer(peerKey);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error disconnecting peer {Peer}", peerKey);
            }

        try
        {
            // Give it a 5-second timeout to disconnect all peers
            var timeoutTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (_peers.Count > 0 && !_cts.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), timeoutTokenSource.Token);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout while waiting for peers to disconnect");
        }

        await _cts.CancelAsync();
    }

    /// <inheritdoc />
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection to the peer already exists.</exception>
    public async Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo)
    {
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var peer = await ConnectToPeerAsync(peerAddressInfo, uow);

        await uow.SaveChangesAsync();

        return peer;
    }

    /// <inheritdoc />
    public void DisconnectPeer(CompactPubKey pubKey, Exception? exception = null)
    {
        if (_peers.TryGetValue(pubKey, out var peer))
        {
            if (!peer.TryGetPeerService(out var peerService))
            {
                _logger.LogWarning("PeerService not found for {Peer}", pubKey);
                return;
            }

            DisconnectPeer(peerService, exception);
        }
        else
        {
            _logger.LogWarning("Peer {Peer} not found", pubKey);
        }
    }

    public List<PeerModel> ListPeers()
    {
        return _peers.Values.ToList();
    }

    public PeerModel? GetPeer(CompactPubKey peerId)
    {
        return _peers.GetValueOrDefault(peerId);
    }

    private static void DisconnectPeer(IPeerService peerService, Exception? exception = null)
    {
        peerService.Disconnect(exception);
    }

    private void RegisterExistingChannels(PeerModel peer)
    {
        if (peer.Channels is not { Count: > 0 })
            return;

        // Only register channels that are not closed or stale
        foreach (var channel in peer.Channels.Where(c => c.State is not (ChannelState.Closed or ChannelState.Stale)))
        {
            try
            {
                // We don't care about the result here, as we just want to register the existing channels
                _ = _channelManager.RegisterExistingChannelAsync(channel);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error registering channel {ChannelId} for peer {PeerId} on startup",
                                 channel.ChannelId, peer.NodeId);
            }
        }
    }

    private async Task<PeerModel> ConnectToPeerAsync(PeerAddressInfo peerAddressInfo, IUnitOfWork uow)
    {
        // Convert and validate the address
        var peerAddress = new PeerAddress(peerAddressInfo.Address);

        // Check if we're already connected to the peer
        if (_peers.ContainsKey(peerAddress.PubKey))
        {
            throw new InvalidOperationException($"Already connected to peer {peerAddress.PubKey}");
        }

        // Connect to the peer
        var connectedPeer = await _tcpService.ConnectToPeerAsync(peerAddress);

        var peerService = await _peerServiceFactory.CreateConnectedPeerAsync(connectedPeer.CompactPubKey,
                                                                             connectedPeer.TcpClient);
        peerService.OnDisconnect += HandlePeerDisconnection;
        peerService.OnChannelMessageReceived += HandlePeerChannelMessage;

        var preferredHost = connectedPeer.Host;
        var preferredPort = connectedPeer.Port;

        // Check if the node has set it's preferred address
        if (peerService.PreferredHost is not null)
            preferredHost = peerService.PreferredHost;

        if (peerService.PreferredPort is not null)
            preferredPort = peerService.PreferredPort.Value;

        var peer = new PeerModel(connectedPeer.CompactPubKey, preferredHost, preferredPort,
                                 connectedPeer.TcpClient.Client.ProtocolType == ProtocolType.IPv6 ? "IPv6" : "IPv4")
        {
            LastSeenAt = DateTime.UtcNow
        };
        peer.SetPeerService(peerService);

        _peers.Add(connectedPeer.CompactPubKey, peer);

        await uow.PeerDbRepository.AddOrUpdateAsync(peer);

        return peer;
    }

    private void HandleNewPeerConnected(object? _, NewPeerConnectedEventArgs args)
    {
        try
        {
            // Create the peer
            var peerService = _peerServiceFactory.CreateConnectingPeerAsync(args.TcpClient).GetAwaiter().GetResult();
            peerService.OnDisconnect += HandlePeerDisconnection;
            peerService.OnChannelMessageReceived += HandlePeerChannelMessage;

            _logger.LogTrace("PeerService created for peer {PeerPubKey}", peerService.PeerPubKey);

            var preferredHost = args.Host;
            var preferredPort = NodeConstants.DefaultPort;

            // Check if the node has set it's preferred address
            if (peerService.PreferredHost is not null)
                preferredHost = peerService.PreferredHost;

            if (peerService.PreferredPort is not null)
                preferredPort = peerService.PreferredPort.Value;

            var peer = new PeerModel(peerService.PeerPubKey, preferredHost, preferredPort,
                                     args.TcpClient.Client.ProtocolType == ProtocolType.IPv6 ? "IPv6" : "IPv4")
            {
                LastSeenAt = DateTime.UtcNow
            };
            peer.SetPeerService(peerService);

            if (preferredHost != "127.0.0.1")
            {
                // Get a context to save the peer to the database
                using var scope = _serviceProvider.CreateScope();
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                uow.PeerDbRepository.AddOrUpdateAsync(peer);
                uow.SaveChanges();
            }

            _peers.Add(peerService.PeerPubKey, peer);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling new peer connection from {Host}:{Port}", args.Host, args.Port);
        }
    }

    private void HandlePeerDisconnection(object? sender, PeerDisconnectedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        _peers.Remove(args.PeerPubKey);
        _logger.LogInformation("Peer {Peer} disconnected", args.PeerPubKey);

        if (sender is IPeerService peerService)
        {
            peerService.OnDisconnect -= HandlePeerDisconnection;
            peerService.OnChannelMessageReceived -= HandlePeerChannelMessage;
            peerService.Dispose();
        }
        else
        {
            _logger.LogWarning("Peer {Peer} disconnected, but we were unable to detach event handlers",
                               args.PeerPubKey);
        }
    }

    private void HandlePeerChannelMessage(object? _, ChannelMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!_peers.TryGetValue(args.PeerPubKey, out var peer))
            throw new ConnectionException($"Peer {args.PeerPubKey} not found while handling channel message");

        if (!peer.TryGetPeerService(out var peerService))
            throw new ConnectionException(
                $"PeerService not found for peer {args.PeerPubKey} while handling channel message");

        // The id (or temporary id) of the channel this message belongs to, so failures stay scoped to that channel
        var channelId = args.Message.Payload?.ChannelId;

        _channelManager.HandleChannelMessageAsync(args.Message, peerService.Features, peerService.PeerPubKey)
                       .ContinueWith(task => HandleChannelMessageResponseAsync(task, peerService.PeerPubKey,
                                                                               args.Message.Type, channelId));
    }

    /// <summary>
    /// Sends the handler's reply, or turns its failure into a message for the peer.
    /// </summary>
    /// <remarks>
    /// BOLT 1: an `error` with an all-zero channel_id makes the peer fail every channel with us, so a channel failure
    /// always carries <paramref name="channelId"/>:
    /// - <see cref="ChannelErrorException"/>: `error` for the channel, then disconnect.
    /// - <see cref="ChannelWarningException"/> (includes the messages we don't implement yet): `warning` for the
    ///   channel, and the connection stays up.
    /// - Any other exception (an internal failure): `warning` for the channel, then disconnect, so the channel is not
    ///   failed because of our own bug.
    /// </remarks>
    private async Task HandleChannelMessageResponseAsync(Task<IChannelMessage?> task, CompactPubKey peerPubKey,
                                                         MessageTypes messageType, ChannelId? channelId)
    {
        if (!_peers.TryGetValue(peerPubKey, out var peer))
            throw new ConnectionException($"Peer {peerPubKey} not found while handling channel response message");

        if (!peer.TryGetPeerService(out var peerService))
            throw new ConnectionException(
                $"PeerService not found for peer {peerPubKey} while handling channel response message");

        if (task.IsFaulted)
        {
            if (task.Exception is { InnerException: ChannelErrorException cee })
            {
                _logger.LogError(
                    "Error handling channel message ({messageType}) from peer {peer}: {message}",
                    Enum.GetName(messageType), peerService.PeerPubKey,
                    !string.IsNullOrEmpty(cee.PeerMessage)
                        ? cee.PeerMessage
                        : cee.Message);

                if (!IsChannelScoped(cee.ChannelId) && IsChannelScoped(channelId))
                    cee = new ChannelErrorException(cee.Message, channelId, cee, cee.PeerMessage);

                DisconnectPeer(peerService, cee);
                return;
            }

            if (task.Exception is { InnerException: ChannelWarningException cwe })
            {
                _logger.LogWarning(
                    "Error handling channel message ({messageType}) from peer {peer}: {message}",
                    Enum.GetName(messageType), peerService.PeerPubKey,
                    !string.IsNullOrEmpty(cwe.PeerMessage)
                        ? cwe.PeerMessage
                        : cwe.Message);

                if (!IsChannelScoped(cwe.ChannelId) && IsChannelScoped(channelId))
                    cwe = new ChannelWarningException(cwe.Message, channelId, cwe, cwe.PeerMessage);

                _ = peerService.SendWarningAsync(cwe)
                               .ContinueWith(warningTask =>
                                {
                                    if (warningTask.IsFaulted)
                                    {
                                        _logger.LogError(warningTask.Exception,
                                                         "Failed to send warning message to peer {Peer}",
                                                         peerService.PeerPubKey);
                                    }
                                }, TaskContinuationOptions.OnlyOnFaulted);

                return;
            }

            _logger.LogError(
                task.Exception, "Error handling channel message ({messageType}) from peer {peer}",
                Enum.GetName(messageType), peerService.PeerPubKey);

            // Our own failure: tell the peer (never with an `error`, which would fail the channel) and disconnect
            var internalError = task.Exception?.InnerException ?? task.Exception!;
            var warning = IsChannelScoped(channelId)
                              ? new ChannelWarningException($"Internal error handling {Enum.GetName(messageType)}",
                                                            channelId, internalError,
                                                            "Sorry, we had an internal error")
                              : new WarningException("Sorry, we had an internal error");
            DisconnectPeer(peerService, warning);
            return;
        }

        var replyMessage = task.Result;
        if (replyMessage is not null)
            await peerService.SendMessageAsync(replyMessage);
    }

    /// <summary>
    /// True when <paramref name="channelId"/> names a single channel (it is set and not all-zero).
    /// </summary>
    private static bool IsChannelScoped(ChannelId? channelId)
    {
        return channelId is not null && channelId.Value != ChannelId.Zero;
    }

    private void HandleResponseMessageReady(object? sender, ChannelResponseMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Find PeerService by CompactPubKey
        if (!_peers.TryGetValue(args.PeerPubKey, out var peer))
            throw new ConnectionException($"Peer {args.PeerPubKey} not found while handling response message");

        if (!peer.TryGetPeerService(out var peerService))
            throw new ConnectionException(
                $"PeerService not found for peer {args.PeerPubKey} while handling response message");

        // Send the response message to the peer
        peerService.SendMessageAsync(args.ResponseMessage)
                   .ContinueWith(task =>
                    {
                        _logger.LogError(task.Exception, "Failed to send response message to peer {Peer}",
                                         args.PeerPubKey);
                    }, TaskContinuationOptions.OnlyOnFaulted);
    }
}