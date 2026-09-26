using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Queries;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Graph.Interfaces;
using Interfaces;

/// <inheritdoc cref="IGossipSyncManager"/>
/// <remarks>
/// <para>
/// One session per connection (per <see cref="IPeerService"/>), dropped when it disconnects. Each session has two
/// loops: the <b>responder</b> answers the peer's queries in arrival order (<see cref="QueryResponder"/>; the replies
/// of one query go out back to back, not at the relay flush, B7-Q-02), and the <b>querier</b> runs our own queries one
/// at a time (B7-Q-01, B7-Q-03: never a second query before the first is answered). Nothing is sent before
/// <see cref="OnPeerInitialized"/> (BOLT 1: init goes first).
/// </para>
/// <para>
/// At init (plan §3.7; replaces the bootstrap <c>gossip_timestamp_filter(0, 0xFFFFFFFF)</c> of wave G-B):
/// <list type="bullet">
/// <item>a peer that offers <c>gossip_queries</c>, while fewer than <see cref="GossipSyncOptions.SyncPeers"/> sync
/// peers are connected, becomes a sync peer: <c>query_channel_range(0, tip + 1)</c> (with <c>query_option</c>
/// timestamps when both sides offer <c>gossip_queries_ex</c>); the replies are checked (<see cref="RangeReplyCollector"/>,
/// a violation gets a <c>warning</c> and ends the sync); the channels we lack (or, with timestamps, whose updates are
/// newer) are asked for with <c>query_short_channel_ids</c> in batches that fit one message; then
/// <c>gossip_timestamp_filter(start - backlog, 0xFFFFFFFF)</c> (see below);</item>
/// <item>another <c>gossip_queries</c> peer gets <c>gossip_timestamp_filter(now, 0xFFFFFFFF)</c> (new gossip only);</item>
/// <item>a peer without <c>gossip_queries</c> gets <c>gossip_timestamp_filter(0xFFFFFFFF, 0)</c> (B7-Q-06).</item>
/// </list>
/// Nothing is queried or filtered while the sync is off (<see cref="GossipSyncOptions.SyncEnabled"/>, or the graph is
/// off); queries are always answered.
/// </para>
/// <para>
/// Every <see cref="GossipSyncOptions.SyncRotationInterval"/> the connected <c>gossip_queries</c> peer synced longest
/// ago runs the range query again; every <see cref="GossipSyncOptions.MissedScidRetryInterval"/> the channels the
/// ingress dropped (NL-353) are asked for again, from an idle <c>gossip_queries</c> peer.
/// </para>
/// <para>
/// A query that is not answered in time, or whose replies break the rules, ends the querying of that connection for
/// good (the peer may still be answering it, and a late reply would be taken for the next query's); a sync peer whose
/// range sync failed gets <c>gossip_timestamp_filter(now, 0xFFFFFFFF)</c>. With the ingress's queue known, each
/// <c>query_short_channel_ids</c> asks for at most a tenth of its per-peer capacity in channels and waits until the
/// queue is at most half full (NL-353), so the answers of a large sync are not dropped. After a sync with timestamps
/// the filter starts where the sync started (less <see cref="TimestampSyncFilterMarginSeconds"/>), since the sync
/// already asked for every newer update; without them it reaches back <see cref="GossipSyncOptions.SyncFilterBacklog"/>.
/// </para>
/// </remarks>
public sealed class GossipSyncManager : IGossipSyncManager, IDisposable
{
    /// <summary>The bytes of a <c>query_short_channel_ids</c> besides its ids and flags (with room to spare).</summary>
    internal const int QueryOverhead = 64;

    /// <summary>The most messages one short channel id can bring: its 256, two 258 and two 257.</summary>
    internal const int MessagesPerQueriedChannel = 5;

    /// <summary>
    /// How far before the start of a range sync with timestamps the following <c>gossip_timestamp_filter</c> reaches
    /// (clock differences between the nodes that signed the updates and us).
    /// </summary>
    internal const uint TimestampSyncFilterMarginSeconds = 600;

    /// <summary>How often the querier looks at the ingress queue while it waits for it to drain.</summary>
    private static readonly TimeSpan s_ingressPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGraphStore _graphStore;
    private readonly QueryResponder _responder;
    private readonly GossipSyncOptions _options;
    private readonly NodeOptions _nodeOptions;
    private readonly ILogger<GossipSyncManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IGossipIngress? _ingress;
    private readonly Func<IReadOnlyList<ShortChannelId>>? _takeMissedShortChannelIds;
    private readonly Func<uint>? _getTipHeight;
    private readonly Func<int>? _getIngressQueueDepth;
    private readonly int _ingressQueueCapacity;
    private readonly ConcurrentDictionary<IPeerService, PeerSession> _sessions = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _timerLock = new();
    private readonly Lock _missedLock = new();
    private readonly HashSet<ShortChannelId> _missedBacklog = [];
    private ITimer? _rotationTimer;
    private ITimer? _missedTimer;
    private volatile bool _hasCompletedInitialSync;
    private bool _disposed;

    /// <param name="graphStore">The graph (read only).</param>
    /// <param name="options">The sync settings.</param>
    /// <param name="nodeOptions">Our chain and features.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="timeProvider">The clock (filters, timers, reply timeouts).</param>
    /// <param name="ingress">The graph ingress; the sync runs only while it is enabled (null: never).</param>
    /// <param name="takeMissedShortChannelIds">Takes the short channel ids the ingress dropped (NL-353).</param>
    /// <param name="getTipHeight">Our chain tip, for the range query's end (null or 0: the whole u32 range).</param>
    /// <param name="getIngressQueueDepth">
    /// The messages waiting in the graph ingress (null: no backpressure). Before each <c>query_short_channel_ids</c> the
    /// querier waits until it is at most half of <paramref name="ingressQueueCapacity"/>.
    /// </param>
    /// <param name="ingressQueueCapacity">
    /// How many messages of one peer the ingress queues before it drops them (0: no limit). A query asks for at most
    /// a tenth of it in channels (each brings up to <see cref="MessagesPerQueriedChannel"/> messages), so the answer
    /// fits in the half the querier waited for.
    /// </param>
    public GossipSyncManager(IGraphStore graphStore, IOptions<GossipSyncOptions> options,
                             IOptions<NodeOptions> nodeOptions, ILogger<GossipSyncManager> logger,
                             TimeProvider? timeProvider = null, IGossipIngress? ingress = null,
                             Func<IReadOnlyList<ShortChannelId>>? takeMissedShortChannelIds = null,
                             Func<uint>? getTipHeight = null, Func<int>? getIngressQueueDepth = null,
                             int ingressQueueCapacity = 0)
    {
        _getIngressQueueDepth = getIngressQueueDepth;
        _ingressQueueCapacity = Math.Max(0, ingressQueueCapacity);
        _graphStore = graphStore;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ingress = ingress;
        _takeMissedShortChannelIds = takeMissedShortChannelIds;
        _getTipHeight = getTipHeight;
        _responder = new QueryResponder(graphStore, options);
    }

    /// <inheritdoc />
    public bool HasCompletedInitialSync => _hasCompletedInitialSync;

    /// <inheritdoc />
    public event EventHandler<GossipFilterReceivedEventArgs>? FilterReceived;

    /// <summary>Whether we query peers and send filters now.</summary>
    public bool IsSyncEnabled =>
        _ingress is { IsEnabled: true } && _options.IsSyncEnabledFor(_nodeOptions.BitcoinNetwork);

    private ChainHash OurChain => _nodeOptions.BitcoinNetwork.ChainHash;

    /// <inheritdoc />
    public void OnPeerInitialized(IPeerService peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            var session = GetOrCreateSession(peer);
            if (session is null || !session.MarkReady())
                return;

            if (!IsSyncEnabled)
                return;

            StartTimers();
            if (!session.SupportsQueries)
            {
                // BOLT 7 (B7-Q-06): a peer that does not offer gossip_queries is not worth querying
                session.EnqueueWork(new FilterWork(GossipTimestampFilter.None));
                return;
            }

            if (TryBecomeSyncPeer(session))
            {
                session.EnqueueWork(new RangeSyncWork());
                return;
            }

            session.EnqueueWork(new FilterWork(new GossipTimestampFilter(NowSeconds(), uint.MaxValue)));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not start the gossip sync with peer {Peer}", peer.PeerPubKey);
        }
    }

    /// <inheritdoc />
    public void HandleMessage(IPeerService peer, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var session = GetOrCreateSession(peer);
            if (session is null)
                return;

            switch (message)
            {
                case QueryChannelRangeMessage or QueryShortChannelIdsMessage:
                    if (!session.TryEnqueueQuery(message, _options.MaxQueuedQueriesPerPeer))
                    {
                        _logger.LogDebug("Peer {Peer} sent {MessageType} while {Count} of its queries wait",
                                         peer.PeerPubKey, Enum.GetName(message.Type), _options.MaxQueuedQueriesPerPeer);
                        _ = SendWarningAsync(session, new WarningException(
                                                          $"{Enum.GetName(message.Type)}: received before the "
                                                        + "previous query was answered"));
                    }

                    break;
                case ReplyChannelRangeMessage or ReplyShortChannelIdsEndMessage:
                    if (!session.TryDeliverReply(message))
                        _logger.LogDebug("Ignoring an unsolicited {MessageType} from peer {Peer}",
                                         Enum.GetName(message.Type), peer.PeerPubKey);
                    break;
                case GossipTimestampFilterMessage filterMessage:
                    HandleFilter(session, filterMessage);
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error handling {MessageType} from peer {Peer}", Enum.GetName(message.Type),
                               peer.PeerPubKey);
        }
    }

    /// <inheritdoc />
    public bool TryGetPeerFilter(IPeerService peer, out GossipTimestampFilter filter)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (_sessions.TryGetValue(peer, out var session) && session.Filter is { } current)
        {
            filter = current;
            return true;
        }

        filter = default;
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> QueryScidAsync(ShortChannelId shortChannelId, CancellationToken cancellationToken = default)
    {
        // B7-Q-01: SHOULD NOT query a channel whose output is spent
        if (_graphStore.TryGetChannel(shortChannelId, out var known) && known.SpentAtHeight is not null)
            return false;

        var session = PickQuerySession();
        if (session is null)
            return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? flag = session.SupportsQueriesEx ? GossipQueryCodec.QueryFlagAll : null;
        if (!session.EnqueueWork(new ScidQueryWork([(shortChannelId, flag)], completion)))
            return false;

        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks an idle <c>gossip_queries</c> peer for the channels the ingress dropped (NL-353) that are still missing
    /// from the graph. The missed-SCID timer calls it; tests call it directly.
    /// </summary>
    /// <returns>How many short channel ids were queued for a query.</returns>
    internal int RetryMissedShortChannelIds()
    {
        List<ShortChannelId> wanted;
        lock (_missedLock)
        {
            if (_takeMissedShortChannelIds is not null)
                foreach (var shortChannelId in _takeMissedShortChannelIds())
                    _missedBacklog.Add(shortChannelId);

            _missedBacklog.RemoveWhere(scid => _graphStore.TryGetChannel(scid, out _));
            if (_missedBacklog.Count == 0)
                return 0;

            wanted = _missedBacklog.OrderBy(QueryResponder.ToUInt64).ToList();
        }

        var session = PickQuerySession();
        if (session is null)
            return 0;

        ulong? flag = session.SupportsQueriesEx ? GossipQueryCodec.QueryFlagAll : null;
        var queued = 0;
        foreach (var batch in wanted.Chunk(GetMaxScidsPerQuery(flag is not null)))
        {
            if (!session.EnqueueWork(new ScidQueryWork(batch.Select(s => (s, flag)).ToList(), null)))
                break;

            queued += batch.Length;
        }

        lock (_missedLock)
            foreach (var shortChannelId in wanted.Take(queued))
                _missedBacklog.Remove(shortChannelId);

        _logger.LogDebug("Asking peer {Peer} again for {Count} channels the gossip ingress dropped",
                         session.Peer.PeerPubKey, queued);
        return queued;
    }

    /// <summary>
    /// Runs the range query again on the connected <c>gossip_queries</c> peer synced longest ago (historical sync, plan
    /// §3.7). The rotation timer calls it; tests call it directly.
    /// </summary>
    /// <returns>The peer that got the range query, or null.</returns>
    internal IPeerService? RotateSyncPeer()
    {
        if (!IsSyncEnabled)
            return null;

        var session = _sessions.Values
                               .Where(s => s is
                               {
                                   IsReady: true, SupportsQueries: true, IsRangeSyncRunning: false,
                                   IsQuerySlotPoisoned: false
                               })
                               .OrderBy(s => s.LastRangeSyncAt ?? DateTimeOffset.MinValue)
                               .FirstOrDefault();
        if (session is null || !session.EnqueueWork(new RangeSyncWork()))
            return null;

        _logger.LogDebug("Rotating the gossip sync to peer {Peer}", session.Peer.PeerPubKey);
        return session.Peer;
    }

    /// <summary>Completes once every queued query and query of <paramref name="peer"/> is done (tests).</summary>
    internal async Task WhenIdleAsync(IPeerService peer, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(peer, out var session))
            return;

        while (!session.IsIdle)
            await Task.Delay(5, cancellationToken);
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _rotationTimer?.Dispose();
            _missedTimer?.Dispose();
            _rotationTimer = null;
            _missedTimer = null;
        }

        foreach (var session in _sessions.Values)
            session.Close();
        _sessions.Clear();
    }

    private PeerSession? GetOrCreateSession(IPeerService peer)
    {
        if (_sessions.TryGetValue(peer, out var existing))
            return existing;

        lock (_timerLock)
        {
            if (_disposed)
                return null;
        }

        var session = new PeerSession(peer);
        if (!_sessions.TryAdd(peer, session))
            return _sessions.TryGetValue(peer, out existing) ? existing : null;

        peer.OnDisconnect += (_, _) => RemoveSession(peer, session);
        session.StartLoops(RespondLoopAsync(session), QueryLoopAsync(session));
        return session;
    }

    private void RemoveSession(IPeerService peer, PeerSession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<IPeerService, PeerSession>(peer, session)))
            session.Close();
    }

    private bool TryBecomeSyncPeer(PeerSession session)
    {
        lock (_timerLock)
        {
            if (_sessions.Values.Count(s => s.IsSyncPeer && s != session) >= _options.SyncPeers)
                return false;

            session.IsSyncPeer = true;
            return true;
        }
    }

    private PeerSession? PickQuerySession()
    {
        if (!IsSyncEnabled)
            return null;

        return _sessions.Values
                        .Where(s => s is { IsReady: true, SupportsQueries: true, IsQuerySlotPoisoned: false })
                        .OrderBy(s => s.PendingWork)
                        .ThenByDescending(s => s.IsSyncPeer)
                        .FirstOrDefault();
    }

    private void HandleFilter(PeerSession session, GossipTimestampFilterMessage message)
    {
        var payload = message.Payload;
        if (payload.ChainHash != OurChain)
        {
            _logger.LogDebug("Ignoring a gossip_timestamp_filter for another chain from peer {Peer}",
                             session.Peer.PeerPubKey);
            return;
        }

        var filter = new GossipTimestampFilter(payload.FirstTimestamp, payload.TimestampRange);
        session.Filter = filter;
        _logger.LogDebug("Peer {Peer} set its gossip filter to [{First}, +{Range})", session.Peer.PeerPubKey,
                         filter.FirstTimestamp, filter.TimestampRange);
        try
        {
            FilterReceived?.Invoke(this, new GossipFilterReceivedEventArgs(session.Peer, filter));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A gossip filter subscriber failed");
        }
    }

    private async Task RespondLoopAsync(PeerSession session)
    {
        var cancellationToken = session.CancellationToken;
        try
        {
            await session.WhenReadyAsync();
            await foreach (var query in session.Queries.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var replies = query switch
                    {
                        QueryChannelRangeMessage range => _responder.CreateRangeReplies(range, OurChain),
                        QueryShortChannelIdsMessage ids => _responder.CreateShortChannelIdsReplies(
                            ids, OurChain, _hasCompletedInitialSync),
                        _ => []
                    };

                    _logger.LogDebug("Answering {MessageType} from peer {Peer} with {Count} messages",
                                     Enum.GetName(query.Type), session.Peer.PeerPubKey, replies.Count);
                    foreach (var reply in replies)
                        await session.Peer.SendGossipMessageAsync(reply);
                }
                catch (WarningException we)
                {
                    _logger.LogWarning("Invalid {MessageType} from peer {Peer}: {Message}", Enum.GetName(query.Type),
                                       session.Peer.PeerPubKey, we.Message);
                    await SendWarningAsync(session, we);
                }
                finally
                {
                    session.QueryAnswered();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disconnected
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Stopped answering the gossip queries of peer {Peer}", session.Peer.PeerPubKey);
        }
    }

    private async Task QueryLoopAsync(PeerSession session)
    {
        var cancellationToken = session.CancellationToken;
        try
        {
            await session.WhenReadyAsync();
            await foreach (var work in session.Work.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (work is RangeSyncWork or ScidQueryWork && session.IsQuerySlotPoisoned)
                    {
                        // A query of ours may still be answered: nothing more is asked on this connection
                        (work as ScidQueryWork)?.Completion?.TrySetResult(false);
                        continue;
                    }

                    switch (work)
                    {
                        case FilterWork filter:
                            await SendFilterAsync(session, filter.Filter);
                            break;
                        case RangeSyncWork:
                            await RunRangeSyncAsync(session, cancellationToken);
                            break;
                        case ScidQueryWork scidQuery:
                            var answered = await RunScidQueryAsync(session, scidQuery.Entries, cancellationToken);
                            scidQuery.Completion?.TrySetResult(answered);
                            break;
                    }
                }
                catch (SyncViolationException violation)
                {
                    // The peer may go on answering the query it broke the rules in: never ask it anything again
                    session.PoisonQuerySlot();
                    (work as ScidQueryWork)?.Completion?.TrySetResult(false);
                    _logger.LogWarning("Ending the gossip sync with peer {Peer}: {Message}", session.Peer.PeerPubKey,
                                       violation.InnerException?.Message);
                    await SendWarningAsync(session, (WarningException)violation.InnerException!);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    (work as ScidQueryWork)?.Completion?.TrySetResult(false);
                    throw;
                }
                catch (Exception e)
                {
                    (work as ScidQueryWork)?.Completion?.TrySetResult(false);
                    _logger.LogDebug(e, "A gossip query to peer {Peer} failed", session.Peer.PeerPubKey);
                }
                finally
                {
                    if (work is RangeSyncWork)
                        await SendLiveFilterIfNeededAsync(session, cancellationToken);
                    session.WorkDone();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disconnected
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Stopped the gossip queries to peer {Peer}", session.Peer.PeerPubKey);
        }
        finally
        {
            // Nobody waits forever for a query that will never be sent
            while (session.Work.TryRead(out var left))
            {
                (left as ScidQueryWork)?.Completion?.TrySetResult(false);
                session.WorkDone();
            }
        }
    }

    private async Task RunRangeSyncAsync(PeerSession session, CancellationToken cancellationToken)
    {
        session.IsRangeSyncRunning = true;
        var completed = false;
        var startedAt = NowSeconds();
        try
        {
            var tip = _getTipHeight?.Invoke() ?? 0;
            var numberOfBlocks = tip > 0 && tip < uint.MaxValue ? tip + 1 : uint.MaxValue;
            var withTimestamps = session.SupportsQueriesEx;
            var query = new QueryChannelRangeMessage(
                new QueryChannelRangePayload(OurChain, 0, numberOfBlocks),
                withTimestamps
                    ? new BaseTlv(TlvConstants.QueryOption,
                                  GossipQueryCodec.EncodeQueryOption(GossipQueryCodec.QueryOptionTimestamps))
                    : null);

            var collector = new RangeReplyCollector(OurChain, 0, numberOfBlocks);
            session.ExpectReplies(MessageTypes.ReplyChannelRange);
            try
            {
                _logger.LogDebug("Querying the channel range of peer {Peer}", session.Peer.PeerPubKey);
                await session.Peer.SendGossipMessageAsync(query);
                while (!collector.IsComplete)
                {
                    var reply = await ReadReplyAsync(session, cancellationToken);
                    if (reply is null)
                    {
                        // BOLT 7: no new query before the last one is answered, and a late reply would be taken for
                        // the next query's: nothing more is asked on this connection
                        session.PoisonQuerySlot();
                        _logger.LogInformation("Peer {Peer} did not finish its reply_channel_range in {Timeout}; "
                                             + "ending the sync with it", session.Peer.PeerPubKey,
                                               _options.SyncReplyTimeout);
                        return;
                    }

                    try
                    {
                        collector.Add((ReplyChannelRangeMessage)reply);
                    }
                    catch (WarningException we)
                    {
                        throw new SyncViolationException(we);
                    }
                }
            }
            finally
            {
                session.ExpectReplies(null);
            }

            var wanted = Diff(collector.Entries, withTimestamps);
            _logger.LogInformation("Peer {Peer} has {Count} channels; asking for {Wanted}", session.Peer.PeerPubKey,
                                   collector.Entries.Count, wanted.Count);

            foreach (var batch in wanted.Chunk(GetMaxScidsPerQuery(withTimestamps)))
                if (!await RunScidQueryAsync(session, batch, cancellationToken))
                    return;

            session.LastRangeSyncAt = _timeProvider.GetUtcNow();
            completed = true;
            if (!session.SyncFilterSent)
            {
                session.SyncFilterSent = true;
                await SendFilterAsync(session, new GossipTimestampFilter(GetSyncFilterStart(withTimestamps, startedAt),
                                                                         uint.MaxValue));
            }

            if (!_hasCompletedInitialSync)
                _logger.LogInformation("Initial gossip sync with peer {Peer} completed", session.Peer.PeerPubKey);
            _hasCompletedInitialSync = true;
        }
        finally
        {
            session.IsRangeSyncRunning = false;
            if (!completed)
                session.NeedsLiveFilter = true;
        }
    }

    /// <summary>
    /// A sync peer whose range sync failed (no reply in time, a rule broken, a failed batch) still gets new gossip:
    /// LND and CLN relay nothing to a <c>gossip_queries</c> peer before its filter. Sent once, after the warning of a
    /// violation; a later successful sync replaces it with the backlog filter.
    /// </summary>
    private async Task SendLiveFilterIfNeededAsync(PeerSession session, CancellationToken cancellationToken)
    {
        if (!session.NeedsLiveFilter || session.SyncFilterSent || session.LiveFilterSent
         || cancellationToken.IsCancellationRequested)
            return;

        session.NeedsLiveFilter = false;
        session.LiveFilterSent = true;
        try
        {
            await SendFilterAsync(session, new GossipTimestampFilter(NowSeconds(), uint.MaxValue));
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not send a gossip filter to peer {Peer}", session.Peer.PeerPubKey);
        }
    }

    /// <summary>
    /// Where the filter after a range sync starts. With timestamps the sync already fetched every channel and update
    /// newer than ours, so only what changed since it started (less a margin for clock differences) is asked for;
    /// without them known channels' updates could not be compared, so <see cref="GossipSyncOptions.SyncFilterBacklog"/>.
    /// </summary>
    private uint GetSyncFilterStart(bool withTimestamps, uint startedAt)
    {
        var back = withTimestamps
                       ? TimestampSyncFilterMarginSeconds
                       : (uint)Math.Min(_options.SyncFilterBacklog.TotalSeconds, uint.MaxValue);
        return startedAt > back ? startedAt - back : 0;
    }

    private List<(ShortChannelId, ulong?)> Diff(IReadOnlyList<KeyValuePair<ShortChannelId, ChannelUpdatePair?>> entries,
                                                bool withFlags)
    {
        var wanted = new List<(ShortChannelId, ulong?)>();
        foreach (var (shortChannelId, timestamps) in entries)
        {
            if (!_graphStore.TryGetChannel(shortChannelId, out var channel))
            {
                wanted.Add((shortChannelId, withFlags ? GossipQueryCodec.QueryFlagAll : null));
                continue;
            }

            // B7-Q-01: never ask for a spent channel; without timestamps a known channel's updates cannot be compared
            // (the timestamp filter's backlog brings the newer ones)
            if (channel.SpentAtHeight is not null || !withFlags || timestamps is not { } remote)
                continue;

            ulong flag = 0;
            if (remote.Node1 > (channel.Policy1?.Timestamp ?? 0))
                flag |= GossipQueryCodec.QueryFlagChannelUpdate1;
            if (remote.Node2 > (channel.Policy2?.Timestamp ?? 0))
                flag |= GossipQueryCodec.QueryFlagChannelUpdate2;
            if (flag != 0)
                wanted.Add((shortChannelId, flag));
        }

        return wanted;
    }

    private async Task<bool> RunScidQueryAsync(PeerSession session, IReadOnlyList<(ShortChannelId Id, ulong? Flag)> entries,
                                               CancellationToken cancellationToken)
    {
        // B7-Q-01: SHOULD NOT query spent channels
        var ids = entries.Where(e => !_graphStore.TryGetChannel(e.Id, out var c) || c.SpentAtHeight is null)
                         .OrderBy(e => QueryResponder.ToUInt64(e.Id))
                         .ToList();
        if (ids.Count == 0)
            return true;

        BaseTlv? flags = null;
        if (session.SupportsQueriesEx && ids.All(e => e.Flag is not null))
            flags = new BaseTlv(TlvConstants.QueryFlags,
                                GossipQueryCodec.EncodeQueryFlags(ids.Select(e => e.Flag!.Value).ToList()));

        var query = new QueryShortChannelIdsMessage(
            new QueryShortChannelIdsPayload(OurChain,
                                            GossipQueryCodec.EncodeShortChannelIds(ids.Select(e => e.Id).ToList())),
            flags);

        await WaitForIngressAsync(session, cancellationToken);
        session.ExpectReplies(MessageTypes.ReplyShortChannelIdsEnd);
        try
        {
            await session.Peer.SendGossipMessageAsync(query);
            var reply = await ReadReplyAsync(session, cancellationToken);
            if (reply is null)
            {
                // BOLT 7: MUST NOT send query_short_channel_ids before reply_short_channel_ids_end, and a late end
                // would complete the next query: nothing more is asked on this connection
                session.PoisonQuerySlot();
                _logger.LogInformation("Peer {Peer} did not answer our query_short_channel_ids in {Timeout}",
                                       session.Peer.PeerPubKey, _options.SyncReplyTimeout);
                return false;
            }

            var end = (ReplyShortChannelIdsEndMessage)reply;
            if (end.Payload.ChainHash != OurChain)
                throw new SyncViolationException(
                    new WarningException("reply_short_channel_ids_end: chain_hash is not the one we queried"));

            _logger.LogDebug("Peer {Peer} answered our query for {Count} channels (full_information={Full})",
                             session.Peer.PeerPubKey, ids.Count, end.Payload.FullInformation);
            return true;
        }
        finally
        {
            session.ExpectReplies(null);
        }
    }

    /// <summary>
    /// Backpressure (NL-353): waits until the graph ingress has room for the answer to one query (its queue at most
    /// half of the per-peer capacity), at most <see cref="GossipSyncOptions.SyncReplyTimeout"/>; after that the query
    /// goes out anyway (what the ingress drops comes back through the missed-channel retry).
    /// </summary>
    private async Task WaitForIngressAsync(PeerSession session, CancellationToken cancellationToken)
    {
        if (_getIngressQueueDepth is null || _ingressQueueCapacity <= 0)
            return;

        var started = _timeProvider.GetTimestamp();
        var room = _ingressQueueCapacity / 2;
        while (_getIngressQueueDepth() > room)
        {
            if (_timeProvider.GetElapsedTime(started) >= _options.SyncReplyTimeout)
            {
                _logger.LogInformation("The gossip ingress queue did not drain in {Timeout}; querying peer {Peer} "
                                     + "anyway", _options.SyncReplyTimeout, session.Peer.PeerPubKey);
                return;
            }

            await Task.Delay(s_ingressPollInterval, _timeProvider, cancellationToken);
        }
    }

    private async Task<IMessage?> ReadReplyAsync(PeerSession session, CancellationToken cancellationToken)
    {
        // A cancelled read leaves the channel alone, so a late reply is not swallowed by an abandoned wait
        using var timeout = new CancellationTokenSource(_options.SyncReplyTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await session.Replies.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task SendFilterAsync(PeerSession session, GossipTimestampFilter filter)
    {
        _logger.LogDebug("Sending gossip_timestamp_filter [{First}, +{Range}) to peer {Peer}", filter.FirstTimestamp,
                         filter.TimestampRange, session.Peer.PeerPubKey);
        await session.Peer.SendGossipMessageAsync(new GossipTimestampFilterMessage(
                                                      new GossipTimestampFilterPayload(
                                                          OurChain, filter.FirstTimestamp, filter.TimestampRange)));
    }

    private async Task SendWarningAsync(PeerSession session, WarningException warning)
    {
        try
        {
            await session.Peer.SendWarningAsync(warning);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not send a warning to peer {Peer}", session.Peer.PeerPubKey);
        }
    }

    private int GetMaxScidsPerQuery(bool withFlags)
    {
        // A flag in 0..0x1F is one bigsize byte
        var perScid = ShortChannelId.Length + (withFlags ? 1 : 0);
        var max = Math.Min(_options.MaxScidsPerQuery, (GossipSyncOptions.MaxMessageLength - QueryOverhead) / perScid);

        // NL-353: the answer must fit in the half of the ingress queue WaitForIngressAsync keeps free
        if (_ingressQueueCapacity > 0)
            max = Math.Min(max, _ingressQueueCapacity / 2 / MessagesPerQueriedChannel);

        return Math.Max(1, max);
    }

    private uint NowSeconds() => (uint)Math.Clamp(_timeProvider.GetUtcNow().ToUnixTimeSeconds(), 0, uint.MaxValue);

    private void StartTimers()
    {
        lock (_timerLock)
        {
            if (_disposed || _rotationTimer is not null)
                return;

            _rotationTimer = _timeProvider.CreateTimer(_ => RunTimerAction(() => RotateSyncPeer()), null,
                                                       _options.SyncRotationInterval, _options.SyncRotationInterval);
            _missedTimer = _timeProvider.CreateTimer(_ => RunTimerAction(() => RetryMissedShortChannelIds()), null,
                                                     _options.MissedScidRetryInterval,
                                                     _options.MissedScidRetryInterval);
        }
    }

    private void RunTimerAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A gossip sync timer failed; the next tick retries");
        }
    }

    /// <summary>A reply that breaks BOLT 7: the sync with that peer ends with a warning.</summary>
    private sealed class SyncViolationException(WarningException warning) : Exception(warning.Message, warning);

    private abstract record SyncWork;

    private sealed record FilterWork(GossipTimestampFilter Filter) : SyncWork;

    private sealed record RangeSyncWork : SyncWork;

    private sealed record ScidQueryWork(IReadOnlyList<(ShortChannelId Id, ulong? Flag)> Entries,
                                        TaskCompletionSource<bool>? Completion) : SyncWork;

    /// <summary>The sync state of one connection.</summary>
    private sealed class PeerSession
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<IMessage> _queries = Channel.CreateUnbounded<IMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly Channel<SyncWork> _work = Channel.CreateUnbounded<SyncWork>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<IMessage> _replies = Channel.CreateUnbounded<IMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private int _queuedQueries;
        private int _pendingWork;
        private int _expectedReplyType = -1;
        private Task _loops = Task.CompletedTask;

        public PeerSession(IPeerService peer)
        {
            Peer = peer;
        }

        public IPeerService Peer { get; }
        public CancellationToken CancellationToken => _cts.Token;
        public ChannelReader<IMessage> Queries => _queries.Reader;
        public ChannelReader<SyncWork> Work => _work.Reader;
        public ChannelReader<IMessage> Replies => _replies.Reader;
        public bool IsReady => _ready.Task.IsCompletedSuccessfully;
        public bool SupportsQueries { get; private set; }
        public bool SupportsQueriesEx { get; private set; }
        public bool IsSyncPeer { get; set; }
        public volatile bool IsRangeSyncRunning;
        public bool SyncFilterSent { get; set; }
        public bool LiveFilterSent { get; set; }
        public bool NeedsLiveFilter { get; set; }
        private volatile bool _querySlotPoisoned;

        /// <summary>
        /// True once a query of ours went unanswered in time or broke the rules: the peer may still be answering it,
        /// so nothing more is asked on this connection (BOLT 7: one outstanding query per kind).
        /// </summary>
        public bool IsQuerySlotPoisoned => _querySlotPoisoned;

        public void PoisonQuerySlot() => _querySlotPoisoned = true;
        public DateTimeOffset? LastRangeSyncAt { get; set; }
        public int PendingWork => Volatile.Read(ref _pendingWork);
        public bool IsIdle => PendingWork == 0 && Volatile.Read(ref _queuedQueries) == 0;

        private GossipTimestampFilter? _filter;
        private readonly Lock _filterLock = new();

        public GossipTimestampFilter? Filter
        {
            get
            {
                lock (_filterLock)
                    return _filter;
            }
            set
            {
                lock (_filterLock)
                    _filter = value;
            }
        }

        /// <summary>Marks the init exchange done (once); false when it already was or the session closed.</summary>
        public bool MarkReady()
        {
            // The negotiated features: gossip_queries(_ex) is not No only when both sides offer it
            SupportsQueries = Peer.Features.GossipQueries != FeatureSupport.No;
            SupportsQueriesEx = Peer.Features.ExpandedGossipQueries != FeatureSupport.No;
            return !_cts.IsCancellationRequested && _ready.TrySetResult();
        }

        public Task WhenReadyAsync() => _ready.Task.WaitAsync(_cts.Token);

        public void StartLoops(Task respond, Task query) => _loops = Task.WhenAll(respond, query);

        public bool TryEnqueueQuery(IMessage query, int max)
        {
            if (Interlocked.Increment(ref _queuedQueries) > max || !_queries.Writer.TryWrite(query))
            {
                Interlocked.Decrement(ref _queuedQueries);
                return false;
            }

            return true;
        }

        public void QueryAnswered() => Interlocked.Decrement(ref _queuedQueries);

        public bool EnqueueWork(SyncWork work)
        {
            Interlocked.Increment(ref _pendingWork);
            if (_work.Writer.TryWrite(work))
                return true;

            Interlocked.Decrement(ref _pendingWork);
            return false;
        }

        public void WorkDone() => Interlocked.Decrement(ref _pendingWork);

        /// <summary>Which reply the querier waits for now (null: none).</summary>
        public void ExpectReplies(MessageTypes? type)
        {
            // Drop what a previous, abandoned query left behind
            while (_replies.Reader.TryRead(out _))
            {
            }

            Volatile.Write(ref _expectedReplyType, type is null ? -1 : (int)type.Value);
        }

        public bool TryDeliverReply(IMessage reply) =>
            Volatile.Read(ref _expectedReplyType) == (int)reply.Type && _replies.Writer.TryWrite(reply);

        public void Close()
        {
            _cts.Cancel();
            _queries.Writer.TryComplete();
            _work.Writer.TryComplete();
            _replies.Writer.TryComplete();
            _ready.TrySetCanceled();
        }

        /// <summary>Completes when both loops ended (after <see cref="Close"/>).</summary>
        public Task Completion => _loops;
    }
}