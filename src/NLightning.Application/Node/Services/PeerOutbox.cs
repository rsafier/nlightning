using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Node.Services;

using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The single, ordered send path for one peer's channel messages (BOLT2 plan D2, §3.7; NL-193).
/// </summary>
/// <remarks>
/// Handler replies, messages raised through <c>IChannelManager.OnResponseMessageReady</c>, channel warnings, our own
/// <c>channel_update</c>s (after the channel_ready they follow) and the final error/warning of a disconnect all go
/// through one FIFO queue, drained by one loop that awaits each send before
/// starting the next. Enqueueing never blocks, so it is safe while holding a channel lock: whatever is enqueued under
/// the lock reaches the wire in that order. A disconnect is terminal: it is sent after everything queued before it,
/// and anything enqueued after it is dropped.
/// <para>
/// NL-360: the gossip share is bounded. <see cref="QueuedGossipCount"/> counts the gossip messages not sent yet, and
/// <see cref="TryEnqueueGossip(IMessage, bool)"/> with <c>capped</c> (our own and relayed gossip through
/// <c>IPeerGossipOutbox</c>) refuses one when the peer already has <c>maxQueuedGossip</c> waiting (a peer that reads
/// slowly), calling <c>onGossipDropped</c>. Channel messages, warnings, errors, the disconnect and our uncapped
/// <c>channel_update</c> for a channel with this peer are never refused for it, so gossip can never hold a channel
/// message back from the queue; the order of what is queued stays FIFO.
/// </para>
/// </remarks>
public sealed class PeerOutbox
{
    private readonly Channel<OutboxItem> _queue = Channel.CreateUnbounded<OutboxItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private readonly ILogger _logger;
    private readonly IPeerService _peerService;
    private readonly int _maxQueuedGossip;
    private readonly Action? _onGossipDropped;
    private int _queuedGossip;
    private long _droppedGossip;

    /// <summary>
    /// Completes when the send loop ends: after a disconnect was processed, or after <see cref="Complete"/> once the
    /// queue is drained.
    /// </summary>
    public Task Completion { get; }

    /// <param name="peerService">The connection.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="maxQueuedGossip">
    /// The capped gossip is refused while this many gossip messages wait (NL-360; 0: no cap).
    /// </param>
    /// <param name="onGossipDropped">Called for every refused capped gossip message (the metric).</param>
    public PeerOutbox(IPeerService peerService, ILogger logger, int maxQueuedGossip = 0, Action? onGossipDropped = null)
    {
        _peerService = peerService;
        _logger = logger;
        _maxQueuedGossip = Math.Max(0, maxQueuedGossip);
        _onGossipDropped = onGossipDropped;
        Completion = Task.Run(SendLoopAsync);
    }

    /// <summary>The gossip messages queued and not sent yet (NL-360).</summary>
    public int QueuedGossipCount => Volatile.Read(ref _queuedGossip);

    /// <summary>The capped gossip messages refused because <see cref="QueuedGossipCount"/> was at the cap.</summary>
    public long DroppedGossipCount => Interlocked.Read(ref _droppedGossip);

    /// <summary>
    /// Queues a channel message. Returns false when the outbox is closed (the peer is disconnecting).
    /// </summary>
    public bool TryEnqueue(IChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Message, message, null));
    }

    /// <summary>
    /// Queues a BOLT 7 gossip message (e.g. our <c>channel_update</c> for a channel with this peer). Returns false when
    /// the outbox is closed.
    /// </summary>
    public bool TryEnqueueGossip(IMessage message) => TryEnqueueGossip(message, false);

    /// <summary>
    /// Queues a BOLT 7 gossip message. With <paramref name="capped"/> (own and relayed gossip, NL-360) it is refused
    /// while <see cref="QueuedGossipCount"/> is at the cap given to the constructor. Returns false when refused or
    /// when the outbox is closed.
    /// </summary>
    public bool TryEnqueueGossip(IMessage message, bool capped)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Interlocked.Increment(ref _queuedGossip) > _maxQueuedGossip && capped && _maxQueuedGossip > 0)
        {
            Interlocked.Decrement(ref _queuedGossip);
            var dropped = Interlocked.Increment(ref _droppedGossip);
            if ((dropped == 1 || dropped % 1_000 == 0) && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Peer {Peer} has {Count} gossip messages waiting to be sent; {Dropped} refused "
                                     + "so far on this connection", _peerService.PeerPubKey, _maxQueuedGossip, dropped);
            try
            {
                _onGossipDropped?.Invoke();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "The gossip drop callback failed");
            }

            return false;
        }

        if (_queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Gossip, message, null)))
            return true;

        Interlocked.Decrement(ref _queuedGossip);
        return false;
    }

    /// <summary>
    /// Queues a `warning`; the connection stays up.
    /// </summary>
    public bool TryEnqueueWarning(WarningException warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return _queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Warning, null, warning));
    }

    /// <summary>
    /// Queues an `error`; the connection stays up (e.g. a failed channel's error, BOLT 1 allows keeping it).
    /// </summary>
    public bool TryEnqueueError(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return _queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Error, error, null));
    }

    /// <summary>
    /// Queues a disconnect after everything already queued, then closes the outbox. The peer service sends the
    /// `error`/`warning` for <paramref name="reason"/> (if any) and closes the connection.
    /// </summary>
    public bool TryEnqueueDisconnect(Exception? reason)
    {
        var queued = _queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Disconnect, null, reason));
        _queue.Writer.TryComplete();
        return queued;
    }

    /// <summary>
    /// Closes the outbox: queued items are still sent, new ones are refused.
    /// </summary>
    public void Complete()
    {
        _queue.Writer.TryComplete();
    }

    private async Task SendLoopAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item.Kind)
                {
                    case OutboxItemKind.Message:
                        await _peerService.SendMessageAsync((IChannelMessage)item.Message!).ConfigureAwait(false);
                        break;
                    case OutboxItemKind.Gossip:
                        Interlocked.Decrement(ref _queuedGossip);
                        await _peerService.SendGossipMessageAsync(item.Message!).ConfigureAwait(false);
                        break;
                    case OutboxItemKind.Error:
                        await _peerService.SendErrorAsync((ErrorMessage)item.Message!).ConfigureAwait(false);
                        break;
                    case OutboxItemKind.Warning:
                        await _peerService.SendWarningAsync((WarningException)item.Reason!).ConfigureAwait(false);
                        break;
                    case OutboxItemKind.Disconnect:
                        _peerService.Disconnect(item.Reason);
                        return;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to send {itemKind} to peer {Peer}", Enum.GetName(item.Kind),
                                 _peerService.PeerPubKey);
            }
        }
    }

    private enum OutboxItemKind
    {
        Message,
        Gossip,
        Warning,
        Error,
        Disconnect
    }

    private sealed record OutboxItem(OutboxItemKind Kind, IMessage? Message, Exception? Reason);
}