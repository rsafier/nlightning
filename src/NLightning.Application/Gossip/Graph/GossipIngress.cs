using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Graph;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Gossip.Validation;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;
using Metrics;

/// <summary>
/// The incoming graph gossip pipeline (plan BOLT7 §3.3, G2-T4): a bounded queue drained by workers that run, per
/// message, the pure checks (<see cref="GossipValidator"/>), an exact-duplicate filter
/// (<see cref="RecentMessageCache"/>), the signatures (<see cref="IGossipSignatureVerifier"/>), for a
/// <c>channel_announcement</c> the funding output (<see cref="IFundingOutputLookup"/>, 6 confirmations), and apply the
/// result to the <see cref="IGraphStore"/>. Also the sink of our own gossip (<see cref="IOwnGossipSink"/>: queued and
/// applied in order by one loop, so the callers, which may hold a channel lock, never wait).
/// </summary>
/// <remarks>
/// <para>
/// Outcomes: accepted messages change the graph (relay comes with G3); BOLT 7 "ignore" cases are dropped silently;
/// a bad signature gets a <c>warning</c> and the connection closed (B7-CA-03, B7-CU-02, B7-NA-03); an invalid key or a
/// malformed <c>addrlen</c> gets a <c>warning</c> only (the connection stays: the message may be relayed). A
/// <c>channel_update</c> whose channel is unknown, and a <c>node_announcement</c> whose node has no channel yet, wait
/// in the <see cref="OrphanUpdateCache"/> and are replayed when the channel is added (the check and the add run under
/// one gate, so none is lost to the race). A chain answer that can still change (bitcoind behind or down, a reorg,
/// a spend in the mempool, fewer than 6 confirmations) defers the announcement: a worker retries it after
/// <see cref="GossipGraphOptions.RetryDelay"/>, up to <see cref="GossipGraphOptions.MaxRetries"/> times; a transient
/// result never counts against the peer.
/// </para>
/// <para>
/// DoS limits (plan §3.8, G5-T2; <see cref="GossipGraphOptions"/>): the per-peer and global queues; a
/// <c>channel_update</c> rate per channel direction and a <c>node_announcement</c> rate per node
/// (<see cref="GossipRateLimiter"/>; checked after the signature, and the newest refused message per channel
/// direction or node is kept and applied once the rate allows it); a keep-alive <c>channel_update</c> (same fields)
/// only when <see cref="GossipGraphOptions.KeepAliveMinInterval"/> newer; timestamps more than
/// <see cref="GossipGraphOptions.MaxFutureTimestamp"/> ahead dropped; no new channel or node beyond
/// <see cref="GossipGraphOptions.MaxChannels"/>/<see cref="GossipGraphOptions.MaxNodes"/>; and a per-peer misbehaviour
/// score (<see cref="GossipMisbehaviourTracker"/>: invalid signatures, bad encodings, funding outputs that contradict
/// the announcement) that bans the peer with one <c>warning</c> and a disconnection. The ban is kept in memory
/// (bounded by <see cref="GossipGraphOptions.MaxMisbehaviourBans"/>, pruned when it ends) and, for a peer that is a
/// graph node, also persisted (<see cref="IGraphStore.Ban"/>, <c>GraphBannedNodes</c>). For the rest of the ban (a
/// restart keeps only the persisted node ban) everything the peer hands over is dropped at the door without being
/// validated, and its connection is left
/// alone (it may carry our channels); a node blacklisted for a conflicting announcement (B7-CA-04) keeps relaying other
/// nodes' gossip, only its own is ignored.
/// Everything is counted in <see cref="GossipMetrics"/> when one is given.
/// </para>
/// <para>
/// The workers and the store's write-behind loop start with <see cref="StartAsync"/>, or on the first queued message.
/// <see cref="StopAsync"/> stops them and writes what is pending.
/// </para>
/// </remarks>
public sealed class GossipIngress : IGossipIngress, IOwnGossipSink, IAsyncDisposable, IDisposable
{
    private readonly IGraphStore _store;
    private readonly IGossipSignatureVerifier _signatureVerifier;
    private readonly IFundingOutputLookup _fundingOutputLookup;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly ILogger<GossipIngress> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly GossipGraphOptions _options;
    private readonly NodeOptions _nodeOptions;

    private readonly Channel<IngressItem> _queue;
    private readonly ConcurrentDictionary<CompactPubKey, int> _queuedPerPeer = new();
    private readonly RecentMessageCache _recentMessages;
    private readonly OrphanUpdateCache _orphans;
    private readonly Lock _orphanGate = new();
    private readonly Lock _startLock = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<Task> _loops = [];
    private readonly Channel<OwnGossipItem> _ownQueue = Channel.CreateUnbounded<OwnGossipItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<ShortChannelId, byte> _missed = new();
    private readonly CompactPubKey? _ourNodeId;
    private readonly GossipRateLimiter _rateLimiter;
    private readonly GossipMisbehaviourTracker _misbehaviour;
    private readonly GossipMetrics? _metrics;
    private readonly ConcurrentDictionary<CompactPubKey, DateTimeOffset> _bannedPeers = new();
    private readonly Lock _banGate = new();
    private readonly ConcurrentDictionary<(ShortChannelId, byte), IngressItem> _limitedUpdates = new();
    private readonly ConcurrentDictionary<CompactPubKey, IngressItem> _limitedNodes = new();

    private Task? _startTask;
    private int _pendingRetries;
    private int _flushRequested;
    private int _pendingOwn;
    private long _droppedCount;
    private long _graphFullCount;

    public GossipIngress(IGraphStore store, IGossipSignatureVerifier signatureVerifier,
                         IFundingOutputLookup fundingOutputLookup, IOptions<GossipGraphOptions> options,
                         IOptions<NodeOptions> nodeOptions, ILogger<GossipIngress> logger,
                         TimeProvider? timeProvider = null,
                         IChannelMemoryRepository? channelMemoryRepository = null,
                         ISecureKeyManager? secureKeyManager = null, GossipMetrics? metrics = null)
    {
        _ourNodeId = secureKeyManager?.GetNodePubKey();
        _store = store;
        _signatureVerifier = signatureVerifier;
        _fundingOutputLookup = fundingOutputLookup;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;

        _queue = Channel.CreateBounded<IngressItem>(new BoundedChannelOptions(_options.MaxQueued)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        _recentMessages = new RecentMessageCache(_options.RecentMessageCacheSize);
        _orphans = new OrphanUpdateCache(_options.MaxOrphans, _options.OrphanTtl, _timeProvider);
        _rateLimiter = new GossipRateLimiter(_options.ChannelUpdateRateInterval, _options.ChannelUpdateBurst,
                                             _options.NodeAnnouncementRateInterval, _timeProvider);
        _misbehaviour = new GossipMisbehaviourTracker(_options.MisbehaviourThreshold, _options.MisbehaviourWindow,
                                                      _timeProvider);
        _metrics = metrics;
        if (metrics is not null)
        {
            metrics.RegisterQueue("ingress", () => _queue.Reader.Count);
            metrics.RegisterQueue("orphans", () => _orphans.Count);
            metrics.RegisterQueue("retries", () => Volatile.Read(ref _pendingRetries));
            metrics.RegisterQueue("rate_limited", () => _limitedUpdates.Count + _limitedNodes.Count);
        }
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.IsEnabledFor(_nodeOptions.BitcoinNetwork);

    /// <summary>The number of messages waiting for a worker.</summary>
    public int QueuedCount => _queue.Reader.Count;

    /// <summary>The orphan cache (for tests and metrics).</summary>
    internal OrphanUpdateCache Orphans => _orphans;

    /// <summary>The rate limits (for tests).</summary>
    internal GossipRateLimiter RateLimiter => _rateLimiter;

    /// <summary>The misbehaviour score (for tests).</summary>
    internal GossipMisbehaviourTracker Misbehaviour => _misbehaviour;

    /// <summary>The peers banned for misbehaviour, ended bans included until the next prune (for tests).</summary>
    internal int BannedPeerCount => _bannedPeers.Count;

    /// <summary>The rate-limited messages kept for later (for tests).</summary>
    internal int RateLimitedCount => _limitedUpdates.Count + _limitedNodes.Count;

    /// <summary>True while <paramref name="peer"/> is banned for misbehaviour (for tests).</summary>
    internal bool IsBannedForMisbehaviour(CompactPubKey peer) => IsPeerBanned(peer);

    /// <inheritdoc />
    public bool TryEnqueue(IPeerService origin, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled || message is not (ChannelAnnouncementMessage or NodeAnnouncementMessage
                                                                  or ChannelUpdateMessage))
            return false;

        _ = StartAsync();
        _metrics?.RecordReceived(message.Type);

        var peer = origin.PeerPubKey;
        if (IsPeerBanned(peer))
        {
            // Dropped at the door without validation, never re-requested
            _metrics?.RecordRejected(message.Type, GossipMetricReasons.BannedPeer);
            return false;
        }

        if (_queuedPerPeer.AddOrUpdate(peer, 1, (_, count) => count + 1) > _options.MaxQueuedPerPeer)
        {
            ReleasePeerSlot(peer);
            _logger.LogDebug("Dropping {MessageType} from peer {Peer}: its gossip queue is full",
                             Enum.GetName(message.Type), peer);
            RecordMissed(message, GossipMetricReasons.PeerQueueFull);
            return false;
        }

        if (_queue.Writer.TryWrite(new IngressItem(origin, message, 0)))
            return true;

        ReleasePeerSlot(peer);
        _logger.LogDebug("Dropping {MessageType} from peer {Peer}: the gossip queue is full",
                         Enum.GetName(message.Type), peer);
        RecordMissed(message, GossipMetricReasons.QueueFull);
        return false;
    }

    /// <summary>
    /// Loads the graph and starts the workers and the write-behind loop (once; later calls return the same task).
    /// Nothing starts while the graph is disabled.
    /// </summary>
    public Task StartAsync()
    {
        lock (_startLock)
        {
            if (_startTask is not null)
                return _startTask;
            if (!IsEnabled || _stopCts.IsCancellationRequested)
                return Task.CompletedTask;

            _startTask = StartCoreAsync(_stopCts.Token);
            return _startTask;
        }
    }

    /// <summary>Stops the workers and writes the pending graph changes.</summary>
    public async Task StopAsync()
    {
        Task? startTask;
        lock (_startLock)
        {
            startTask = _startTask;
            if (!_stopCts.IsCancellationRequested)
                _stopCts.Cancel();
        }

        if (startTask is null)
            return;

        try
        {
            await startTask;
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }

        try
        {
            await _store.FlushAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write the graph while stopping");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
    }

    /// <summary>
    /// Stops the workers without waiting for them or writing the pending changes (a container disposed
    /// synchronously; the host stops the ingress with <see cref="StopAsync"/> first).
    /// </summary>
    public void Dispose()
    {
        lock (_startLock)
        {
            if (!_stopCts.IsCancellationRequested)
                _stopCts.Cancel();
        }
    }

    /// <inheritdoc />
    public void AddOwnChannelAnnouncement(ChannelAnnouncementPayload announcement, LightningMoney capacity)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(capacity);
        EnqueueOwn(new OwnGossipItem(new ChannelAnnouncementMessage(announcement), capacity));
    }

    /// <inheritdoc />
    public void AddOwnChannelUpdate(ChannelUpdatePayload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnqueueOwn(new OwnGossipItem(new ChannelUpdateMessage(update), null));
    }

    /// <inheritdoc />
    public void AddOwnNodeAnnouncement(NodeAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        EnqueueOwn(new OwnGossipItem(new NodeAnnouncementMessage(announcement), null));
    }

    /// <summary>Completes once every queued own message is applied (tests).</summary>
    internal async Task WhenOwnGossipAppliedAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pendingOwn) > 0)
            await Task.Delay(10, cancellationToken);
    }

    /// <summary>
    /// The short channel ids whose <c>channel_announcement</c> or <c>channel_update</c> was dropped (a full queue) or
    /// given up on (transient chain answers until <see cref="GossipGraphOptions.MaxRetries"/>) and not stored since,
    /// taken (cleared) by the caller: what the G3 sync should ask for again with <c>query_short_channel_ids</c>. At
    /// most <see cref="GossipGraphOptions.MaxMissedShortChannelIds"/> are kept.
    /// </summary>
    public IReadOnlyList<ShortChannelId> TakeMissedShortChannelIds()
    {
        var taken = new List<ShortChannelId>(_missed.Count);
        foreach (var shortChannelId in _missed.Keys)
        {
            if (_missed.TryRemove(shortChannelId, out _))
                taken.Add(shortChannelId);
        }

        return taken;
    }

    /// <summary>The number of graph messages dropped so far (full queues, retries given up).</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Applies one of our own messages to the graph (the own-gossip loop calls it in submission order; tests call it
    /// directly): a <c>channel_announcement</c> is stored as <see cref="GraphChannelVerification.Own"/> with
    /// <paramref name="capacity"/> and, when our channel is loaded, its funding txid (no chain lookup); a
    /// <c>channel_update</c> is applied or waits for its announcement; a <c>node_announcement</c> is applied in memory
    /// only (its row is written by the node announcement service before it publishes it, plan G1-T6).
    /// </summary>
    /// <exception cref="ArgumentException">The message is not graph gossip.</exception>
    internal async Task ApplyOwnAsync(IMessage message, LightningMoney? capacity,
                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case ChannelAnnouncementMessage { Payload: var announcement }:
                {
                    TxId? fundingTxId = null;
                    var ours = _channelMemoryRepository?
                              .FindChannels(c => c.ShortChannelId == announcement.ShortChannelId)
                              .FirstOrDefault();
                    if (ours?.FundingOutput is { } fundingOutput)
                    {
                        fundingTxId = fundingOutput.TransactionId;
                        capacity ??= fundingOutput.Amount;
                    }

                    var channel = new GraphChannel(announcement.ShortChannelId, announcement.NodeId1,
                                                   announcement.NodeId2, announcement.BitcoinKey1,
                                                   announcement.BitcoinKey2,
                                                   capacity is null ? null : (ulong)capacity.Satoshi,
                                                   announcement.Features, GraphChannelVerification.Own)
                    {
                        RawAnnouncement = announcement.GetBytes()
                    };
                    await AddChannelAndReplayAsync(channel, fundingTxId, cancellationToken);
                    break;
                }
            case ChannelUpdateMessage { Payload: var update }:
                {
                    var policy = GraphPolicy.FromChannelUpdate(update) with { RawUpdate = update.GetBytes() };
                    lock (_orphanGate)
                    {
                        if (!_store.TryGetChannel(update.ShortChannelId, out _))
                        {
                            _orphans.AddUpdate((ChannelUpdateMessage)message, null);
                            return;
                        }
                    }

                    _store.TryApplyPolicy(update.ShortChannelId, policy);
                    break;
                }
            case NodeAnnouncementMessage { Payload: var announcement }:
                {
                    var addresses = AddressDescriptorCodec.DecodeList(announcement.Addresses.Span);
                    var node = new GraphNode(announcement.NodeId, announcement.Timestamp, announcement.Features,
                                             announcement.Alias.Span, announcement.RgbColor.Span, addresses.Addresses)
                    {
                        RawAnnouncement = announcement.GetBytes()
                    };
                    _store.TryApplyOwnNode(node);
                    break;
                }
            default:
                throw new ArgumentException($"{message.GetType().Name} is not graph gossip", nameof(message));
        }
    }

    private void EnqueueOwn(OwnGossipItem item)
    {
        // Idempotent and never blocking (the caller may hold a channel lock): queue, and let one loop apply in order
        if (!IsEnabled)
            return;

        Interlocked.Increment(ref _pendingOwn);
        if (!_ownQueue.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _pendingOwn);
            return;
        }

        _ = StartAsync();
    }

    private async Task OwnLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _ownQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ApplyOwnAsync(item.Message, item.Capacity, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to add our own {MessageType} to the graph",
                                       Enum.GetName(item.Message.Type));
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingOwn);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
    }

    private void RecordMissed(IMessage message, string reason)
    {
        _metrics?.RecordDropped(reason);
        var dropped = Interlocked.Increment(ref _droppedCount);
        if (dropped == 1 || dropped % 1_000 == 0)
            _logger.LogWarning("{Count} graph gossip messages dropped so far (full queues or chain lookups given up); "
                             + "their channels wait for the next sync", dropped);

        if (_missed.Count >= _options.MaxMissedShortChannelIds)
            return;

        if (message is ChannelAnnouncementMessage announcement)
            _missed.TryAdd(announcement.Payload.ShortChannelId, 0);
        else if (message is ChannelUpdateMessage update)
            _missed.TryAdd(update.Payload.ShortChannelId, 0);
    }

    /// <summary>
    /// Runs the whole pipeline for one message and returns what was done (the workers call it; tests call it
    /// directly). <paramref name="attempt"/> above 0 is a retry of a deferred message.
    /// </summary>
    public async Task<GossipIngressResult> ProcessAsync(IPeerService? origin, IMessage message, int attempt = 0,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = message switch
        {
            ChannelAnnouncementMessage announcement => await ProcessChannelAnnouncementAsync(
                                                           origin, announcement, attempt, cancellationToken),
            NodeAnnouncementMessage announcement => await ProcessNodeAnnouncementAsync(
                                                        origin, announcement, attempt, cancellationToken),
            ChannelUpdateMessage update => await ProcessChannelUpdateAsync(origin, update, attempt),
            _ => GossipIngressResult.Ignored($"{message.GetType().Name} is not graph gossip")
        };

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{MessageType} from peer {Peer}: {Outcome} ({Detail})", Enum.GetName(message.Type),
                             origin?.PeerPubKey.ToString() ?? "us", result.Outcome, result.Detail);

        RecordOutcome(message.Type, result);
        return result;
    }

    private async Task<GossipIngressResult> ProcessChannelAnnouncementAsync(
        IPeerService? origin, ChannelAnnouncementMessage message, int attempt, CancellationToken cancellationToken)
    {
        var announcement = message.Payload;
        var raw = announcement.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.ChannelAnnouncement, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.AlreadyKnown);

        var fields = new ChannelAnnouncementFields(announcement.ChainHash, announcement.ShortChannelId,
                                                   (byte[])announcement.NodeId1, (byte[])announcement.NodeId2,
                                                   (byte[])announcement.BitcoinKey1,
                                                   (byte[])announcement.BitcoinKey2, announcement.Features);
        _store.TryGetChannel(announcement.ShortChannelId, out var known);
        var context = CreateContext();
        var validation = GossipValidator.ValidateChannelAnnouncement(fields, context, known);
        switch (validation.Outcome)
        {
            case GossipValidationOutcome.Warn:
                Remember(MessageTypes.ChannelAnnouncement, raw);
                return await WarnAsync(origin, validation.Reason,
                                       $"Invalid channel_announcement for {announcement.ShortChannelId}: "
                                     + validation.Reason, validation.CloseConnection);
            case GossipValidationOutcome.Ignore:
                if (validation.Reason == GossipRejectReason.ConflictingAnnouncement && known is not null)
                    BlacklistIfLeaked(announcement, known, context);
                if (validation.Reason is GossipRejectReason.AlreadyKnown or GossipRejectReason.UnknownChain)
                    Remember(MessageTypes.ChannelAnnouncement, raw);
                return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        // Plan §3.8: no new channel beyond the limit (before the signatures and the chain lookup it would cost)
        if (known is null && _store.ChannelCount >= _options.MaxChannels)
            return GraphFull($"the graph holds {_options.MaxChannels} channels",
                             announcement.ShortChannelId.ToString());

        if (!VerifyChannelAnnouncement(announcement))
        {
            Remember(MessageTypes.ChannelAnnouncement, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in channel_announcement for {announcement.ShortChannelId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        var lookup = await _fundingOutputLookup.VerifyAsync(announcement.ShortChannelId, announcement.BitcoinKey1,
                                                            announcement.BitcoinKey2,
                                                            cancellationToken: cancellationToken);
        _metrics?.RecordChainLookup(GossipMetrics.TagValue(lookup.Status));
        ulong? capacitySat;
        var verification = GraphChannelVerification.Verified;
        switch (lookup.Status)
        {
            case FundingOutputStatus.Found when lookup.Confirmations >= AnnouncementDepth:
                capacitySat = (ulong)lookup.Amount!.Satoshi;
                break;
            case FundingOutputStatus.Found:
                return GossipIngressResult.Deferred(
                    $"the funding output has {lookup.Confirmations} confirmations, {AnnouncementDepth} needed");
            case FundingOutputStatus.BlockUnavailable
                when _options.FundingValidation == FundingValidationMode.SkipUnavailable:
                capacitySat = null;
                verification = GraphChannelVerification.Unverified;
                break;
            default:
                if (lookup.IsTransient)
                    return GossipIngressResult.Deferred($"the chain lookup returned {lookup.Status}");

                // Permanent: the announcement names no unspent 2-of-2 of its keys (BOLT 7 MUST ignore)
                Remember(MessageTypes.ChannelAnnouncement, raw);
                return FundingCheckFailed(origin, announcement.ShortChannelId, lookup.Status);
        }

        var channel = new GraphChannel(announcement.ShortChannelId, announcement.NodeId1, announcement.NodeId2,
                                       announcement.BitcoinKey1, announcement.BitcoinKey2, capacitySat,
                                       announcement.Features, verification)
        {
            RawAnnouncement = raw
        };

        Remember(MessageTypes.ChannelAnnouncement, raw);
        return await AddChannelAndReplayAsync(channel, lookup.TransactionId, cancellationToken)
                   ? GossipIngressResult.Accepted(verification.ToString())
                   : GossipIngressResult.Ignored("already known", GossipRejectReason.AlreadyKnown);
    }

    private async Task<GossipIngressResult> ProcessNodeAnnouncementAsync(
        IPeerService? origin, NodeAnnouncementMessage message, int attempt, CancellationToken cancellationToken)
    {
        var announcement = message.Payload;
        var raw = announcement.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.NodeAnnouncement, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.NotNewer);

        if (_store.IsBanned(announcement.NodeId))
            return GossipIngressResult.Ignored("the node is banned", GossipRejectReason.BlacklistedNode);

        if (announcement.Timestamp > NowUnixSeconds() + (ulong)_options.MaxFutureTimestamp.TotalSeconds)
            return GossipIngressResult.Limited("the timestamp is too far in the future",
                                               GossipMetricReasons.FutureTimestamp);

        var fields = new NodeAnnouncementFields((byte[])announcement.NodeId, announcement.Timestamp,
                                                announcement.Features, announcement.Addresses);
        uint? lastTimestamp = _store.TryGetNode(announcement.NodeId, out var stored) ? stored.Timestamp : null;
        var validation = GossipValidator.ValidateNodeAnnouncement(fields, _store.NodeHasChannels(announcement.NodeId),
                                                                  lastTimestamp, out var addresses);
        switch (validation.Outcome)
        {
            case GossipValidationOutcome.Warn:
                Remember(MessageTypes.NodeAnnouncement, raw);
                return await WarnAsync(origin, validation.Reason,
                                       $"Invalid node_announcement of {announcement.NodeId}: {validation.Reason}",
                                       validation.CloseConnection);
            case GossipValidationOutcome.Ignore when validation.Reason == GossipRejectReason.UnknownNode:
                {
                    bool nowKnown;
                    lock (_orphanGate)
                    {
                        nowKnown = _store.NodeHasChannels(announcement.NodeId);
                        if (!nowKnown && !_orphans.AddNodeAnnouncement(message, origin, out var full) && full)
                            _metrics?.RecordDropped(GossipMetricReasons.OrphanCacheFull);
                    }

                    // Its first channel was added between the check and the gate: validate again
                    return nowKnown
                               ? await ProcessNodeAnnouncementAsync(origin, message, attempt, cancellationToken)
                               : GossipIngressResult.Orphaned("the node has no channel yet");
                }
            case GossipValidationOutcome.Ignore:
                return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        if (stored is null && _store.NodeCount >= _options.MaxNodes)
            return GraphFull($"the graph holds {_options.MaxNodes} nodes", announcement.NodeId.ToString());

        // The signature first: only a valid announcement is kept for when the rate allows it again
        if (!_signatureVerifier.Verify(announcement.GetSignatureHash(), announcement.Signature, announcement.NodeId))
        {
            Remember(MessageTypes.NodeAnnouncement, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in node_announcement of {announcement.NodeId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        if (!_rateLimiter.TryAcquireNodeAnnouncement(announcement.NodeId))
        {
            KeepLimited(_limitedNodes, announcement.NodeId, origin, message, announcement.Timestamp);
            return GossipIngressResult.Limited("the node announced itself too recently",
                                               GossipMetricReasons.RateLimited);
        }

        var node = new GraphNode(announcement.NodeId, announcement.Timestamp, announcement.Features,
                                 announcement.Alias.Span, announcement.RgbColor.Span, addresses!.Addresses)
        {
            RawAnnouncement = raw
        };

        Remember(MessageTypes.NodeAnnouncement, raw);
        return _store.TryApplyNode(node)
                   ? GossipIngressResult.Accepted(validation.Forwardable ? "forwardable" : "not forwardable")
                   : GossipIngressResult.Ignored("not newer", GossipRejectReason.NotNewer);
    }

    private async Task<GossipIngressResult> ProcessChannelUpdateAsync(
        IPeerService? origin, ChannelUpdateMessage message, int attempt)
    {
        var update = message.Payload;
        var raw = update.GetBytes();
        if (attempt == 0 && _recentMessages.Contains((ushort)MessageTypes.ChannelUpdate, raw))
            return GossipIngressResult.Ignored("an exact duplicate", GossipRejectReason.DuplicateUpdate);

        var context = CreateContext();
        if (!_store.TryGetChannel(update.ShortChannelId, out var channel))
        {
            if (update.ChainHash != context.ChainHash)
                return GossipIngressResult.Ignored("another chain", GossipRejectReason.UnknownChain);

            // A private channel's update (sent to its peer only) never becomes graph data
            if (update.DontForward)
                return GossipIngressResult.Ignored("dont_forward for a channel without announcement",
                                                   GossipRejectReason.UnknownChannel);

            lock (_orphanGate)
            {
                if (!_store.TryGetChannel(update.ShortChannelId, out channel))
                {
                    if (!_orphans.AddUpdate(message, origin, out var full) && full)
                        _metrics?.RecordDropped(GossipMetricReasons.OrphanCacheFull);
                    return GossipIngressResult.Orphaned("the channel is not in the graph yet");
                }
            }
        }

        var validation = GossipValidator.ValidateChannelUpdate(update, context, channel);
        if (validation.Outcome == GossipValidationOutcome.Ignore)
        {
            if (validation.Reason is GossipRejectReason.DuplicateUpdate or GossipRejectReason.OutdatedUpdate)
                Remember(MessageTypes.ChannelUpdate, raw);
            return GossipIngressResult.Ignored(validation.Reason.ToString(), validation.Reason);
        }

        if (validation.Outcome == GossipValidationOutcome.Warn)
            return await WarnAsync(origin, validation.Reason,
                                   $"Invalid channel_update for {update.ShortChannelId}: {validation.Reason}",
                                   validation.CloseConnection);

        var signer = update.Direction ? channel.NodeId2 : channel.NodeId1;
        if (_store.IsBanned(signer))
            return GossipIngressResult.Ignored("the node is banned", GossipRejectReason.BlacklistedNode);

        // Plan §3.8: a keep-alive (the stored fields again) only when it is more than a day newer
        var direction = update.Direction ? (byte)1 : (byte)0;
        if (_options.KeepAliveMinInterval > TimeSpan.Zero && channel.GetPolicy(direction) is { } last
                                                          && last.HasSameFieldsAs(update)
                                                          && (ulong)update.Timestamp - last.Timestamp
                                                          <= (ulong)_options.KeepAliveMinInterval.TotalSeconds)
        {
            Remember(MessageTypes.ChannelUpdate, raw);
            return GossipIngressResult.Limited("a keep-alive less than a day newer than the stored update",
                                               GossipMetricReasons.KeepAliveTooSoon);
        }

        // The signature first: only a valid update is kept for when the rate allows it again
        if (!_signatureVerifier.Verify(update.GetSignatureHash(), update.Signature, signer))
        {
            Remember(MessageTypes.ChannelUpdate, raw);
            return await WarnAsync(origin, GossipRejectReason.None,
                                   $"Invalid signature in channel_update for {update.ShortChannelId}",
                                   closeConnection: true, GossipMetricReasons.InvalidSignature);
        }

        if (!_rateLimiter.TryAcquireUpdate(update.ShortChannelId, direction))
        {
            KeepLimited(_limitedUpdates, (update.ShortChannelId, direction), origin, message, update.Timestamp);
            return GossipIngressResult.Limited("the channel direction's update rate is spent",
                                               GossipMetricReasons.RateLimited);
        }

        Remember(MessageTypes.ChannelUpdate, raw);
        var policy = GraphPolicy.FromChannelUpdate(update) with { RawUpdate = raw };
        return _store.TryApplyPolicy(update.ShortChannelId, policy)
                   ? GossipIngressResult.Accepted(validation.Routable ? "routable" : "not routable")
                   : GossipIngressResult.Ignored("not newer", GossipRejectReason.OutdatedUpdate);
    }

    /// <summary>
    /// Adds the channel and replays what waited for it (its updates, and the announcements of its nodes), in that
    /// order. False when the channel was already stored.
    /// </summary>
    private async Task<bool> AddChannelAndReplayAsync(GraphChannel channel,
                                                      TxId? fundingTxId,
                                                      CancellationToken cancellationToken)
    {
        IReadOnlyList<OrphanEntry<ChannelUpdateMessage>> updates;
        var nodes = new List<OrphanEntry<NodeAnnouncementMessage>>(2);
        lock (_orphanGate)
        {
            if (!_store.TryAddChannel(channel, fundingTxId))
                return false;

            _missed.TryRemove(channel.ShortChannelId, out _);

            updates = _orphans.TakeUpdates(channel.ShortChannelId);
            foreach (var nodeId in (ReadOnlySpan<CompactPubKey>)[channel.NodeId1, channel.NodeId2])
            {
                if (_orphans.TakeNodeAnnouncement(nodeId) is { } entry)
                    nodes.Add(entry);
            }
        }

        foreach (var entry in updates)
            await ReplayAsync(entry.Origin, entry.Message, cancellationToken);
        foreach (var entry in nodes)
            await ReplayAsync(entry.Origin, entry.Message, cancellationToken);

        return true;
    }

    private async Task ReplayAsync(IPeerService? origin, IMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessAsync(origin, message, 0, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Failed to replay a waiting {MessageType}", Enum.GetName(message.Type));
        }
    }

    private bool VerifyChannelAnnouncement(ChannelAnnouncementPayload announcement)
    {
        var hash = announcement.GetSignatureHash();
        return _signatureVerifier.VerifyAll([
            new GossipSignatureCheck(hash, announcement.NodeSignature1, announcement.NodeId1),
            new GossipSignatureCheck(hash, announcement.NodeSignature2, announcement.NodeId2),
            new GossipSignatureCheck(hash, announcement.BitcoinSignature1, announcement.BitcoinKey1),
            new GossipSignatureCheck(hash, announcement.BitcoinSignature2, announcement.BitcoinKey2)
        ]);
    }

    /// <summary>
    /// B7-CA-04: a validly signed announcement of the same funding output (the same bitcoin keys, which signed it)
    /// with other node ids means a funding key leaked; both node pairs are ignored for
    /// <see cref="GossipGraphOptions.ConflictBanDuration"/>. Different bitcoin keys prove nothing (anyone can sign
    /// an announcement with their own keys for any short channel id; the chain check would reject it), so they never
    /// ban anyone.
    /// </summary>
    private void BlacklistIfLeaked(ChannelAnnouncementPayload announcement,
                                   GraphChannel known, GossipValidationContext context)
    {
        if (announcement.BitcoinKey1 != known.BitcoinKey1 || announcement.BitcoinKey2 != known.BitcoinKey2
         || !VerifyChannelAnnouncement(announcement))
            return;

        var fields = new ChannelAnnouncementFields(announcement.ChainHash, announcement.ShortChannelId,
                                                   (byte[])announcement.NodeId1, (byte[])announcement.NodeId2,
                                                   (byte[])announcement.BitcoinKey1,
                                                   (byte[])announcement.BitcoinKey2, announcement.Features);
        var verified = GossipValidator.ValidateChannelAnnouncement(fields, context, known, signaturesVerified: true);
        if (!verified.MayBlacklist)
            return;

        var until = _timeProvider.GetUtcNow() + _options.ConflictBanDuration;
        var banned = new HashSet<CompactPubKey>();
        foreach (var nodeId in (ReadOnlySpan<CompactPubKey>)
                 [announcement.NodeId1, announcement.NodeId2, known.NodeId1, known.NodeId2])
        {
            // Never ourselves: our own channels stay (the channel layer decides about them)
            if (nodeId == _ourNodeId || !banned.Add(nodeId))
                continue;

            _store.Ban(nodeId, $"conflicting channel_announcement for {announcement.ShortChannelId}", until);
        }

        // BOLT 7: blacklist the nodes AND forget every channel connected to them (never one of ours)
        var forgotten = 0;
        foreach (var channel in _store.GetSnapshot().Channels)
        {
            if (channel.Verification == GraphChannelVerification.Own
             || channel.NodeId1 == _ourNodeId || channel.NodeId2 == _ourNodeId
             || (!banned.Contains(channel.NodeId1) && !banned.Contains(channel.NodeId2)))
                continue;

            if (_store.RemoveChannel(channel.ShortChannelId))
                forgotten++;
        }

        _logger.LogWarning("Conflicting channel_announcement for {ShortChannelId} signed by its funding keys: "
                         + "nodes {Node1}, {Node2}, {Known1} and {Known2} are ignored until {Until}, and their "
                         + "{Count} channels forgotten",
                           announcement.ShortChannelId, announcement.NodeId1, announcement.NodeId2, known.NodeId1,
                           known.NodeId2, until, forgotten);
    }

    private async Task<GossipIngressResult> WarnAsync(IPeerService? origin, GossipRejectReason reason, string text,
                                                      bool closeConnection, string? limitReason = null)
    {
        _logger.LogWarning("Gossip from peer {Peer}: {Text}{Close}", origin?.PeerPubKey.ToString() ?? "us", text,
                           closeConnection ? " (closing the connection)" : "");

        // Plan §3.3: invalid signatures and bad encodings count against the peer. Scored before the warning goes out:
        // a connection is closed only once (the first Disconnect wins), so the ban's text rides on this warning
        var banned = ScoreMisbehaviour(origin, text, disconnect: false);
        if (origin is not null)
        {
            try
            {
                if (banned)
                    origin.Disconnect(new WarningException($"{text}. {BanWarning}"));
                else if (closeConnection)
                    origin.Disconnect(new WarningException(text));
                else
                    await origin.SendWarningAsync(new WarningException(text));
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not send the gossip warning to peer {Peer}", origin.PeerPubKey);
            }
        }

        return new GossipIngressResult(GossipIngressOutcome.Warned, text, reason, closeConnection || banned)
        {
            LimitReason = limitReason
        };
    }

    /// <summary>
    /// A funding output check that failed for good. A contradiction (another script or amount, no such transaction
    /// index) is the announcer's lie that the relaying peer should have caught, so it counts against the peer; a spent
    /// output (a closed channel) or an unreadable block never does.
    /// </summary>
    private GossipIngressResult FundingCheckFailed(IPeerService? origin, ShortChannelId shortChannelId,
                                                   FundingOutputStatus status)
    {
        var detail = $"funding output check: {status}";
        switch (status)
        {
            case FundingOutputStatus.ScriptMismatch or FundingOutputStatus.AmountMismatch
                                                    or FundingOutputStatus.TransactionIndexOutOfRange:
                _logger.LogDebug("The funding output of channel_announcement {ShortChannelId} from peer {Peer} "
                               + "contradicts it: {Status}", shortChannelId, origin?.PeerPubKey.ToString() ?? "us",
                                 status);
                ScoreMisbehaviour(origin, $"channel_announcement for {shortChannelId} contradicted by the chain",
                                  disconnect: true);
                return GossipIngressResult.Limited(detail, GossipMetricReasons.ChainMismatch);
            case FundingOutputStatus.OutputSpentOrMissing:
                return GossipIngressResult.Limited(detail, GossipMetricReasons.FundingSpent);
            case FundingOutputStatus.BlockUnavailable:
                return GossipIngressResult.Limited(detail, GossipMetricReasons.FundingUnavailable);
            default:
                return GossipIngressResult.Ignored(detail);
        }
    }

    /// <summary>Plan §3.8: a new channel or node beyond the graph's limits (logged at the first and every 1,000th).</summary>
    private GossipIngressResult GraphFull(string detail, string what)
    {
        var count = Interlocked.Increment(ref _graphFullCount);
        if (count == 1 || count % 1_000 == 0)
            _logger.LogWarning("The graph is full ({Detail}); refused {What} ({Count} refused so far)", detail, what,
                               count);
        return GossipIngressResult.Limited(detail, GossipMetricReasons.GraphFull);
    }

    /// <summary>
    /// Counts one misbehaviour of <paramref name="origin"/>; at the threshold the peer is banned for
    /// <see cref="GossipGraphOptions.MisbehaviourBanDuration"/> and (with <paramref name="disconnect"/>) warned and
    /// disconnected (plan §3.8). True when this banned the peer.
    /// </summary>
    /// <remarks>
    /// The ban lives in memory, at most <see cref="GossipGraphOptions.MaxMisbehaviourBans"/> of them (the one ending
    /// first makes room), the ended ones pruned by the write-behind loop. It is also persisted
    /// (<see cref="IGraphStore.Ban"/>, which ignores the node's own gossip and survives a restart) only when the peer
    /// is a node of the graph: a throwaway node id costs a flooder one handshake, and must cost us neither a
    /// database row nor memory that is never given back.
    /// </remarks>
    private bool ScoreMisbehaviour(IPeerService? origin, string why, bool disconnect)
    {
        if (origin is null || origin.PeerPubKey == _ourNodeId || !_misbehaviour.Record(origin.PeerPubKey))
            return false;

        var peer = origin.PeerPubKey;
        var until = _timeProvider.GetUtcNow() + _options.MisbehaviourBanDuration;
        var persisted = _store.TryGetNode(peer, out _) || _store.NodeHasChannels(peer);
        if (persisted)
            _store.Ban(peer, $"gossip misbehaviour ({_options.MisbehaviourThreshold} in "
                           + $"{_options.MisbehaviourWindow}), the last: {why}", until);
        AddBan(peer, until);
        _metrics?.RecordPeerBanned();
        _logger.LogWarning("Peer {Peer} sent {Threshold} invalid gossip messages within {Window}; ignoring its gossip "
                         + "until {Until}{Persisted} and disconnecting it (last: {Why})", peer,
                           _options.MisbehaviourThreshold, _options.MisbehaviourWindow, until,
                           persisted ? " (a graph node: its own gossip too)" : "", why);
        if (!disconnect)
            return true;

        try
        {
            origin.Disconnect(new WarningException(BanWarning));
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not disconnect banned peer {Peer}", peer);
        }

        return true;
    }

    private void AddBan(CompactPubKey peer, DateTimeOffset until)
    {
        lock (_banGate)
        {
            if (!_bannedPeers.ContainsKey(peer) && _bannedPeers.Count >= _options.MaxMisbehaviourBans)
            {
                PruneBans();
                if (_bannedPeers.Count >= _options.MaxMisbehaviourBans)
                    _bannedPeers.TryRemove(_bannedPeers.MinBy(p => p.Value));
            }

            _bannedPeers[peer] = until;
        }
    }

    /// <summary>Forgets the ended misbehaviour bans; returns how many.</summary>
    internal int PruneBans()
    {
        var now = _timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var ban in _bannedPeers)
        {
            if (ban.Value <= now && _bannedPeers.TryRemove(ban))
                removed++;
        }

        return removed;
    }

    /// <summary>True while <paramref name="peer"/> is banned for misbehaviour (an ended ban is forgotten).</summary>
    private bool IsPeerBanned(CompactPubKey peer)
    {
        if (_bannedPeers.IsEmpty || !_bannedPeers.TryGetValue(peer, out var until))
            return false;

        if (until > _timeProvider.GetUtcNow())
            return true;

        _bannedPeers.TryRemove(new KeyValuePair<CompactPubKey, DateTimeOffset>(peer, until));
        return false;
    }

    /// <summary>
    /// Keeps the newest validly signed message that the rate refused, one per channel direction or node, so the last
    /// policy of a burst is applied once the rate allows it (<see cref="ReplayRateLimited"/>) instead of being lost
    /// until the node's next update. At most <see cref="GossipGraphOptions.MaxRateLimited"/> keys.
    /// </summary>
    private void KeepLimited<TKey>(ConcurrentDictionary<TKey, IngressItem> kept, TKey key, IPeerService? origin,
                                   IMessage message, uint timestamp) where TKey : notnull
    {
        if (!kept.ContainsKey(key) && _limitedUpdates.Count + _limitedNodes.Count >= _options.MaxRateLimited)
        {
            _metrics?.RecordDropped(GossipMetricReasons.RateLimitedFull);
            return;
        }

        var item = new IngressItem(origin, message, 1);
        kept.AddOrUpdate(key, item, (_, current) => TimestampOf(current.Message) < timestamp ? item : current);
    }

    /// <summary>
    /// Queues again the kept rate-limited messages whose rate allows one now (the write-behind loop, and tests).
    /// Returns how many were queued.
    /// </summary>
    internal int ReplayRateLimited()
    {
        var queued = 0;
        foreach (var (key, item) in _limitedUpdates)
        {
            if (_rateLimiter.CanAcceptUpdate(key.Item1, key.Item2)
             && _limitedUpdates.TryRemove(new KeyValuePair<(ShortChannelId, byte), IngressItem>(key, item))
             && RequeueLimited(item))
                queued++;
        }

        foreach (var (key, item) in _limitedNodes)
        {
            if (_rateLimiter.CanAcceptNodeAnnouncement(key)
             && _limitedNodes.TryRemove(new KeyValuePair<CompactPubKey, IngressItem>(key, item))
             && RequeueLimited(item))
                queued++;
        }

        return queued;
    }

    private bool RequeueLimited(IngressItem item)
    {
        if (item.Origin is not null && IsPeerBanned(item.Origin.PeerPubKey))
            return false;
        if (_queue.Writer.TryWrite(item))
            return true;

        RecordMissed(item.Message, GossipMetricReasons.QueueFull);
        return false;
    }

    private static uint TimestampOf(IMessage message) => message switch
    {
        ChannelUpdateMessage update => update.Payload.Timestamp,
        NodeAnnouncementMessage announcement => announcement.Payload.Timestamp,
        _ => 0
    };

    private void RecordOutcome(MessageTypes type, GossipIngressResult result)
    {
        if (_metrics is null)
            return;

        switch (result.Outcome)
        {
            case GossipIngressOutcome.Accepted:
                _metrics.RecordAccepted(type);
                break;
            case GossipIngressOutcome.Ignored or GossipIngressOutcome.Warned:
                _metrics.RecordRejected(type, result.MetricReason);
                break;
            case GossipIngressOutcome.Orphaned:
                _metrics.RecordOrphaned(type);
                break;
        }
    }

    private ulong NowUnixSeconds() => (ulong)Math.Max(0, _timeProvider.GetUtcNow().ToUnixTimeSeconds());

    private GossipValidationContext CreateContext() =>
        new(_nodeOptions.BitcoinNetwork.ChainHash, (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            MinConfirmations = AnnouncementDepth,
            StaleAfter = _options.StaleAfter,
            MaxFutureSkew = _options.MaxFutureTimestamp,
            IsBlacklisted = _store.IsBanned
        };

    private uint AnnouncementDepth => _options.GetAnnouncementDepth(_nodeOptions.BitcoinNetwork);

    private void Remember(MessageTypes type, byte[] raw) => _recentMessages.Add((ushort)type, raw);

    private void ReleasePeerSlot(CompactPubKey peer)
    {
        if (_queuedPerPeer.AddOrUpdate(peer, 0, (_, count) => count - 1) <= 0)
            _queuedPerPeer.TryRemove(new KeyValuePair<CompactPubKey, int>(peer, 0));
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        // Never load the graph on the caller's thread (a peer's read loop, or a caller holding a channel lock)
        await Task.Yield();
        try
        {
            await _store.LoadAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The graph is rebuilt from gossip; a database problem must not stop the node
            _logger.LogError(e, "Failed to load the graph; starting from what gossip brings");
        }

        var workers = _options.GetWorkerCount();
        lock (_startLock)
        {
            for (var i = 0; i < workers; i++)
                _loops.Add(Task.Run(() => WorkerLoopAsync(cancellationToken), CancellationToken.None));
            _loops.Add(Task.Run(() => FlushLoopAsync(cancellationToken), CancellationToken.None));
            _loops.Add(Task.Run(() => OwnLoopAsync(cancellationToken), CancellationToken.None));
        }

        _logger.LogInformation("Gossip ingress started with {Workers} workers", workers);
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Attempt == 0 && item.Origin is not null)
                {
                    ReleasePeerSlot(item.Origin.PeerPubKey);

                    // Queued before the peer was banned: its flood must not reach the validation stages
                    if (IsPeerBanned(item.Origin.PeerPubKey))
                    {
                        _metrics?.RecordRejected(item.Message.Type, GossipMetricReasons.BannedPeer);
                        continue;
                    }
                }

                try
                {
                    var result = await ProcessAsync(item.Origin, item.Message, item.Attempt, cancellationToken);
                    if (result.Outcome == GossipIngressOutcome.Deferred)
                        ScheduleRetry(item, result.Detail, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to process {MessageType} from peer {Peer}",
                                       Enum.GetName(item.Message.Type), item.Origin?.PeerPubKey);
                }

                if (_store.PendingChanges >= _options.FlushBatchSize
                 && Interlocked.CompareExchange(ref _flushRequested, 1, 0) == 0)
                {
                    try
                    {
                        await _store.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        Volatile.Write(ref _flushRequested, 0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
    }

    private void ScheduleRetry(IngressItem item, string reason, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _options.MaxRetries)
        {
            _logger.LogDebug("Giving up on {MessageType} after {Attempts} attempts: {Reason}",
                             Enum.GetName(item.Message.Type), item.Attempt + 1, reason);
            RecordMissed(item.Message, GossipMetricReasons.RetriesExhausted);
            return;
        }

        if (Interlocked.Increment(ref _pendingRetries) > _options.MaxPendingRetries)
        {
            Interlocked.Decrement(ref _pendingRetries);
            _logger.LogDebug("Dropping deferred {MessageType}: too many retries pending",
                             Enum.GetName(item.Message.Type));
            RecordMissed(item.Message, GossipMetricReasons.RetryQueueFull);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.RetryDelay, _timeProvider, cancellationToken);
                if (!_queue.Writer.TryWrite(item with { Attempt = item.Attempt + 1 }))
                {
                    _logger.LogDebug("Dropping deferred {MessageType}: the gossip queue is full",
                                     Enum.GetName(item.Message.Type));
                    RecordMissed(item.Message, GossipMetricReasons.QueueFull);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
            finally
            {
                Interlocked.Decrement(ref _pendingRetries);
            }
        }, CancellationToken.None);
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.FlushInterval, _timeProvider, cancellationToken);
                await _store.FlushAsync(cancellationToken);
                _orphans.PruneExpired();
                ReplayRateLimited();
                _rateLimiter.Prune();
                _misbehaviour.Prune();
                PruneBans();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Graph write-behind round failed");
            }
        }
    }

    private const string BanWarning = "Too much invalid gossip: your gossip is ignored for a while";

    private sealed record IngressItem(IPeerService? Origin, IMessage Message, int Attempt);

    private sealed record OwnGossipItem(IMessage Message, LightningMoney? Capacity);
}