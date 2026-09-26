using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Protocol.Services;

using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

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
    private readonly NodeOptions _nodeOptions;
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
        _nodeOptions = nodeOptions.Value;
        _lastPing = messageFactory.CreatePingMessage();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ping messages are sent to the peer at random intervals ranging from 30 seconds to 5 minutes.
    /// If a pong message is not received within the network timeout, DisconnectEvent is raised.
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
                var timeoutTask = Task.Delay(_nodeOptions.NetworkTimeout, timeoutTokenSource.Token);
                var completedTask = await Task.WhenAny(pongReceivedTask, timeoutTask);
                await timeoutTokenSource.CancelAsync();

                if (cancellationToken.IsCancellationRequested)
                    return;

                if (completedTask != pongReceivedTask)
                {
                    DisconnectEvent?
                       .Invoke(this, new ConnectionException("Pong message not received within network timeout."));
                    return;
                }
            }

            try
            {
                await Task.Delay(_random.Next(30_000, 300_000), cancellationToken);
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

        DisconnectEvent?.Invoke(this, new ConnectionException($"Pong message not received within {timeout}."));
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
            DisconnectEvent?.Invoke(this, new Exception("Pong message has different length than ping message."));
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