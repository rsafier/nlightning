using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Node.Services;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Service for communication with a single peer.
/// </summary>
public class PeerCommunicationService : IPeerCommunicationService
{
    /// <summary>
    /// Pings asking for this many pong bytes or more must not be answered (BOLT 1).
    /// </summary>
    private const ushort IgnorePingNumPongBytes = 65532;

    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<PeerCommunicationService> _logger;
    private readonly IMessageService _messageService;
    private readonly IPingPongService _pingPongService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessageFactory _messageFactory;
    private readonly TaskCompletionSource<bool> _pingPongTcs = new();
    private readonly Lock _pingStartLock = new();

    private volatile bool _isInitialized;
    private bool _initSent;
    private bool _pingStarted;
    private int _disconnecting;
    private CancellationTokenSource? _initWaitCancellationTokenSource;
    private EventHandler<IMessage?>? _messageReceived;
    private int _listening;

    /// <inheritdoc />
    /// <remarks>
    /// The service only starts listening to the peer (and so reading the socket) when it gets its first subscriber:
    /// the peer's <c>init</c> often arrives right after the handshake, before the peer service above us subscribed,
    /// and was lost (NL-239).
    /// </remarks>
    public event EventHandler<IMessage?>? MessageReceived
    {
        add
        {
            lock (_pingStartLock)
                _messageReceived += value;

            if (value is not null && Interlocked.Exchange(ref _listening, 1) == 0)
                _messageService.OnMessageReceived += HandleMessageReceived;
        }
        remove
        {
            lock (_pingStartLock)
                _messageReceived -= value;
        }
    }

    /// <inheritdoc />
    public event EventHandler<Exception?>? DisconnectEvent;

    /// <inheritdoc />
    public event EventHandler<Exception>? ExceptionRaised;

    /// <inheritdoc />
    public bool IsConnected => _messageService.IsConnected;

    /// <inheritdoc />
    public CompactPubKey PeerCompactPubKey { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCommunicationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="messageService">The message service.</param>
    /// <param name="messageFactory">The message factory.</param>
    /// <param name="peerCompactPubKey">The peer's public key.</param>
    /// <param name="pingPongService">The ping pong service.</param>
    /// <param name="serviceProvider">The service provider.</param>
    public PeerCommunicationService(ILogger<PeerCommunicationService> logger, IMessageService messageService,
                                    IMessageFactory messageFactory, CompactPubKey peerCompactPubKey,
                                    IPingPongService pingPongService, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _messageService = messageService;
        _messageFactory = messageFactory;
        PeerCompactPubKey = peerCompactPubKey;
        _pingPongService = pingPongService;
        _serviceProvider = serviceProvider;

        _messageService.OnExceptionRaised += HandleExceptionRaised;
        _pingPongService.DisconnectEvent += HandlePingPongDisconnect;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(TimeSpan networkTimeout)
    {
        _logger.LogTrace("Waiting for init message from peer {peer}", PeerCompactPubKey);

        // Set timeout to close connection if the other peer doesn't send an init message
        _initWaitCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Delay(networkTimeout, _initWaitCancellationTokenSource.Token).ContinueWith(task =>
        {
            if (!task.IsCanceled && !_isInitialized)
            {
                RaiseException(
                    new ConnectionException($"Peer {PeerCompactPubKey} did not send an init message before timeout"));
            }
        });

        // Always send an init message upon connection
        _logger.LogTrace("Sending init message to peer {peer}", PeerCompactPubKey);
        var initMessage = _messageFactory.CreateInitMessage();
        try
        {
            await _messageService.SendMessageAsync(initMessage, true, _cts.Token);
        }
        catch (Exception e)
        {
            _pingPongTcs.TrySetResult(true);
            throw new ConnectionException($"Failed to send init message to peer {PeerCompactPubKey}", e);
        }

        // Set up ping service to keep connection alive (it only starts once the peer's init has arrived too)
        if (!_cts.IsCancellationRequested)
        {
            if (!_messageService.IsConnected)
                throw new ConnectionException($"Failed to connect to peer {PeerCompactPubKey}");

            lock (_pingStartLock)
                _initSent = true;

            TryStartPingPongService();
        }
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _messageService.SendMessageAsync(message, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            RaiseException(new ConnectionException($"Failed to send message to peer {PeerCompactPubKey}", ex));
        }
    }

    /// <inheritdoc/>
    public async Task SendWarningAsync(WarningException we, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = we.Message;
            ChannelId? channelId = null;
            if (we is ChannelWarningException cwe)
            {
                message = cwe.PeerMessage ?? we.Message;
                channelId = cwe.ChannelId;
            }

            var warningMessage = _messageFactory.CreateWarningMessage(message, channelId);
            await _messageService.SendMessageAsync(warningMessage, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            RaiseException(new ConnectionException($"Failed to send message to peer {PeerCompactPubKey}", ex));
        }
    }

    /// <inheritdoc />
    public void Disconnect(Exception? exception = null)
    {
        // Only the first caller disconnects, so DisconnectEvent fires once and we never touch a disposed _cts
        if (Interlocked.Exchange(ref _disconnecting, 1) == 1)
            return;

        try
        {
            SendExceptionMessage(exception).GetAwaiter().GetResult();

            bool pingStarted;
            lock (_pingStartLock)
                pingStarted = _pingStarted;

            _ = _cts.CancelAsync();
            if (!pingStarted)
                _pingPongTcs.TrySetResult(true);

            _logger.LogTrace("Waiting for ping service to stop for peer {peer}", PeerCompactPubKey);
            _pingPongTcs.Task.Wait(TimeSpan.FromSeconds(5));
            _logger.LogTrace("Ping service stopped for peer {peer}", PeerCompactPubKey);

            // Actually close the connection now that the error/warning is out, even if nobody disposes us (e.g. the
            // peer is still being set up and has no disconnect subscriber yet). Disposing twice is harmless.
            // Dispose on another thread: Disconnect often runs on the transport read loop (e.g. an init rejection
            // raised from MessageService.ReceiveMessage), and TransportService.Dispose waits for that loop to end, so
            // disposing inline would block this thread (and MessageService's dispose lock) for its 5 s timeout.
            _ = Task.Run(_messageService.Dispose);
        }
        finally
        {
            DisconnectEvent?.Invoke(this, exception);
        }
    }

    /// <summary>
    /// Starts the ping loop once both our init has been sent and the peer's init has been received (BOLT 1: no other
    /// message before init in either direction), so the first ping is actually sent and its pong timeout is real.
    /// </summary>
    private void TryStartPingPongService()
    {
        lock (_pingStartLock)
        {
            if (_pingStarted || !_initSent || !_isInitialized || Volatile.Read(ref _disconnecting) == 1
             || _cts.IsCancellationRequested)
                return;

            _pingStarted = true;
        }

        SetupPingPongService();
    }

    private void SetupPingPongService()
    {
        _pingPongService.OnPingMessageReady += HandlePingMessageReady;
        _pingPongService.OnPongReceived += HandlePongReceived;

        // Setup Ping to keep connection alive
        _ = _pingPongService.StartPingAsync(_cts.Token).ContinueWith(_ =>
        {
            _logger.LogTrace("Ping service stopped for peer {peer}, setting result", PeerCompactPubKey);
            _pingPongTcs.TrySetResult(true);
        });

        _logger.LogInformation("Ping service started for peer {peer}", PeerCompactPubKey);
    }

    private void HandlePongReceived(object? sender, EventArgs e)
    {
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        uow.PeerDbRepository.UpdatePeerLastSeenAsync(PeerCompactPubKey).GetAwaiter().GetResult();
        uow.SaveChanges();
    }

    private void HandlePingMessageReady(object? sender, IMessage pingMessage)
    {
        // We can only send ping messages if the peer is initialized
        if (!_isInitialized)
            return;

        try
        {
            _messageService.SendMessageAsync(pingMessage, cancellationToken: _cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            RaiseException(new ConnectionException($"Failed to send ping message to peer {PeerCompactPubKey}", ex));
        }
    }

    private void HandleMessageReceived(object? sender, IMessage? message)
    {
        if (message is null)
        {
            return;
        }

        if (!_isInitialized && message.Type == MessageTypes.Init)
        {
            _isInitialized = true;
            _initWaitCancellationTokenSource?.Cancel();
        }

        // Forward the message to subscribers
        _messageReceived?.Invoke(this, message);

        // Start pinging once the peer's init was accepted by the subscribers (they disconnect on a bad init)
        if (message.Type == MessageTypes.Init)
            TryStartPingPongService();

        // Handle ping messages internally
        if (_isInitialized && message.Type == MessageTypes.Ping)
        {
            // BOLT 1: a ping with num_pong_bytes >= 65532 MUST be ignored (no pong)
            if (message is PingMessage { Payload.NumPongBytes: >= IgnorePingNumPongBytes })
            {
                _logger.LogTrace("Ignoring ping with num_pong_bytes >= {threshold} from peer {peer}",
                                 IgnorePingNumPongBytes, PeerCompactPubKey);
                return;
            }

            _ = HandlePingAsync(message);
        }
        else if (_isInitialized && message.Type == MessageTypes.Pong)
        {
            _pingPongService.HandlePong(message);
        }
    }

    private async Task HandlePingAsync(IMessage pingMessage)
    {
        var pongMessage = _messageFactory.CreatePongMessage(pingMessage);
        await _messageService.SendMessageAsync(pongMessage);
    }

    private void HandleExceptionRaised(object? sender, Exception e)
    {
        RaiseException(e);
    }

    private void HandlePingPongDisconnect(object? sender, Exception e)
    {
        // Pong timeout or mismatched pong: forward the reason and close the connection (BOLT 1 allows it, and the
        // channels must not be failed). Disconnect on another thread, because it waits for the ping loop, which is
        // the one raising this event.
        ExceptionRaised?.Invoke(this, e);

        if (Volatile.Read(ref _disconnecting) == 1)
            return;

        _logger.LogWarning(e, "Disconnecting peer {peer} because of a ping/pong failure", PeerCompactPubKey);
        _ = Task.Run(() => Disconnect(e));
    }

    /// <summary>
    /// Tells the peer why we are failing: nothing for a <see cref="ConnectionException"/>, an `error` for a
    /// <see cref="ChannelErrorException"/> that names its channel, and a `warning` otherwise.
    /// </summary>
    /// <remarks>
    /// BOLT 1: an `error` with an all-zero channel_id makes the peer fail every channel it has with us. Nothing we do
    /// today is meant to do that, so an <see cref="ErrorException"/> without a channel id (including a
    /// <see cref="ChannelErrorException"/> that lost its id) is sent as a connection-level `warning` instead.
    /// </remarks>
    private Task SendExceptionMessage(Exception? exception)
    {
        switch (exception)
        {
            case ConnectionException:
            case null:
                return Task.CompletedTask;
            case ChannelErrorException { ChannelId: { } channelId } channelErrorException
                when channelId != ChannelId.Zero:
                {
                    var message = !string.IsNullOrWhiteSpace(channelErrorException.PeerMessage)
                                      ? channelErrorException.PeerMessage
                                      : channelErrorException.Message;

                    _logger.LogTrace("Sending error message to peer {peer}. ChannelId: {channelId}, Message: {message}",
                                     PeerCompactPubKey, channelId, message);

                    return _messageService.SendMessageAsync(
                        new ErrorMessage(new ErrorPayload(channelId, message)));
                }
            case ErrorException errorException:
                {
                    var message = errorException is ChannelErrorException
                    {
                        PeerMessage: { } peerMessage
                    } && !string.IsNullOrWhiteSpace(peerMessage)
                                      ? peerMessage
                                      : errorException.Message;

                    _logger.LogWarning(
                        "Not sending an all-zero channel_id error to peer {peer}, sending a warning instead: {message}",
                        PeerCompactPubKey, message);

                    return _messageService.SendMessageAsync(new WarningMessage(new ErrorPayload(message)));
                }
            case WarningException warningException:
                {
                    ChannelId? channelId = null;
                    var message = warningException.Message;

                    if (warningException is ChannelWarningException channelWarningException)
                    {
                        if (channelWarningException.ChannelId != ChannelId.Zero)
                            channelId = channelWarningException.ChannelId;
                        if (!string.IsNullOrWhiteSpace(channelWarningException.PeerMessage))
                            message = channelWarningException.PeerMessage;
                    }

                    _logger.LogTrace("Sending warning message to peer {peer}. ChannelId: {channelId}, Message: {message}",
                                     PeerCompactPubKey, channelId, message);

                    return _messageService.SendMessageAsync(
                        new WarningMessage(new ErrorPayload(channelId, message)));
                }
            default:
                return Task.CompletedTask;
        }
    }

    private void RaiseException(Exception exception)
    {
        // Forward the exception to subscribers
        ExceptionRaised?.Invoke(this, exception);

        if (exception is not ErrorException)
        {
            _ = Task.Run(() => SendExceptionMessage(exception));
            return;
        }

        // Disconnect if not already disconnecting (Disconnect sends the error/warning before closing the connection)
        if (Volatile.Read(ref _disconnecting) == 0)
        {
            _logger.LogWarning(exception, "We're disconnecting peer {peer} because of an exception",
                               PeerCompactPubKey);
            Disconnect(exception);
        }
    }

    public void Dispose()
    {
        // Unsubscribe from events
        if (Volatile.Read(ref _listening) == 1)
            _messageService.OnMessageReceived -= HandleMessageReceived;
        _messageService.OnExceptionRaised -= HandleExceptionRaised;
        _pingPongService.DisconnectEvent -= HandlePingPongDisconnect;

        // A Disconnect after Dispose must not touch the disposed _cts
        Interlocked.Exchange(ref _disconnecting, 1);

        _cts.Dispose();
        _messageService.Dispose();
        _initWaitCancellationTokenSource?.Dispose();
    }
}