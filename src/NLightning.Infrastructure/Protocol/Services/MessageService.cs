using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Protocol.Services;

using Domain.Exceptions;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Exceptions;

/// <summary>
/// Service for sending and receiving messages.
/// </summary>
/// <remarks>
/// This class is used to send and receive messages over a transport service.
/// </remarks>
/// <seealso cref="IMessageService" />
internal sealed class MessageService : IMessageService
{
    /// <summary>
    /// The <c>reason</c> tag of a malformed <c>onion_message</c> on <c>nlightning.onion_messages.dropped</c> (NL-444).
    /// </summary>
    internal const string MalformedOnionMessageDropReason = "malformed";

    // The onion-message meter of the Application's OnionMessageMetrics (same meter and instrument names), so a
    // listener or exporter sees the malformed drops, which never reach the onion-message service, with the others
    private static readonly Meter s_onionMessageMeter = new("NLightning.OnionMessages");

    private static readonly Counter<long> s_onionMessagesDropped =
        s_onionMessageMeter.CreateCounter<long>("nlightning.onion_messages.dropped");

    private readonly ILogger<IMessageService> _logger;
    private readonly IMessageSerializer _messageSerializer;
    private readonly ITransportService? _transportService;

    private volatile bool _disposed;
    private readonly object _disposeLock = new();
    private long _malformedGossipCount;
    private long _malformedOnionMessageCount;

    private EventHandler<IMessage?>? _onMessageReceived;
    private bool _listening;

    /// <inheritdoc />
    /// <remarks>
    /// The service only starts listening to the transport (which only then starts reading the socket) when it gets
    /// its first subscriber, so a message the peer sends before anyone listens is not raised to nobody (NL-239).
    /// </remarks>
    public event EventHandler<IMessage?>? OnMessageReceived
    {
        add
        {
            var startListening = false;
            lock (_disposeLock)
            {
                _onMessageReceived += value;
                if (!_listening && !_disposed && _transportService is not null && _onMessageReceived is not null)
                {
                    _listening = true;
                    startListening = true;
                }
            }

            // Outside the lock: the transport may start its read loop, which raises into ReceiveMessage (takes it)
            if (startListening)
                _transportService!.MessageReceived += ReceiveMessage;
        }
        remove
        {
            lock (_disposeLock)
                _onMessageReceived -= value;
        }
    }

    public event EventHandler<Exception>? OnExceptionRaised;

    /// <inheritdoc />
    public bool IsConnected => _transportService?.IsConnected ?? false;

    /// <summary>
    /// Initializes a new <see cref="MessageService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="messageSerializer">The message serializer.</param>
    /// <param name="transportService">The transport service.</param>
    public MessageService(ILogger<IMessageService> logger, IMessageSerializer messageSerializer,
                          ITransportService transportService)
    {
        _logger = logger;
        _messageSerializer = messageSerializer;
        _transportService = transportService;

        _transportService.ExceptionRaised += RaiseException;
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the object is disposed.</exception>
    public async Task SendMessageAsync(IMessage message, bool throwOnException = false,
                                       CancellationToken cancellationToken = default)
    {
        lock (_disposeLock)
            if (_disposed)
            {
                var connectionException = new ConnectionException($"{nameof(MessageService)} was disposed.",
                                                                  new ObjectDisposedException(nameof(MessageService)));
                if (throwOnException)
                    throw connectionException;

                RaiseException(this, connectionException);
                return;
            }

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (_transportService == null)
            {
                var connectionException = new ConnectionException($"{nameof(MessageService)} is not initialized");
                if (throwOnException)
                    throw connectionException;

                RaiseException(this, connectionException);
                return;
            }

            await _transportService.WriteMessageAsync(message, cancellationToken);
        }
        catch (Exception e)
        {
            var connectionException = new ConnectionException("Failed to send message", e);
            if (throwOnException)
                throw connectionException;

            RaiseException(this, connectionException);
        }
    }

    private void ReceiveMessage(object? _, MemoryStream stream)
    {
        Exception? malformedMessageException = null;
        var messageType = PeekMessageType(stream);
        try
        {
            lock (_disposeLock)
            {
                if (_disposed)
                    return;

                IMessage? message;
                try
                {
                    message = _messageSerializer.DeserializeMessageAsync(stream).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    // Handled outside the lock, because it sends a warning (and closes the connection)
                    malformedMessageException = e;
                    message = null;
                }

                if (message is not null)
                {
                    _onMessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to receive message");
            RaiseException(this, e);
        }

        if (malformedMessageException is null)
            return;

        if (messageType is { } type && IsGossipBroadcast(type))
            HandleMalformedGossip(type, malformedMessageException);
        else if (messageType == (ushort)MessageTypes.OnionMessage)
            HandleMalformedOnionMessage(malformedMessageException);
        else
            HandleMalformedMessage(malformedMessageException);
    }

    /// <summary>
    /// The gossip broadcasts (<c>channel_announcement</c>, <c>node_announcement</c>, <c>channel_update</c>): relayed
    /// on behalf of other nodes, so one that does not parse is not the sending peer's own fault.
    /// </summary>
    private static bool IsGossipBroadcast(ushort type) =>
        type is (ushort)MessageTypes.ChannelAnnouncement or (ushort)MessageTypes.NodeAnnouncement
                or (ushort)MessageTypes.ChannelUpdate;

    /// <summary>The message type (the first two bytes, big-endian) without moving the stream; null when too short.</summary>
    private static ushort? PeekMessageType(MemoryStream stream)
    {
        if (stream.Length - stream.Position < 2)
            return null;

        Span<byte> type = stackalloc byte[2];
        var position = stream.Position;
        stream.ReadExactly(type);
        stream.Position = position;
        return BinaryPrimitives.ReadUInt16BigEndian(type);
    }

    /// <summary>
    /// A gossip broadcast we could not parse is ignored and the connection kept. Honest peers relay what they stored,
    /// e.g. LND answers a gossip query with a pre-2022 <c>channel_update</c> without <c>htlc_maximum_msat</c> (128
    /// bytes), which BOLT 7 now requires; closing the connection for it cut every mainnet LND peer off during a sync
    /// (mainnet gossip probe, NL-401). BOLT 7 asks for a <c>warning</c> for invalid keys and lets the receiver keep the
    /// connection, so one <c>warning</c> goes out per connection (the first such message) and the rest is only
    /// logged.
    /// </summary>
    private void HandleMalformedGossip(ushort type, Exception exception)
    {
        var count = Interlocked.Increment(ref _malformedGossipCount);
        var reason = exception.InnerException?.InnerException?.Message ?? exception.InnerException?.Message
                  ?? exception.Message;
        if (count == 1 || count % 1_000 == 0)
            _logger.LogInformation("Ignoring malformed gossip message {Type} from the peer ({Count} so far on this "
                                 + "connection): {Reason}", type, count, reason);
        else
            _logger.LogDebug("Ignoring malformed gossip message {Type}: {Reason}", type, reason);

        if (count == 1)
            SendMessageAsync(new WarningMessage(new ErrorPayload($"Ignoring malformed gossip message {type}: "
                                                               + reason))).GetAwaiter().GetResult();
    }

    /// <summary>
    /// An <c>onion_message</c> (513) we could not parse (a <c>len</c> below 66, a key without a 02/03 prefix, a
    /// truncated packet) is ignored and the connection kept, with no <c>warning</c> (NL-444). BOLT 4 has a reader
    /// ignore every onion message it cannot use and never answer one, and 513 is odd (BOLT 1: odd types may be
    /// ignored), so the BOLT 1 option to warn and close is not taken: with onion messages advertised any peer could
    /// otherwise make us drop its connection. Counted on <c>nlightning.onion_messages.dropped</c> with
    /// <c>reason</c> = <see cref="MalformedOnionMessageDropReason"/>; logged at Information on the first one of the
    /// connection and every 1,000th, at Debug otherwise.
    /// </summary>
    private void HandleMalformedOnionMessage(Exception exception)
    {
        s_onionMessagesDropped.Add(1, new KeyValuePair<string, object?>("reason", MalformedOnionMessageDropReason));
        var count = Interlocked.Increment(ref _malformedOnionMessageCount);
        var reason = exception.InnerException?.InnerException?.Message ?? exception.InnerException?.Message
                  ?? exception.Message;
        if (count == 1 || count % 1_000 == 0)
            _logger.LogInformation("Ignoring malformed onion_message from the peer ({Count} so far on this "
                                 + "connection): {Reason}", count, reason);
        else
            _logger.LogDebug("Ignoring malformed onion_message: {Reason}", reason);
    }

    /// <summary>
    /// Handles a message we could not deserialize: malformed, truncated, or of an unknown even type.
    /// </summary>
    /// <remarks>
    /// BOLT 1: an `error` with an all-zero channel_id makes the peer fail every channel with us, and BOLT 1/2 allow
    /// "send a `warning` and close the connection" here (the offending channel is not known at this layer). So we
    /// send a connection-level warning, then raise a <see cref="ConnectionException"/>, on which
    /// <c>PeerCommunicationService</c> closes the connection. Channel-level rules that fail a channel (e.g.
    /// update_fail_malformed_htlc without BADONION) are checked after deserialization, in the Application layer.
    /// </remarks>
    private void HandleMalformedMessage(Exception exception)
    {
        var message = exception.Message;
        switch (exception)
        {
            case MessageSerializationException { InnerException: PayloadSerializationException pse }:
                message = pse.InnerException is not null ? pse.InnerException.Message : pse.Message;
                break;
            case MessageSerializationException { InnerException: { } innerException }:
                message = innerException.Message;
                break;
            case InvalidMessageException ime:
                message = ime.Message;
                break;
        }

        _logger.LogError(exception, "Failed to deserialize message: {Message}", message);
        SendMessageAsync(new WarningMessage(new ErrorPayload(message))).GetAwaiter().GetResult();
        RaiseException(this, exception);
    }

    private void RaiseException(object? sender, Exception e)
    {
        OnExceptionRaised?.Invoke(sender, new ConnectionException("Error received from transportService", e));
    }

    #region Dispose Pattern

    /// <inheritdoc />
    /// <remarks>
    /// Disposes the TransportService.
    /// </remarks>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;

            if (disposing && _transportService is not null)
            {
                if (_listening)
                    _transportService.MessageReceived -= ReceiveMessage;
                _transportService.ExceptionRaised -= RaiseException;
                _transportService.Dispose();
            }

            _disposed = true;
        }
    }

    ~MessageService()
    {
        Dispose(false);
    }

    #endregion
}