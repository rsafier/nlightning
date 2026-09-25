using System.Net;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

// TODO: Eventually move this to the Application layer
/// <summary>
/// Service for peer communication
/// </summary>
public sealed class PeerService : IPeerService
{
    private readonly IPeerCommunicationService _peerCommunicationService;
    private readonly ILogger<PeerService> _logger;

    private bool _isInitialized;

    /// <inheritdoc/>
    public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect;

    /// <inheritdoc/>
    public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived;

    /// <inheritdoc/>
    public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived;

    /// <inheritdoc/>
    public event EventHandler<Exception>? OnExceptionRaised;

    /// <inheritdoc/>
    public CompactPubKey PeerPubKey => _peerCommunicationService.PeerCompactPubKey;

    public string? PreferredHost { get; private set; }
    public ushort? PreferredPort { get; private set; }

    public FeatureOptions Features { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerService"/> class.
    /// </summary>
    /// <param name="peerCommunicationService">The peer communication service</param>
    /// <param name="features">The feature options</param>
    /// <param name="logger">A logger</param>
    /// <param name="networkTimeout">Network timeout</param>
    public PeerService(IPeerCommunicationService peerCommunicationService, FeatureOptions features,
                       ILogger<PeerService> logger, TimeSpan networkTimeout)
    {
        _peerCommunicationService = peerCommunicationService;
        Features = features;
        _logger = logger;

        // Set up event handlers
        _peerCommunicationService.MessageReceived += HandleMessage;
        _peerCommunicationService.ExceptionRaised += HandleException;
        _peerCommunicationService.DisconnectEvent += HandleDisconnection;

        // Initialize communication
        try
        {
            _peerCommunicationService.InitializeAsync(networkTimeout).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new ErrorException("Error initializing peer communication", e);
        }
    }

    /// <summary>
    /// Disconnects from the peer.
    /// </summary>
    public void Disconnect(Exception? exception = null)
    {
        _logger.LogInformation("Disconnecting peer {peer}", PeerPubKey);
        _peerCommunicationService.Disconnect(exception);
    }

    public Task SendMessageAsync(IChannelMessage replyMessage)
    {
        return _peerCommunicationService.SendMessageAsync(replyMessage);
    }

    public Task SendWarningAsync(WarningException we)
    {
        return _peerCommunicationService.SendWarningAsync(we);
    }

    /// <summary>
    /// Handles messages received from the peer.
    /// </summary>
    private void HandleMessage(object? sender, IMessage? message)
    {
        if (message is null)
            return;

        if (!_isInitialized)
        {
            HandleInitialization(message);
        }
        else if (message is IChannelMessage channelMessage)
        {
            _logger.LogTrace("Received channel message ({messageType}) from peer {peer}",
                             Enum.GetName(message.Type), PeerPubKey);

            OnChannelMessageReceived?.Invoke(this, new ChannelMessageEventArgs(channelMessage, PeerPubKey));
        }
        else if (message is ErrorMessage errorMessage)
        {
            var errorMessageString = string.Empty;
            ChannelId? channelId = null;
            if (errorMessage.Payload.ChannelId != ChannelId.Zero)
                channelId = errorMessage.Payload.ChannelId;

            if (errorMessage.Payload.Data is not null)
            {
                // Try to get utf8 string from error data
                errorMessageString = Utf8.IsValid(errorMessage.Payload.Data)
                                         ? System.Text.Encoding.UTF8.GetString(errorMessage.Payload.Data)
#if NET9_0_OR_GREATER
                                         : Convert.ToHexStringLower(errorMessage.Payload.Data);
#else
                                         : Convert.ToHexString(errorMessage.Payload.Data).ToLowerInvariant();
#endif

                _logger.LogError(
                    "Received error message from peer {peer} for channel {channelId}: {errorMessage}",
                    PeerPubKey, channelId is null ? "" : channelId.ToString(), errorMessageString);
            }

            OnAttentionMessageReceived?.Invoke(
                this, new AttentionMessageEventArgs(errorMessageString, PeerPubKey, channelId));
        }
        else if (message is WarningMessage warningMessage)
        {
            var warningMessageString = string.Empty;
            ChannelId? channelId = null;
            if (warningMessage.Payload.ChannelId != ChannelId.Zero)
                channelId = warningMessage.Payload.ChannelId;

            if (warningMessage.Payload.Data is not null)
            {
                // Try to get utf8 string from error data
                warningMessageString = Utf8.IsValid(warningMessage.Payload.Data)
                                           ? System.Text.Encoding.UTF8.GetString(warningMessage.Payload.Data)
#if NET9_0_OR_GREATER
                                           : Convert.ToHexStringLower(warningMessage.Payload.Data);
#else
                                         : Convert.ToHexString(warningMessage.Payload.Data).ToLowerInvariant();
#endif

                _logger.LogError(
                    "Received error message from peer {peer} for channel {channelId}: {errorMessage}",
                    PeerPubKey, channelId is null ? "" : channelId.ToString(), warningMessageString);
            }

            OnAttentionMessageReceived?.Invoke(
                this, new AttentionMessageEventArgs(warningMessageString, PeerPubKey, channelId));
        }
        else if (message is StfuMessage stfuMessage)
        {
            // Quiescence (BOLT 2, option_quiesce) is not implemented, so we can never reply with our own stfu. The
            // sender now considers the channel quiescing and stops sending updates; the only spec-defined way out is
            // a disconnection. So send a channel-scoped warning and disconnect (the channel is NOT failed).
            _logger.LogWarning("Received stfu for channel {channelId} from peer {peer}, but quiescence is not supported",
                               stfuMessage.Payload.ChannelId, PeerPubKey);

            Disconnect(new ChannelWarningException("Received stfu, but quiescence is not supported",
                                                   stfuMessage.Payload.ChannelId,
                                                   "Quiescence (stfu) is not supported"));
        }
        else if (message is GossipMessage)
        {
            // BOLT 7 gossip is not implemented yet: accept the message so the connection stays up, and drop it
            _logger.LogDebug("Dropping gossip message ({messageType}) from peer {peer}",
                             Enum.GetName(message.Type), PeerPubKey);
        }
    }

    /// <summary>
    /// Handles exceptions raised by the communication service.
    /// </summary>
    private void HandleException(object? sender, Exception e)
    {
        _logger.LogError(e, "Exception occurred with peer {peer}", PeerPubKey);
        OnExceptionRaised?.Invoke(this, e);
    }

    private void HandleDisconnection(object? sender, Exception e)
    {
        _logger.LogTrace(e, "Handling disconnection for peer {Peer}", PeerPubKey);
        OnDisconnect?.Invoke(this, new PeerDisconnectedEventArgs(PeerPubKey, e));
    }

    /// <summary>
    /// Handles the initialization process when receiving the first message.
    /// </summary>
    private void HandleInitialization(IMessage message)
    {
        // Check if the first message is an init message
        if (message.Type != MessageTypes.Init || message is not InitMessage initMessage)
        {
            _logger.LogError("Failed to receive init message from peer {peer}", PeerPubKey);
            // BOLT 1: we must not send anything before receiving init, so just close the connection
            Disconnect(new ConnectionException("Expected init as the first message"));
            return;
        }

        // Check if Features are compatible
        if (!Features.GetNodeFeatures().IsCompatible(initMessage.Payload.FeatureSet, out var negotiatedFeatures)
         || negotiatedFeatures is null)
        {
            _logger.LogError("Peer {peer} is not compatible", PeerPubKey);
            Disconnect(new WarningException("Incompatible features"));
            return;
        }

        // BOLT 1: only close the connection if `networks` has no chain in common with ours
        var networkChainHashes = initMessage.NetworksTlv?.ChainHashes;
        if (networkChainHashes != null && !networkChainHashes.Any(chainHash => Features.ChainHashes.Contains(chainHash)))
        {
            _logger.LogError("Peer {peer} chain is not compatible", PeerPubKey);
            Disconnect(new WarningException("No common chain in networks"));
            return;
        }

        if (initMessage.RemoteAddressTlv is not null)
        {
            switch (initMessage.RemoteAddressTlv.AddressType)
            {
                case 1 or 2:
                    {
                        if (!IPAddress.TryParse(initMessage.RemoteAddressTlv.Address, out var ipAddress))
                        {
                            _logger.LogWarning("Peer {peer} has an invalid remote address: {address}",
                                               PeerPubKey, initMessage.RemoteAddressTlv.Address);
                        }
                        else
                        {
                            PreferredHost = ipAddress.ToString();
                            PreferredPort = initMessage.RemoteAddressTlv.Port;
                        }

                        break;
                    }
                case 5:
                    PreferredHost = initMessage.RemoteAddressTlv.Address;
                    PreferredPort = initMessage.RemoteAddressTlv.Port;
                    break;
                default:
                    _logger.LogWarning("Peer {peer} has an unsupported remote address type: {addressType}",
                                       PeerPubKey, initMessage.RemoteAddressTlv.AddressType);
                    break;
            }
        }

        Features = FeatureOptions.GetNodeOptions(negotiatedFeatures, initMessage.Extension);
        _logger.LogTrace("Initialization from peer {peer} completed successfully", PeerPubKey);
        _isInitialized = true;
    }

    public void Dispose()
    {
        _peerCommunicationService.MessageReceived -= HandleMessage;
        _peerCommunicationService.ExceptionRaised -= HandleException;
        _peerCommunicationService.DisconnectEvent -= HandleDisconnection;
        _peerCommunicationService.Dispose();
    }
}