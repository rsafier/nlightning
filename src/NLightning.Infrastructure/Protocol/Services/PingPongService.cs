using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Protocol.Services;

using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Exceptions;

/// <summary>
/// Service for managing the ping pong protocol.
/// </summary>
/// <remarks>
/// At most one <c>ping</c> is outstanding at a time: the keep-alive loop (<see cref="StartPingAsync"/>) and an
/// on-demand <see cref="PingAsync"/> (ping before <c>commitment_signed</c>, BOLT 2) share it, so a <c>pong</c> always
/// answers the ping whose <c>num_pong_bytes</c> it is checked against.
/// </remarks>
internal class PingPongService : IPingPongService
{
    private readonly Lock _lock = new();
    private readonly IMessageFactory _messageFactory;
    private readonly Random _random = new();

    private PingMessage _lastPing;
    private TaskCompletionSource<bool>? _outstandingPong;

    /// <inheritdoc />
    public event EventHandler<IMessage>? OnPingMessageReady;

    /// <inheritdoc />
    public event EventHandler? OnPongReceived;

    /// <inheritdoc />
    public event EventHandler<Exception>? DisconnectEvent;

    public PingPongService(IMessageFactory messageFactory, IOptions<NodeOptions> nodeOptions)
    {
        _messageFactory = messageFactory;
        _lastPing = messageFactory.CreatePingMessage();
        PongTimeout = nodeOptions.Value.NetworkTimeout;
        PingInterval = nodeOptions.Value.GetEffectivePingInterval();
    }

    /// <summary>The share of <see cref="PingInterval"/> each wait may move either way: 10 %.</summary>
    internal const double PingJitter = 0.1;

    /// <summary>
    /// How long the keep-alive loop waits for a pong: <c>Node:NetworkTimeout</c>, or the Tor network timeout for a
    /// connection through Tor (set by the peer service factory, NL-590).
    /// </summary>
    internal TimeSpan PongTimeout { get; set; }

    /// <summary>
    /// The keep-alive interval: <c>Node:PingInterval</c>, by default 15 s on regtest and 60 s elsewhere (NL-806).
    /// </summary>
    internal TimeSpan PingInterval { get; }

    /// <summary>The wait between two keep-alive pings (<see cref="Task.Delay(TimeSpan, CancellationToken)"/>; tests
    /// replace it to step the loop).</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// The next wait between two keep-alive pings: <see cref="PingInterval"/> moved by up to <see cref="PingJitter"/>
    /// either way, so the pings of many connections do not line up.
    /// </summary>
    internal TimeSpan NextPingDelay()
    {
        double factor;
        lock (_lock)
            factor = 1 + (_random.NextDouble() * 2 - 1) * PingJitter;
        return TimeSpan.FromTicks((long)(PingInterval.Ticks * factor));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A ping goes out at once and then every <see cref="PingInterval"/> (±<see cref="PingJitter"/>, NL-806). If a pong
    /// message is not received within the network timeout, DisconnectEvent is raised: the connection closes and the
    /// reconnect backoff dials again (BOLT 1: MUST NOT fail the channels).
    /// </remarks>
    public async Task StartPingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pongReceivedTask = SendOrJoinPing();

            // Wait for the pong or the network timeout. The timeout task is linked to the shutdown token, so check
            // for shutdown first: only a timeout that is not a shutdown means the peer is unresponsive.
            using (var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var timeoutTask = Task.Delay(PongTimeout, timeoutTokenSource.Token);
                var completedTask = await Task.WhenAny(pongReceivedTask, timeoutTask);
                await timeoutTokenSource.CancelAsync();

                if (cancellationToken.IsCancellationRequested)
                    return;

                if (completedTask != pongReceivedTask)
                {
                    DisconnectEvent?
                       .Invoke(this, new PingTimeoutException("Pong message not received within network timeout."));
                    return;
                }
            }

            try
            {
                await DelayAsync(NextPingDelay(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Joins the ping already in flight, if any. BOLT 1 lets a node that gets no <c>pong</c> close the connection
    /// (never fail the channels), so a timeout also raises <see cref="DisconnectEvent"/>, as the keep-alive loop does:
    /// the channel_reestablish of the next connection then sends what was held back.
    /// </remarks>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pongReceivedTask = SendOrJoinPing();

        using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completedTask = await Task.WhenAny(pongReceivedTask, Task.Delay(timeout, timeoutTokenSource.Token));
        await timeoutTokenSource.CancelAsync();

        if (completedTask == pongReceivedTask)
            return true;

        cancellationToken.ThrowIfCancellationRequested();

        DisconnectEvent?.Invoke(this, new PingTimeoutException($"Pong message not received within {timeout}."));
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Handles a pong message.
    /// If the pong message has a different length than the ping message, DisconnectEvent is raised.
    /// </remarks>
    public void HandlePong(IMessage message)
    {
        TaskCompletionSource<bool>? answered;
        lock (_lock)
        {
            // if the pong message has a different length than the ping message, disconnect
            if (message is not PongMessage pongMessage ||
                pongMessage.Payload.BytesLength != _lastPing.Payload.NumPongBytes)
            {
                answered = null;
            }
            else
            {
                answered = _outstandingPong ?? new TaskCompletionSource<bool>();
                _outstandingPong = null;
            }
        }

        if (answered is null)
        {
            DisconnectEvent?.Invoke(this,
                                    new ConnectionException("Pong message has different length than ping message."));
            return;
        }

        answered.TrySetResult(true);

        OnPongReceived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sends a new ping unless one is outstanding, and returns the task of the pong that answers the outstanding one.
    /// </summary>
    private Task<bool> SendOrJoinPing()
    {
        PingMessage ping;
        TaskCompletionSource<bool> pong;
        lock (_lock)
        {
            if (_outstandingPong is not null)
                return _outstandingPong.Task;

            ping = _messageFactory.CreatePingMessage();
            pong = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastPing = ping;
            _outstandingPong = pong;
        }

        // Raised outside the lock: the handler sends it, and a pong may be handled before it returns
        OnPingMessageReady?.Invoke(this, ping);
        return pong.Task;
    }
}