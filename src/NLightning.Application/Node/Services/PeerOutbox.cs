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

    /// <summary>
    /// Completes when the send loop ends: after a disconnect was processed, or after <see cref="Complete"/> once the
    /// queue is drained.
    /// </summary>
    public Task Completion { get; }

    public PeerOutbox(IPeerService peerService, ILogger logger)
    {
        _peerService = peerService;
        _logger = logger;
        Completion = Task.Run(SendLoopAsync);
    }

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
    public bool TryEnqueueGossip(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _queue.Writer.TryWrite(new OutboxItem(OutboxItemKind.Gossip, message, null));
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