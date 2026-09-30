using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Node.Factories;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Services;

/// <summary>
/// Factory for creating peer services.
/// </summary>
public class PeerServiceFactory : IPeerServiceFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageServiceFactory _messageServiceFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ITransportServiceFactory _transportServiceFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly NodeOptions _nodeOptions;

    private int _missingPeerStorageLogged;

    public PeerServiceFactory(ILoggerFactory loggerFactory, IMessageFactory messageFactory,
                              IMessageServiceFactory messageServiceFactory, ISecureKeyManager secureKeyManager,
                              ITransportServiceFactory transportServiceFactory, IOptions<NodeOptions> nodeOptions,
                              IServiceProvider serviceProvider)
    {
        _loggerFactory = loggerFactory;
        _messageFactory = messageFactory;
        _messageServiceFactory = messageServiceFactory;
        _secureKeyManager = secureKeyManager;
        _transportServiceFactory = transportServiceFactory;
        _serviceProvider = serviceProvider;
        _nodeOptions = nodeOptions.Value;
    }

    /// <inheritdoc />
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    public async Task<IPeerService> CreateConnectedPeerAsync(CompactPubKey peerPubKey, TcpClient tcpClient)
    {
        // Create a specific logger for the communication service
        var commLogger = _loggerFactory.CreateLogger<PeerCommunicationService>();
        var appLogger = _loggerFactory.CreateLogger<PeerService>();

        // Create and Initialize the transport service. The handshake's static ECDH runs through the key manager
        // (like the onion peel), so the node private key never leaves locked memory (NL-436)
        var transportService =
            _transportServiceFactory.CreateTransportService(true, _secureKeyManager.GetNodePubKey(), peerPubKey,
                                                            tcpClient,
                                                            _secureKeyManager.ComputeNodeSharedSecret);

        try
        {
            await transportService.InitializeAsync();
        }
        catch (Exception ex)
        {
            transportService.Dispose();
            throw new ConnectionException($"Error connecting to peer {peerPubKey}", ex);
        }

        // Create the message service
        var messageService = _messageServiceFactory.CreateMessageService(transportService);

        // Create the ping pong service
        var pingPongService = _serviceProvider.GetRequiredService<IPingPongService>();

        // Create the communication service
        var communicationService =
            new PeerCommunicationService(commLogger, messageService, _messageFactory, peerPubKey, pingPongService,
                                         _serviceProvider);

        // Create the service
        return new PeerService(communicationService, _nodeOptions.Features, appLogger, _nodeOptions.NetworkTimeout,
                               _serviceProvider.GetService<IGossipIngress>(),
                               _serviceProvider.GetService<IGossipSyncService>(),
                               GetPeerStorageService(), _serviceProvider.GetService<IOnionMessageService>());
    }

    /// <inheritdoc />
    /// <exception cref="ConnectionException">Thrown when the connection to the peer fails.</exception>
    public async Task<IPeerService> CreateConnectingPeerAsync(TcpClient tcpClient)
    {
        // Create loggers
        var commLogger = _loggerFactory.CreateLogger<PeerCommunicationService>();
        var appLogger = _loggerFactory.CreateLogger<PeerService>();

        var remoteEndPoint =
            (IPEndPoint)(tcpClient.Client.RemoteEndPoint ?? throw new Exception("Failed to get remote endpoint"));
        var ipAddress = remoteEndPoint.Address.ToString();
        var port = remoteEndPoint.Port;

        // Create and Initialize the transport service. The responder's remote static key arrives in act three, so
        // the own public key is the placeholder the old call passed too; the static ECDH runs through the key
        // manager (NL-436)
        var localStaticPublicKey = _secureKeyManager.GetNodePubKey();
        var transportService =
            _transportServiceFactory.CreateTransportService(false, localStaticPublicKey, localStaticPublicKey,
                                                            tcpClient,
                                                            _secureKeyManager.ComputeNodeSharedSecret);
        try
        {
            await transportService.InitializeAsync();
        }
        catch (Exception ex)
        {
            transportService.Dispose();
            throw new ConnectionException($"Error establishing connection to peer {ipAddress}:{port}", ex);
        }

        if (transportService.RemoteStaticPublicKey is null)
        {
            throw new ErrorException("Failed to get remote static public key");
        }

        // Create the message service
        var messageService = _messageServiceFactory.CreateMessageService(transportService);

        // Create the ping pong service
        var pingPongService = _serviceProvider.GetRequiredService<IPingPongService>();

        // Create the communication service (infrastructure layer)
        var communicationService = new PeerCommunicationService(commLogger, messageService, _messageFactory,
                                                                transportService.RemoteStaticPublicKey.Value,
                                                                pingPongService, _serviceProvider);

        // Create the application service (application layer)
        return new PeerService(communicationService, _nodeOptions.Features, appLogger, _nodeOptions.NetworkTimeout,
                               _serviceProvider.GetService<IGossipIngress>(),
                               _serviceProvider.GetService<IGossipSyncService>(),
                               GetPeerStorageService(), _serviceProvider.GetService<IOnionMessageService>());
    }

    /// <summary>
    /// The registered peer storage service, or null. Offering <c>option_provide_storage</c> without one breaks BOLT 1
    /// (a peer with a channel MUST get its blob stored), so that misconfiguration is logged as an error once.
    /// </summary>
    private IPeerStorageService? GetPeerStorageService()
    {
        var peerStorage = _serviceProvider.GetService<IPeerStorageService>();
        if (peerStorage is null && _nodeOptions.Features.OptionProvideStorage != FeatureSupport.No
                                && Interlocked.Exchange(ref _missingPeerStorageLogged, 1) == 0)
            _loggerFactory.CreateLogger<PeerServiceFactory>().LogError(
                "option_provide_storage is advertised but no peer storage service is registered: peer_storage "
              + "messages are dropped. Register AddPeerStorageServices() or set "
              + "Node:Features:OptionProvideStorage to No");

        return peerStorage;
    }
}