using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Node.Services;

using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Node.Services;

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
/// NL-360: the gossip share is bounded, by count and by bytes. <see cref="QueuedGossipCount"/> and
/// <see cref="QueuedGossipBytes"/> count the gossip not taken by the send loop yet, and <see cref="EnqueueGossip"/>
/// (our own and relayed gossip through <c>IPeerGossipOutbox</c>) refuses a message with
/// <see cref="GossipEnqueueResult.Full"/> when it would take the share over <c>maxQueuedGossip</c> messages or
/// <c>maxQueuedGossipBytes</c> bytes (a peer that reads slowly), calling <c>onGossipRefused</c>; an empty share always
/// takes one message, whatever its size. The caller keeps what was refused and offers it again once
/// <see cref="GossipDepth"/> shows the queue drained (the relay pauses the connection). Channel messages, warnings,
/// errors, the disconnect and our uncapped <c>channel_update</c> for a channel with this peer are never refused for
/// it, so gossip can never hold a channel message back from the queue; the order of what is queued stays FIFO.
/// </para>
/// <para>
/// Onion messages (BOLT 4, type 513, <see cref="TryEnqueueOnionMessage"/>) are a separate, lower-priority class on
/// their own bounded queue: the send loop takes one when nothing else is waiting, or in place of the gossip message at
/// the head of the queue once <see cref="GossipSendsPerOnionMessage"/> gossip messages went out since the last onion
/// message (so a long gossip stream cannot starve them). A channel message, warning, error or disconnect at the head is
/// always sent first, so an onion message never delays one by more than the one send already on the wire. The queue holds at most <c>maxQueuedOnionMessages</c> (default <see cref="DefaultMaxQueuedOnionMessages"/>,
/// BOLT12 plan §3.4 <c>OnionMessages:MaxOutboxPerPeer</c>); one more is refused (dropped, <c>onOnionMessageDropped</c>
/// is called): a slow reader only loses onion messages, which have no delivery guarantee. A disconnect drops the onion
/// messages still waiting; <see cref="Complete"/> still sends them after the rest.
/// </para>
/// </remarks>
public sealed class PeerOutbox
{
    /// <summary>
    /// The onion messages a peer's outbox holds by default before it refuses more (BOLT12 plan §3.4).
    /// </summary>
    public const int DefaultMaxQueuedOnionMessages = 64;

    /// <summary>
    /// While gossip waits, one waiting onion message goes out after at most this many gossip messages, so a long
    /// gossip stream (e.g. a query_channel_range answer) never starves onion messages. Channel messages, warnings,
    /// errors and the disconnect are never passed by an onion message.
    /// </summary>
    public const int GossipSendsPerOnionMessage = 8;

    private readonly Channel<OutboxItem> _queue = Channel.CreateUnbounded<OutboxItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    private readonly Channel<OnionMessageMessage> _onionQueue = Channel.CreateUnbounded<OnionMessageMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    /// <summary>
    /// Released once per item written to either queue, and once when the outbox is closed: the send loop waits on it
    /// and then takes from the main queue first.
    /// </summary>
    private readonly SemaphoreSlim _signal = new(0);

    private readonly ILogger _logger;
    private readonly IPeerService _peerService;
    private readonly int _maxQueuedGossip;
    private readonly long _maxQueuedGossipBytes;
    private readonly Action? _onGossipRefused;
    private readonly int _maxQueuedOnionMessages;
    private readonly Action? _onOnionMessageDropped;
    private int _queuedGossip;
    private long _queuedGossipBytes;
    private long _sentGossip;
    private long _refusedGossip;
    private int _queuedOnionMessages;
    private long _droppedOnionMessages;

    /// <summary>
    /// Completes when the send loop ends: after a disconnect was processed, or after <see cref="Complete"/> once the
    /// queue is drained.
    /// </summary>
    public Task Completion { get; }

    /// <param name="peerService">The connection.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="maxQueuedGossip">
    /// Capped gossip is refused when it would make more than this many gossip messages wait (NL-360; 0: no cap).
    /// </param>
    /// <param name="onGossipRefused">Called for every refused capped gossip message (the metric).</param>
    /// <param name="maxQueuedOnionMessages">
    /// The onion messages that may wait; one more is refused. Always bounded: a value below 1 means 1.
    /// </param>
    /// <param name="onOnionMessageDropped">Called for every refused onion message (the metric).</param>
    /// <param name="maxQueuedGossipBytes">
    /// Capped gossip is refused when it would make more than this many bytes of gossip wait (NL-360; 0: no cap).
    /// </param>
    public PeerOutbox(IPeerService peerService, ILogger logger, int maxQueuedGossip = 0, Action? onGossipRefused = null,
                      int maxQueuedOnionMessages = DefaultMaxQueuedOnionMessages,
                      Action? onOnionMessageDropped = null, long maxQueuedGossipBytes = 0)
    {
        _peerService = peerService;
        _logger = logger;
        _maxQueuedGossip = Math.Max(0, maxQueuedGossip);
        _maxQueuedGossipBytes = Math.Max(0, maxQueuedGossipBytes);
        _onGossipRefused = onGossipRefused;
        _maxQueuedOnionMessages = Math.Max(1, maxQueuedOnionMessages);
        _onOnionMessageDropped = onOnionMessageDropped;
        Completion = Task.Run(SendLoopAsync);
    }

    /// <summary>The gossip messages queued and not sent yet (NL-360).</summary>
    public int QueuedGossipCount => Volatile.Read(ref _queuedGossip);

    /// <summary>The size of the gossip messages queued and not sent yet, as given when they were queued.</summary>
    public long QueuedGossipBytes => Interlocked.Read(ref _queuedGossipBytes);

    /// <summary>The gossip messages the send loop took off the queue so far (the connection's progress).</summary>
    public long SentGossipCount => Interlocked.Read(ref _sentGossip);

    /// <summary>The capped gossip messages refused because the gossip share was at its cap.</summary>
    public long RefusedGossipCount => Interlocked.Read(ref _refusedGossip);

    /// <summary>The gossip share of this outbox: queued messages and bytes, the caps, the progress.</summary>
    public GossipOutboxDepth GossipDepth =>
        new(QueuedGossipCount, QueuedGossipBytes, _maxQueuedGossip, _maxQueuedGossipBytes, SentGossipCount);

    /// <summary>The onion messages queued and not taken by the send loop yet.</summary>
    public int QueuedOnionMessageCount => Volatile.Read(ref _queuedOnionMessages);

    /// <summary>The onion messages refused because <see cref="QueuedOnionMessageCount"/> was at the cap.</summary>
    public long DroppedOnionMessageCount => Interlocked.Read(ref _droppedOnionMessages);

    /// <summary>
    /// Queues a channel message. Returns false when the outbox is closed (the peer is disconnecting).
    /// </summary>
    public bool TryEnqueue(IChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return TryWrite(new OutboxItem(OutboxItemKind.Message, message, null));
    }

    /// <summary>
    /// Queues a BOLT 4 <c>onion_message</c> in the low-priority class: it goes out only when nothing else waits.
    /// Returns false, without blocking, when <see cref="QueuedOnionMessageCount"/> is at the cap (the message is
    /// dropped and counted) or when the outbox is closed.
    /// </summary>
    public bool TryEnqueueOnionMessage(OnionMessageMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Interlocked.Increment(ref _queuedOnionMessages) > _maxQueuedOnionMessages)
        {
            Interlocked.Decrement(ref _queuedOnionMessages);
            var dropped = Interlocked.Increment(ref _droppedOnionMessages);
            if ((dropped == 1 || dropped % 1_000 == 0) && _logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Peer {Peer} has {Count} onion messages waiting to be sent; {Dropped} dropped so far "
                               + "on this connection", _peerService.PeerPubKey, _maxQueuedOnionMessages, dropped);
            try
            {
                _onOnionMessageDropped?.Invoke();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "The onion message drop callback failed");
            }

            return false;
        }

        if (_onionQueue.Writer.TryWrite(message))
        {
            _signal.Release();
            return true;
        }

        Interlocked.Decrement(ref _queuedOnionMessages);
        return false;
    }

    /// <summary>
    /// Queues a BOLT 7 gossip message (e.g. our <c>channel_update</c> for a channel with this peer), never refused for
    /// the gossip cap. Returns false when the outbox is closed.
    /// </summary>
    public bool TryEnqueueGossip(IMessage message) => TryEnqueueGossip(message, false);

    /// <summary>
    /// Queues a BOLT 7 gossip message; with <paramref name="capped"/> as <see cref="EnqueueGossip"/> (size unknown).
    /// Returns false when refused or when the outbox is closed.
    /// </summary>
    public bool TryEnqueueGossip(IMessage message, bool capped) =>
        Enqueue(message, 0, capped) == GossipEnqueueResult.Queued;

    /// <summary>
    /// Queues our own or relayed gossip (NL-360): <see cref="GossipEnqueueResult.Full"/> when it would take the gossip
    /// share over its message or byte cap (an empty share always takes one message), <see cref="GossipEnqueueResult.Gone"/>
    /// when the outbox is closed. Never blocks.
    /// </summary>
    /// <param name="message">The gossip message.</param>
    /// <param name="size">Its size in bytes, counted against the byte cap (0 when unknown).</param>
    public GossipEnqueueResult EnqueueGossip(IMessage message, int size) => Enqueue(message, size, true);

    private GossipEnqueueResult Enqueue(IMessage message, int size, bool capped)
    {
        ArgumentNullException.ThrowIfNull(message);
        size = Math.Max(0, size);
        var count = Interlocked.Increment(ref _queuedGossip);
        var bytes = Interlocked.Add(ref _queuedGossipBytes, size);
        if (capped && ((_maxQueuedGossip > 0 && count > _maxQueuedGossip)
                    || (_maxQueuedGossipBytes > 0 && count > 1 && bytes > _maxQueuedGossipBytes)))
        {
            Interlocked.Decrement(ref _queuedGossip);
            Interlocked.Add(ref _queuedGossipBytes, -size);
            var refused = Interlocked.Increment(ref _refusedGossip);
            if ((refused == 1 || refused % 10_000 == 0) && _logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Peer {Peer} has {Count} gossip messages ({Bytes} bytes) waiting to be sent; {Refused} "
                               + "refused so far on this connection", _peerService.PeerPubKey, count - 1,
                                 bytes - size, refused);
            try
            {
                _onGossipRefused?.Invoke();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "The gossip refusal callback failed");
            }

            return GossipEnqueueResult.Full;
        }

        if (TryWrite(new OutboxItem(OutboxItemKind.Gossip, message, null, size)))
            return GossipEnqueueResult.Queued;

        Interlocked.Decrement(ref _queuedGossip);
        Interlocked.Add(ref _queuedGossipBytes, -size);
        return GossipEnqueueResult.Gone;
    }

    /// <summary>
    /// Queues a `warning`; the connection stays up.
    /// </summary>
    public bool TryEnqueueWarning(WarningException warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return TryWrite(new OutboxItem(OutboxItemKind.Warning, null, warning));
    }

    /// <summary>
    /// Queues an `error`; the connection stays up (e.g. a failed channel's error, BOLT 1 allows keeping it).
    /// </summary>
    public bool TryEnqueueError(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return TryWrite(new OutboxItem(OutboxItemKind.Error, error, null));
    }

    /// <summary>
    /// Queues a disconnect after everything already queued, then closes the outbox. The peer service sends the
    /// `error`/`warning` for <paramref name="reason"/> (if any) and closes the connection.
    /// </summary>
    public bool TryEnqueueDisconnect(Exception? reason)
    {
        var queued = TryWrite(new OutboxItem(OutboxItemKind.Disconnect, null, reason));
        Complete();
        return queued;
    }

    /// <summary>
    /// Closes the outbox: queued items (onion messages last) are still sent, new ones are refused.
    /// </summary>
    public void Complete()
    {
        // The onion queue first: once the main queue reports completion, no onion message can be written any more
        var onionCompleted = _onionQueue.Writer.TryComplete();
        var mainCompleted = _queue.Writer.TryComplete();
        if (onionCompleted || mainCompleted)
            _signal.Release();
    }

    private bool TryWrite(OutboxItem item)
    {
        if (!_queue.Writer.TryWrite(item))
            return false;

        _signal.Release();
        return true;
    }

    /// <summary>
    /// One send at a time: the main queue (FIFO) first, an onion message when the main queue is empty or when its head
    /// is gossip and <see cref="GossipSendsPerOnionMessage"/> gossip messages went out since the last onion message.
    /// Ends after a disconnect, or once the outbox is closed and both queues are drained. Every write releases the
    /// signal once (after the item is in its queue) and closing releases it once more, so a wake that finds both queues
    /// empty happens only after the close.
    /// </summary>
    private async Task SendLoopAsync()
    {
        var gossipSinceOnionMessage = 0;
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);

            if (_queue.Reader.TryPeek(out var head))
            {
                if (head.Kind == OutboxItemKind.Gossip && gossipSinceOnionMessage >= GossipSendsPerOnionMessage
                                                       && _onionQueue.Reader.TryRead(out var interleaved))
                {
                    gossipSinceOnionMessage = 0;
                    await SendOnionMessageAsync(interleaved).ConfigureAwait(false);
                    continue;
                }

                _queue.Reader.TryRead(out var item);
                if (item!.Kind == OutboxItemKind.Gossip)
                    gossipSinceOnionMessage++;

                if (!await SendItemAsync(item).ConfigureAwait(false))
                    return;

                continue;
            }

            if (_onionQueue.Reader.TryRead(out var onionMessage))
            {
                gossipSinceOnionMessage = 0;
                await SendOnionMessageAsync(onionMessage).ConfigureAwait(false);
                continue;
            }

            if (_queue.Reader.Completion.IsCompleted)
                return;
        }
    }

    private async Task SendOnionMessageAsync(OnionMessageMessage onionMessage)
    {
        Interlocked.Decrement(ref _queuedOnionMessages);
        try
        {
            await _peerService.SendOnionMessageAsync(onionMessage).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Failed to send an onion message to peer {Peer}", _peerService.PeerPubKey);
        }
    }

    /// <summary>Sends one item of the main queue; false after a disconnect (the loop ends).</summary>
    private async Task<bool> SendItemAsync(OutboxItem item)
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
                    Interlocked.Add(ref _queuedGossipBytes, -item.Size);
                    Interlocked.Increment(ref _sentGossip);
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
                    return false;
            }
        }
        catch (Exception e)
        {
            // NL-532: a send that fails because the connection went away is routine; Error only for our own failure
            _logger.Log(PeerConnectionFailures.GetLogLevel(e), e, "Failed to send {itemKind} to peer {Peer}",
                        Enum.GetName(item.Kind), _peerService.PeerPubKey);
        }

        return true;
    }

    private enum OutboxItemKind
    {
        Message,
        Gossip,
        Warning,
        Error,
        Disconnect
    }

    private sealed record OutboxItem(OutboxItemKind Kind, IMessage? Message, Exception? Reason, int Size = 0);
}