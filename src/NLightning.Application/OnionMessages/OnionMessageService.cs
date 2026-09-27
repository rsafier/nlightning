using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.OnionMessages;

using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Gossip.Graph.Interfaces;

/// <summary>
/// The node's onion-message endpoint (BOLT 4 "Onion Messages", plan OM2-T3/T4, OM3-T2; see
/// <see cref="IOnionMessageService"/>).
/// </summary>
/// <remarks>
/// <para>Receive: <see cref="HandleIncoming"/> applies the rate limit and queues (never blocks the peer's read loop). A
/// worker peels with an empty associated data and the message's path_key, reads the <c>onionmsg_tlv</c> strictly and
/// unblinds the <c>encrypted_recipient_data</c>; then it forwards (a non-final hop that carries only
/// <c>encrypted_recipient_data</c>, no <c>path_id</c>, and names the next node by id or by a SCID of ours) to the next
/// peer if it is connected, negotiated onion messages and is not the sender, with the next path key
/// (<c>next_path_key_override</c> or the derived one); or it delivers a final hop with at most one payload field to
/// the waiting <see cref="SendAndWaitForReplyAsync"/> caller (a <c>path_id</c> of one of our reply paths) or to the
/// <see cref="IOnionMessageHandler"/> of its payload type, on a second bounded queue. Anything else is ignored and
/// counted in <see cref="OnionMessageMetrics"/>: onion messages have no error replies.</para>
/// <para>Send: a node id is reached through a blinded path we create over the unblinded hops to it (a direct peer, or
/// a graph path); a blinded path through its introduction node, after unblinding our own hops when the introduction
/// node is us, with the unblinded prefix ending in <c>next_path_key_override = first_path_key</c> (the packet builder
/// writes it). We only send to connected peers (plan D6).</para>
/// <para>Off (every incoming message dropped, every send <see cref="OnionMessageSendStatus.NotAvailable"/>) unless we
/// advertise <c>option_onion_messages</c> and a packet builder is registered.</para>
/// </remarks>
public sealed class OnionMessageService : IOnionMessageService, IDisposable
{
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private readonly ISphinxService _sphinxService;
    private readonly IRouteBlindingService _routeBlindingService;
    private readonly IOnionMessagePacketBuilder? _packetBuilder;
    private readonly IOnionMessageRateLimiter? _rateLimiter;
    private readonly OnionMessageMetrics _metrics;
    private readonly OnionMessageDispatcher _dispatcher;
    private readonly OnionMessagePathFinder _pathFinder;
    private readonly MessagePathFactory _messagePathFactory;
    private readonly ReplyPathFactory _replyPathFactory;
    private readonly PendingReplyRegistry _pendingReplies;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OnionMessageService> _logger;
    private readonly CompactPubKey _ourNodeId;
    private readonly Channel<IncomingOnionMessage> _incoming;
    private readonly Channel<HandlerWork> _handlerWork;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _incomingWorker = Task.CompletedTask;
    private readonly Task _handlerWorker = Task.CompletedTask;
    private int _disposed;

    public OnionMessageService(IOptions<NodeOptions> nodeOptions, ISecureKeyManager secureKeyManager,
                               ISphinxService sphinxService, IRouteBlindingService routeBlindingService,
                               IPeerManager peerManager, IChannelMemoryRepository channelMemoryRepository,
                               IEnumerable<IOnionMessageHandler> handlers, OnionMessageMetrics metrics,
                               ILogger<OnionMessageService> logger, IOptions<OnionMessageOptions>? options = null,
                               IOnionMessagePacketBuilder? packetBuilder = null,
                               IOnionMessageRateLimiter? rateLimiter = null, IGraphStore? graphStore = null,
                               TimeProvider? timeProvider = null)
    {
        var settings = options?.Value ?? new OnionMessageOptions();
        var errors = settings.GetValidationErrors();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid onion message options: " + string.Join(" ", errors));

        _sphinxService = sphinxService;
        _routeBlindingService = routeBlindingService;
        _packetBuilder = packetBuilder;
        _rateLimiter = rateLimiter;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ourNodeId = secureKeyManager.GetNodePubKey();
        _dispatcher = new OnionMessageDispatcher(handlers);
        _pathFinder = new OnionMessagePathFinder(peerManager, channelMemoryRepository, _ourNodeId,
                                                 settings.MaxPathHops, graphStore);
        _messagePathFactory = new MessagePathFactory(routeBlindingService);
        _replyPathFactory = new ReplyPathFactory(_messagePathFactory, _pathFinder, _ourNodeId);
        _pendingReplies = new PendingReplyRegistry(settings.MaxPendingReplies);
        _incoming = Channel.CreateBounded<IncomingOnionMessage>(
            new BoundedChannelOptions(settings.MaxQueuedMessages)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        _handlerWork = Channel.CreateBounded<HandlerWork>(
            new BoundedChannelOptions(settings.MaxQueuedHandlerWork)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        var advertised = nodeOptions.Value.Features.GetNodeFeatures().IsFeatureSet(Feature.OptionOnionMessages);
        if (advertised && packetBuilder is null)
            _logger.LogWarning("option_onion_messages is advertised but no onion message packet builder is "
                             + "registered: onion messages stay off");
        IsAvailable = advertised && packetBuilder is not null;
        if (!IsAvailable)
            return;

        _incomingWorker = Task.Run(RunIncomingAsync);
        _handlerWorker = Task.Run(RunHandlersAsync);
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <summary>The waits of <see cref="SendAndWaitForReplyAsync"/> in progress.</summary>
    public int PendingReplies => _pendingReplies.Count;

    /// <inheritdoc />
    public void HandleIncoming(IPeerService peer, OnionMessageMessage message)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsAvailable || Volatile.Read(ref _disposed) != 0)
        {
            Drop(OnionMessageDropReasons.NotAvailable, peer.PeerPubKey);
            return;
        }

        if (_rateLimiter is not null
         && !_rateLimiter.TryAdmit(peer.PeerPubKey, message.Payload.OnionMessagePacket.Length))
        {
            Drop(OnionMessageDropReasons.RateLimited, peer.PeerPubKey);
            return;
        }

        _metrics.RecordReceived();
        if (!_incoming.Writer.TryWrite(new IncomingOnionMessage(peer.PeerPubKey, message)))
            Drop(OnionMessageDropReasons.QueueFull, peer.PeerPubKey);
    }

    /// <inheritdoc />
    public Task<OnionMessageSendResult> SendAsync(OnionMessageDestination destination, OnionMessageContents contents,
                                                  BlindedPath? replyPath,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(contents);
        if (!IsAvailable)
            return Task.FromResult(new OnionMessageSendResult(OnionMessageSendStatus.NotAvailable));

        return SendCoreAsync(destination, contents,
                             replyPath is null ? null : WireBlindedPath.FromBlindedPath(replyPath), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OnionMessageSendResult> SendAndWaitForReplyAsync(OnionMessageDestination destination,
                                                                       OnionMessageContents contents,
                                                                       IReadOnlyCollection<ulong> expectedReplyTypes,
                                                                       TimeSpan timeout,
                                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(expectedReplyTypes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!IsAvailable)
            return new OnionMessageSendResult(OnionMessageSendStatus.NotAvailable);

        using var pending = _pendingReplies.TryRegister(expectedReplyTypes);
        if (pending is null)
        {
            _logger.LogWarning("Too many onion message replies awaited; the send is refused");
            return new OnionMessageSendResult(OnionMessageSendStatus.Dropped);
        }

        var replyPath = _replyPathFactory.Create(pending.PathId);
        var result = await SendCoreAsync(destination, contents, WireBlindedPath.FromBlindedPath(replyPath),
                                         cancellationToken);
        if (result.Status != OnionMessageSendStatus.Sent)
            return result;

        try
        {
            var reply = await pending.Reply.WaitAsync(timeout, _timeProvider, cancellationToken);
            return new OnionMessageSendResult(OnionMessageSendStatus.Replied, reply);
        }
        catch (TimeoutException)
        {
            return new OnionMessageSendResult(OnionMessageSendStatus.ReplyTimedOut);
        }
    }

    /// <summary>
    /// Processes one received message (the worker's body; tests call it directly).
    /// </summary>
    internal async Task ProcessIncomingAsync(CompactPubKey fromPeer, OnionMessageMessage message,
                                             CancellationToken cancellationToken)
    {
        var packetBytes = message.Payload.OnionMessagePacket;
        PeeledOnion peeled;
        try
        {
            var packet = new OnionPacket(packetBytes.Span, packetBytes.Length - OnionConstants.PacketOverheadLength);
            peeled = _sphinxService.PeelAsLocalNode(packet, [], message.Payload.PathKey, OnionPacketKind.OnionMessage);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Drop(OnionMessageDropReasons.Undecryptable, fromPeer, e);
            return;
        }

        if (!OnionMessagePayloadCodec.TryDecode(peeled.Payload.Span, out var tlvs) || tlvs is null)
        {
            Drop(OnionMessageDropReasons.InvalidPayload, fromPeer);
            return;
        }

        if (tlvs.EncryptedRecipientData is not { } encryptedRecipientData)
        {
            Drop(OnionMessageDropReasons.InvalidRecipientData, fromPeer);
            return;
        }

        BlindedHopUnblinding unblinding;
        try
        {
            unblinding = _routeBlindingService.UnblindAsLocalNode(message.Payload.PathKey, encryptedRecipientData,
                                                                  peeled.PathKeySharedSecret);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Drop(OnionMessageDropReasons.InvalidRecipientData, fromPeer, e);
            return;
        }

        var recipientData = unblinding.RecipientData;
        if (!IsValidMessagePathData(recipientData))
        {
            Drop(OnionMessageDropReasons.ForbiddenRecipientData, fromPeer);
            return;
        }

        if (peeled.NextPacket is { } nextPacket)
            await ForwardAsync(fromPeer, tlvs, recipientData, unblinding.NextPathKey, nextPacket, cancellationToken);
        else
            Deliver(fromPeer, tlvs, recipientData);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _incoming.Writer.TryComplete();
        _handlerWork.Writer.TryComplete();
        _stopping.Cancel();
        try
        {
            Task.WaitAll([_incomingWorker, _handlerWorker], s_stopTimeout);
        }
        catch (AggregateException)
        {
            // The workers only end by cancellation; anything else was logged by them
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The message-path rules for a hop's recipient data (BOLT 4: <c>allowed_features</c> with any bit is unknown to
    /// us, since onion messages use no feature; the creator MUST NOT include <c>payment_relay</c> or
    /// <c>payment_constraints</c>).
    /// </summary>
    private static bool IsValidMessagePathData(BlindedRecipientData data) =>
        !data.HasAnyAllowedFeature && data.PaymentRelay is null && data.PaymentConstraints is null;

    private async Task ForwardAsync(CompactPubKey fromPeer, OnionMessageTlvs tlvs, BlindedRecipientData recipientData,
                                    CompactPubKey nextPathKey, OnionPacket nextPacket,
                                    CancellationToken cancellationToken)
    {
        // BOLT 4 reader, non-final hop: only encrypted_recipient_data, and no path_id
        if (tlvs.ReplyPath is not null || tlvs.OtherRecords.Count > 0)
        {
            Drop(OnionMessageDropReasons.NonFinalExtraFields, fromPeer);
            return;
        }

        if (recipientData.PathId is not null)
        {
            Drop(OnionMessageDropReasons.NonFinalPathId, fromPeer);
            return;
        }

        var nextNode = recipientData.NextNodeId
                    ?? (recipientData.ShortChannelId is { } shortChannelId
                            ? _pathFinder.ResolveOurChannel(shortChannelId)
                            : null);
        if (nextNode is not { } next)
        {
            Drop(OnionMessageDropReasons.NoNextHop, fromPeer);
            return;
        }

        if (next == _ourNodeId)
        {
            Drop(OnionMessageDropReasons.Loop, fromPeer);
            return;
        }

        if (next == fromPeer)
        {
            Drop(OnionMessageDropReasons.Echo, fromPeer);
            return;
        }

        var peer = _pathFinder.GetOnionMessagePeer(next);
        if (peer is null)
        {
            Drop(OnionMessageDropReasons.NextPeerUnreachable, fromPeer);
            return;
        }

        var forwarded = new OnionMessageMessage(new OnionMessagePayload(nextPathKey, nextPacket.ToBytes()));
        try
        {
            await peer.SendOnionMessageAsync(forwarded, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Drop(OnionMessageDropReasons.SendFailed, fromPeer, e);
            return;
        }

        _metrics.RecordForwarded();
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Forwarded an onion message from {From} to {Next}", fromPeer, next);
    }

    private void Deliver(CompactPubKey fromPeer, OnionMessageTlvs tlvs, BlindedRecipientData recipientData)
    {
        if (OnionMessagePayloadCodec.CountPayloadFields(tlvs.OtherRecords) > 1)
        {
            Drop(OnionMessageDropReasons.MultiplePayloadFields, fromPeer);
            return;
        }

        var contents = new OnionMessageContents(tlvs.OtherRecords);
        var received = new ReceivedOnionMessage(contents, tlvs.ReplyPath, recipientData.PathId, fromPeer);

        // A message through one of our reply paths is only ever the reply we wait for (BOLT 4 reader)
        if (recipientData.PathId is { } pathId && _pendingReplies.IsOurs(pathId.Span))
        {
            if (_pendingReplies.TryComplete(pathId.Span, received))
                _metrics.RecordDelivered("reply");
            else
                Drop(OnionMessageDropReasons.UnexpectedReply, fromPeer);
            return;
        }

        if (OnionMessageDispatcher.GetPayloadType(contents) is not { } payloadType)
        {
            // Valid, but nothing to hand over (tests and probes)
            _metrics.RecordDelivered("empty");
            return;
        }

        var handler = _dispatcher.GetHandler(payloadType);
        if (handler is null)
        {
            Drop(OnionMessageDropReasons.NoHandler, fromPeer);
            return;
        }

        if (!_handlerWork.Writer.TryWrite(new HandlerWork(handler, received)))
        {
            Drop(OnionMessageDropReasons.HandlerQueueFull, fromPeer);
            return;
        }

        _metrics.RecordDelivered("handler");
    }

    private async Task<OnionMessageSendResult> SendCoreAsync(OnionMessageDestination destination,
                                                             OnionMessageContents contents,
                                                             WireBlindedPath? replyPath,
                                                             CancellationToken cancellationToken)
    {
        if (contents.Records.Any(r => r.Type is OnionMessageConstants.ReplyPathType
                                               or OnionMessageConstants.EncryptedRecipientDataType))
            throw new ArgumentException("The contents cannot carry reply_path or encrypted_recipient_data",
                                        nameof(contents));

        if (contents.Records.Sum(r => (long)r.Value.Length) > OnionMessageConstants.LargePayloadsLength)
            return new OnionMessageSendResult(OnionMessageSendStatus.TooLarge);

        if (ResolveRoute(destination) is not { } route)
            return new OnionMessageSendResult(OnionMessageSendStatus.NoPath);

        var firstHop = route.Prefix.Count > 0 ? route.Prefix[0] : route.Path.FirstNodeId;
        var peer = _pathFinder.GetOnionMessagePeer(firstHop);
        if (peer is null)
            return new OnionMessageSendResult(OnionMessageSendStatus.NoPath);

        OnionMessageMessage message;
        try
        {
            message = _packetBuilder!.Build(route.Prefix, route.Path, contents, replyPath);
        }
        catch (ArgumentException e)
        {
            _logger.LogDebug(e, "The onion message does not fit a packet");
            return new OnionMessageSendResult(OnionMessageSendStatus.TooLarge);
        }

        try
        {
            await peer.SendOnionMessageAsync(message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The connection to {Peer} refused an onion message", firstHop);
            return new OnionMessageSendResult(OnionMessageSendStatus.Dropped);
        }

        _metrics.RecordSent();
        return new OnionMessageSendResult(OnionMessageSendStatus.Sent);
    }

    /// <summary>
    /// The unblinded prefix and the blinded path to send along, or null when there is no path.
    /// </summary>
    private SendRoute? ResolveRoute(OnionMessageDestination destination)
    {
        if (destination.NodeId is { } nodeId)
        {
            // A node id: a path we blind ourselves over every hop, no unblinded prefix
            var prefix = _pathFinder.FindPrefix(nodeId);
            if (prefix is null)
                return null;

            var path = _messagePathFactory.Create([.. prefix, nodeId]);
            return new SendRoute([], path);
        }

        var wirePath = destination.BlindedPath!;
        if (_pathFinder.Resolve(wirePath.FirstNode) is not { } introduction)
            return null;

        var pathKey = wirePath.FirstPathKey;
        var hops = wirePath.Hops;
        // The introduction node is us (our own reply path, or a path through us): read our hops ourselves
        while (introduction == _ourNodeId)
        {
            if (hops.Count <= 1)
                return null;

            BlindedHopUnblinding unblinding;
            try
            {
                unblinding = _routeBlindingService.UnblindAsLocalNode(pathKey, hops[0].EncryptedRecipientData);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Cannot read our own hop of a blinded path");
                return null;
            }

            var data = unblinding.RecipientData;
            if (!IsValidMessagePathData(data) || data.PathId is not null)
                return null;

            var next = data.NextNodeId
                    ?? (data.ShortChannelId is { } shortChannelId
                            ? _pathFinder.ResolveOurChannel(shortChannelId)
                            : null);
            if (next is null)
                return null;

            introduction = next.Value;
            pathKey = unblinding.NextPathKey;
            hops = hops.Skip(1).ToList();
        }

        var toIntroduction = _pathFinder.FindPrefix(introduction);
        return toIntroduction is null
                   ? null
                   : new SendRoute(toIntroduction, new BlindedPath(introduction, pathKey, hops));
    }

    private async Task RunIncomingAsync()
    {
        var cancellationToken = _stopping.Token;
        try
        {
            await foreach (var item in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessIncomingAsync(item.FromPeer, item.Message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to process an onion message from {Peer}", item.FromPeer);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
    }

    private async Task RunHandlersAsync()
    {
        var cancellationToken = _stopping.Token;
        try
        {
            await foreach (var work in _handlerWork.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await work.Handler.HandleAsync(work.Message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "The onion message handler {Handler} failed", work.Handler.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
    }

    private void Drop(string reason, CompactPubKey fromPeer, Exception? exception = null)
    {
        _metrics.RecordDropped(reason);
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace(exception, "Dropped an onion message from {Peer}: {Reason}", fromPeer, reason);
    }

    private sealed record IncomingOnionMessage(CompactPubKey FromPeer, OnionMessageMessage Message);

    private sealed record HandlerWork(IOnionMessageHandler Handler, ReceivedOnionMessage Message);

    private sealed record SendRoute(IReadOnlyList<CompactPubKey> Prefix, BlindedPath Path);
}