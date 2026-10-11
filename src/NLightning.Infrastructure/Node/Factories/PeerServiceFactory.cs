using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Node.Factories;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Protocol.Services;
using Services;
using Transport.Tor;

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

        // Create the ping pong service; a connection through Tor waits longer for its pong and init (NL-590)
        var networkTimeout = TorTcpClient.GetNetworkTimeout(_nodeOptions, tcpClient, inbound: false);
        var pingPongService = CreatePingPongService(networkTimeout);

        // Create the communication service
        var communicationService =
            new PeerCommunicationService(commLogger, messageService, _messageFactory, peerPubKey, pingPongService,
                                         _serviceProvider);

        // Create the service
        return new PeerService(communicationService, _nodeOptions.Features, appLogger, networkTimeout,
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
            transportService.Dispose();
            throw new ErrorException("Failed to get remote static public key");
        }

        // Create the message service
        var messageService = _messageServiceFactory.CreateMessageService(transportService);

        // Create the ping pong service; a connection that reached our onion service waits longer (NL-590)
        var networkTimeout = TorTcpClient.GetNetworkTimeout(_nodeOptions, tcpClient, inbound: true);
        var pingPongService = CreatePingPongService(networkTimeout);

        // Create the communication service (infrastructure layer). BOLT 1 (NL-009): as the receiver of the connection
        // our init carries remote_addr: the endpoint the peer connected from, unless it is a private address
        var communicationService = new PeerCommunicationService(commLogger, messageService, _messageFactory,
                                                                transportService.RemoteStaticPublicKey.Value,
                                                                pingPongService, _serviceProvider,
                                                                FromInboundEndPoint(tcpClient.Client.RemoteEndPoint));

        // Create the application service (application layer)
        return new PeerService(communicationService, _nodeOptions.Features, appLogger, networkTimeout,
                               _serviceProvider.GetService<IGossipIngress>(),
                               _serviceProvider.GetService<IGossipSyncService>(),
                               GetPeerStorageService(), _serviceProvider.GetService<IOnionMessageService>());
    }

    /// <summary>
    /// The connection's ping service (transient) with <paramref name="networkTimeout"/> as its pong wait.
    /// </summary>
    private IPingPongService CreatePingPongService(TimeSpan networkTimeout)
    {
        var pingPongService = _serviceProvider.GetRequiredService<IPingPongService>();
        if (pingPongService is PingPongService concrete)
            concrete.PongTimeout = networkTimeout;

        return pingPongService;
    }

    /// <summary>
    /// The BOLT 1 init <c>remote_addr</c> for an inbound connection (NL-009): the connection's remote endpoint as an
    /// address descriptor. Null when there is nothing worth sending: a non-IP endpoint, port 0, or a private address
    /// (BOLT 1: the receiver of an IP connection SHOULD set <c>remote_addr</c> to the remote IP address and port, and
    /// SHOULD NOT set private addresses). Private: this-network, loopback, RFC 1918 and link-local IPv4 (an
    /// IPv4-mapped IPv6 address is taken as its IPv4 form), and loopback, link-local, site-local, unique-local
    /// (fc00::/7) and multicast IPv6.
    /// </summary>
    internal static AddressDescriptor? FromInboundEndPoint(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ipEndPoint || ipEndPoint.Port == 0)
            return null;

        var address = ipEndPoint.Address.IsIPv4MappedToIPv6 ? ipEndPoint.Address.MapToIPv4() : ipEndPoint.Address;
        if (IsPrivate(address))
            return null;

        try
        {
            return AddressDescriptor.FromIpAddress(address, (ushort)ipEndPoint.Port);
        }
        catch (ArgumentException)
        {
            // remote_addr is advisory; an address the descriptor cannot represent (e.g. a scoped IPv6 one) is skipped
            return null;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] is 0 or 10 or 127
              || (bytes[0] == 169 && bytes[1] == 254)
              || (bytes[0] == 172 && (bytes[1] & 0xF0) == 0x10)
              || (bytes[0] == 192 && bytes[1] == 168)
            : IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal
            || (bytes[0] & 0xFE) is 0xFC or 0xFE || bytes.All(b => b == 0);
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