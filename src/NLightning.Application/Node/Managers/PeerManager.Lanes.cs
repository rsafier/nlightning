using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Node.Managers;

using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;

/// <summary>
/// Per-channel lanes for one peer's channel messages (plan <c>docs/agents/PEER_CHANNEL_PARALLELISM_PLAN.md</c>,
/// <c>Node:ChannelLanes</c>).
/// </summary>
/// <remarks>
/// <para>A laned connection replaces the single inbound loop by a dispatcher and lanes. The dispatcher is the only
/// reader of the connection's inbound queue: it runs the reestablish prologue, reads in arrival order, groups
/// <c>start_batch</c> members and hands each work item (a message, a complete batch, or a failure to report) to a
/// lane. It never awaits a handler. A lane handles one item at a time, exactly as the serial loop does (the channel
/// manager's call including the HTLC switch work after the lock), so each lane is FIFO and different lanes run at the
/// same time.</para>
/// <para>Lane assignment (plan R4) is sticky per connection: a channel id gets its own lane at its first sight only when
/// it is then a registered (not temporary) channel of this peer in memory; every other id (temporary ids of an open,
/// unknown ids, an all-zero or missing id) goes to the one serial peer lane, and stays there for the connection. So an
/// open flow runs serially as before, and the temporary-to-real handover never crosses lanes. A channel lane created
/// while the peer lane still has work waits for the items dispatched to the peer lane before it (the barrier), so a
/// channel the peer lane registered is never touched by its new lane early.</para>
/// <para>Replies go to the lane's connection (<see cref="_currentSession"/>) through the connection's one outbox: a
/// channel's replies are enqueued under its lock, so each channel's wire order is its handling order. A lane's failure
/// that disconnects closes the session: the dispatcher stops, queued items of every lane are dropped, each lane
/// finishes the item it is handling. The connection's loop task (that the next connection waits for) ends only after
/// every lane finished and the peer's uncommitted updates were reverted.</para>
/// <para>Caps: a lane takes a per-connection permit (<see cref="ChannelLaneOptions.MaxConcurrentPerPeer"/>) and then a
/// node-wide one (<see cref="ChannelLaneOptions.MaxConcurrent"/>) before it calls the channel manager, so never while
/// it holds a channel lock, and releases both after each item. The work queued in a connection's lanes is bounded by
/// the inbound queue's capacity: the dispatcher stops reading (and the peer's receive path waits) when it is reached.</para>
/// </remarks>
public sealed partial class PeerManager
{
    private ChannelLaneOptions _channelLanes = new();
    private SemaphoreSlim _laneGlobalPermits = new(new ChannelLaneOptions().MaxConcurrent);

    /// <summary>
    /// The lane settings (<c>Node:ChannelLanes</c>); read when a connection is installed.
    /// </summary>
    internal ChannelLaneOptions ChannelLanes
    {
        get => _channelLanes;
        set
        {
            _channelLanes = value ?? new ChannelLaneOptions();
            _laneGlobalPermits = new SemaphoreSlim(_channelLanes.MaxConcurrent);
        }
    }

    /// <summary>The loop of a connection installed now: laned or serial for its whole lifetime.</summary>
    private Func<PeerSession, ChannelReader<IChannelMessage>, CancellationToken, Task> SelectInboundLoop()
    {
        if (!_channelLanes.IsEnabled)
            return ProcessInboundMessagesAsync;

        var perPeer = _channelLanes.MaxConcurrentPerPeer;
        var global = _laneGlobalPermits;
        return (session, inbound, cancellationToken) =>
            ProcessInboundMessagesLanedAsync(session, inbound, perPeer, global, cancellationToken);
    }

    /// <summary>
    /// The dispatcher of a laned connection (see the class remarks).
    /// </summary>
    private async Task ProcessInboundMessagesLanedAsync(PeerSession session, ChannelReader<IChannelMessage> inbound,
                                                        int maxConcurrentPerPeer, SemaphoreSlim globalPermits,
                                                        CancellationToken cancellationToken)
    {
        _currentSession.Value = session;
        var lanes = new ConnectionLanes(this, session, maxConcurrentPerPeer, globalPermits, cancellationToken);
        try
        {
            // The prologue stays serial and first (plan R2): our channel_reestablish (or the stored error) for every
            // channel with the peer before any of its messages is handled
            if (!cancellationToken.IsCancellationRequested)
                await StartReestablishAsync(session);

            var batching = new InboundBatchState();
            await foreach (var message in inbound.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (!await DispatchAsync(session, lanes, message, batching))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed
        }
        finally
        {
            // Every lane finishes the item it is handling (queued ones are dropped once the session closed), then the
            // peer's uncommitted updates are reverted before the next connection's loop starts
            await lanes.WhenAllIdleAsync();
            await RevertPeerUpdatesAsync(session);
        }
    }

    /// <summary>
    /// Routes one inbound message on a laned connection: into the <c>start_batch</c> group being collected, as the
    /// start of a new group, or to its lane. Returns false when the dispatcher must stop reading (a failure that closes
    /// the connection was handed to a lane).
    /// </summary>
    private async ValueTask<bool> DispatchAsync(PeerSession session, ConnectionLanes lanes, IChannelMessage message,
                                                InboundBatchState batching)
    {
        if (batching.Pending is { } pending)
            return await DispatchBatchMemberAsync(session, lanes, message, batching, pending);

        if (message is StartBatchMessage startBatch)
        {
            if (session.PeerService.Features.OptionSplice == FeatureSupport.No)
            {
                _logger.LogDebug("Ignoring start_batch of channel {ChannelId} from peer {Peer}: option_splice not "
                               + "negotiated", startBatch.Payload.ChannelId, session.Peer.NodeId);
                return true;
            }

            return await DispatchStartBatchAsync(session, lanes, startBatch, batching);
        }

        await lanes.EnqueueAsync(message.Payload?.ChannelId, () => ProcessChannelMessageAsync(session, message));
        return true;
    }

    /// <summary>
    /// The <c>start_batch</c> receiver rules (SP-OP-04) on a laned connection: the warnings go to the batch channel's
    /// lane, after what that channel already has queued.
    /// </summary>
    private async ValueTask<bool> DispatchStartBatchAsync(PeerSession session, ConnectionLanes lanes,
                                                          StartBatchMessage startBatch, InboundBatchState batching)
    {
        var channelId = startBatch.Payload.ChannelId;
        var batchSize = startBatch.Payload.BatchSize;

        if (batchSize <= 1)
        {
            var warning = new ChannelWarningException($"Ignoring start_batch with batch_size {batchSize}", channelId,
                                                      "start_batch batch_size must be greater than 1");
            return await DispatchFailureAsync(session, lanes, warning, MessageTypes.StartBatch, channelId);
        }

        if (batchSize > MaxBatchSize)
        {
            var warning = new ChannelWarningException($"start_batch with batch_size {batchSize}", channelId,
                                                      $"start_batch batch_size must be at most {MaxBatchSize}")
            {
                CloseConnection = true
            };
            return await DispatchFailureAsync(session, lanes, warning, MessageTypes.StartBatch, channelId);
        }

        if (startBatch.MessageTypeTlv?.MessageType != (ushort)MessageTypes.CommitmentSigned)
        {
            _logger.LogDebug(
                "Ignoring start_batch of channel {ChannelId} from peer {Peer} with message_type {MessageType}: "
              + "processing the following messages one by one", channelId, session.Peer.NodeId,
                startBatch.MessageTypeTlv?.MessageType.ToString() ?? "missing");
            return true;
        }

        batching.Pending = new PendingBatch(channelId, batchSize);
        return true;
    }

    /// <summary>
    /// Adds a message to the group being collected; a complete group is one work item of the channel's lane (plan R6),
    /// so nothing of that channel runs between its members.
    /// </summary>
    private async ValueTask<bool> DispatchBatchMemberAsync(PeerSession session, ConnectionLanes lanes,
                                                           IChannelMessage message, InboundBatchState batching,
                                                           PendingBatch pending)
    {
        var messageChannelId = message.Payload?.ChannelId;
        if (messageChannelId is null || messageChannelId.Value != pending.ChannelId)
        {
            batching.Pending = null;
            var warning = new ChannelWarningException(
                $"{Enum.GetName(message.Type)} for channel {messageChannelId} inside a start_batch of channel "
              + $"{pending.ChannelId}", pending.ChannelId, "start_batch message for another channel")
            {
                CloseConnection = true
            };
            return await DispatchFailureAsync(session, lanes, warning, message.Type, pending.ChannelId);
        }

        if (message is not CommitmentSignedMessage commitmentSigned)
        {
            batching.Pending = null;
            var warning = new ChannelWarningException(
                $"{Enum.GetName(message.Type)} inside a start_batch of commitment_signed", pending.ChannelId,
                "start_batch must be followed by commitment_signed only")
            {
                CloseConnection = true
            };
            return await DispatchFailureAsync(session, lanes, warning, message.Type, pending.ChannelId);
        }

        pending.Messages.Add(commitmentSigned);
        if (pending.Messages.Count < pending.BatchSize)
            return true;

        batching.Pending = null;
        var batch = new CommitmentSignedBatch(pending.ChannelId, pending.Messages);
        await lanes.EnqueueAsync(pending.ChannelId, async () =>
        {
            try
            {
                await _channelManager.HandleCommitmentSignedBatchAsync(batch, session.PeerService.Features,
                                                                       session.PeerService.PeerPubKey);
                return true;
            }
            catch (Exception e)
            {
                return HandleChannelMessageFailure(session, e, MessageTypes.CommitmentSigned, batch.ChannelId);
            }
        });
        return true;
    }

    /// <summary>
    /// Hands a failure found by the dispatcher to the channel's lane, so it follows that channel's queued messages.
    /// Returns false (stop reading) when the failure closes the connection.
    /// </summary>
    private async ValueTask<bool> DispatchFailureAsync(PeerSession session, ConnectionLanes lanes,
                                                       ChannelWarningException warning, MessageTypes messageType,
                                                       ChannelId channelId)
    {
        await lanes.EnqueueAsync(channelId,
                                 () => Task.FromResult(HandleChannelMessageFailure(session, warning, messageType,
                                                                                   channelId)));
        return !warning.CloseConnection;
    }

    /// <summary>
    /// Whether a channel id gets a lane of its own at its first sight on a connection (plan R4): a registered channel
    /// of this peer (never a temporary one, which lives elsewhere in the memory repository).
    /// </summary>
    private bool HasOwnLane(PeerSession session, ChannelId channelId)
    {
        try
        {
            return _channelMemoryRepository.TryGetChannel(channelId, out var channel)
                && channel.RemoteNodeId == session.Peer.NodeId;
        }
        catch (InvalidOperationException)
        {
            // A handler is changing the channel right now (the NL-138 test guard saw it): the serial peer lane is
            // always safe
            return false;
        }
    }

    /// <summary>
    /// The lanes of one laned connection.
    /// </summary>
    private sealed class ConnectionLanes
    {
        private readonly PeerManager _manager;
        private readonly PeerSession _session;
        private readonly SemaphoreSlim _peerPermits;
        private readonly SemaphoreSlim _globalPermits;
        private readonly SemaphoreSlim _slots = new(InboundQueueCapacity, InboundQueueCapacity);
        private readonly CancellationToken _cancellationToken;
        private readonly Lock _gate = new();

        /// <summary>Sticky lane assignment of every channel id seen on the connection: true = a lane of its own.</summary>
        private readonly Dictionary<ChannelId, bool> _ownLane = new();

        /// <summary>The channel lanes with work (an idle lane is dropped; its assignment stays).</summary>
        private readonly Dictionary<ChannelId, Lane> _channelLanes = new();

        private readonly Lane _peerLane = new(null);

        /// <summary>Lanes with a worker; <see cref="_idle"/> completes when it drops to zero after the end.</summary>
        private int _runningLanes;

        private TaskCompletionSource? _idle;

        /// <summary>Completes when every item dispatched to the peer lane so far has finished.</summary>
        private Task _peerLaneTail = Task.CompletedTask;

        public ConnectionLanes(PeerManager manager, PeerSession session, int maxConcurrentPerPeer,
                               SemaphoreSlim globalPermits, CancellationToken cancellationToken)
        {
            _manager = manager;
            _session = session;
            _peerPermits = new SemaphoreSlim(maxConcurrentPerPeer, maxConcurrentPerPeer);
            _globalPermits = globalPermits;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Queues one work item on the lane of <paramref name="channelId"/>. Waits (dispatcher backpressure) while the
        /// connection has a full inbound queue's worth of work in its lanes.
        /// </summary>
        public async Task EnqueueAsync(ChannelId? channelId, Func<Task<bool>> run)
        {
            await _slots.WaitAsync(_cancellationToken);
            var item = new LaneItem(run);
            Lane lane;
            var start = false;
            lock (_gate)
            {
                lane = GetLane(channelId);
                lane.Items.Enqueue(item);
                if (ReferenceEquals(lane, _peerLane))
                {
                    item.Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _peerLaneTail = item.Done.Task;
                }

                if (!lane.Running)
                {
                    lane.Running = true;
                    _runningLanes++;
                    start = true;
                }
            }

            if (start)
                _ = Task.Run(() => RunAsync(lane), CancellationToken.None);
        }

        /// <summary>
        /// Waits for every lane to stop (each finishes its item; queued ones are dropped once closed). Called by the
        /// dispatcher after its last <see cref="EnqueueAsync"/>, so no lane starts after it.
        /// </summary>
        public Task WhenAllIdleAsync()
        {
            lock (_gate)
            {
                if (_runningLanes == 0)
                    return Task.CompletedTask;

                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _idle.Task;
            }
        }

        private Lane GetLane(ChannelId? channelId)
        {
            if (channelId is not { } id || id == ChannelId.Zero)
                return _peerLane;

            if (!_ownLane.TryGetValue(id, out var own))
            {
                own = _manager.HasOwnLane(_session, id);
                _ownLane[id] = own;
                if (own)
                {
                    // First sight: wait for whatever the peer lane still has to do (it may have registered the channel)
                    var lane = new Lane(id) { Barrier = _peerLaneTail.IsCompleted ? null : _peerLaneTail };
                    _channelLanes[id] = lane;
                    return lane;
                }
            }

            if (!own)
                return _peerLane;

            if (_channelLanes.TryGetValue(id, out var existing))
                return existing;

            var idle = new Lane(id);
            _channelLanes[id] = idle;
            return idle;
        }

        private async Task RunAsync(Lane lane)
        {
            _manager._currentSession.Value = _session;
            if (lane.Barrier is { } barrier)
            {
                // Completed by the peer lane's items, never faulted
                await barrier.ConfigureAwait(false);
                lane.Barrier = null;
            }

            while (true)
            {
                LaneItem? item;
                lock (_gate)
                {
                    if (!lane.Items.TryDequeue(out item))
                    {
                        lane.Running = false;
                        if (lane.ChannelId is { } id)
                            _channelLanes.Remove(id);
                        if (--_runningLanes == 0)
                            _idle?.TrySetResult();
                        return;
                    }
                }

                try
                {
                    await RunItemAsync(item).ConfigureAwait(false);
                }
                finally
                {
                    item.Done?.TrySetResult();
                    _slots.Release();
                }
            }
        }

        private async Task RunItemAsync(LaneItem item)
        {
            // The connection closed: drop what is still queued (the one being handled elsewhere finishes)
            if (_cancellationToken.IsCancellationRequested)
                return;

            var peerPermit = false;
            var globalPermit = false;
            try
            {
                // Permits before the channel lock, never while holding one (plan R14)
                await _peerPermits.WaitAsync(_cancellationToken).ConfigureAwait(false);
                peerPermit = true;
                await _globalPermits.WaitAsync(_cancellationToken).ConfigureAwait(false);
                globalPermit = true;

                if (!await item.Run().ConfigureAwait(false))
                    _session.Close();
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                // The connection closed while this item waited for a permit
            }
            catch (Exception e)
            {
                // Every item maps its own failure; anything left is our bug: close the connection as the loop would
                _manager._logger.LogError(e, "Channel lane of peer {Peer} failed", _session.Peer.NodeId);
                _session.Close();
            }
            finally
            {
                if (globalPermit)
                    _globalPermits.Release();
                if (peerPermit)
                    _peerPermits.Release();
            }
        }

        private sealed class Lane(ChannelId? channelId)
        {
            public ChannelId? ChannelId { get; } = channelId;
            public Queue<LaneItem> Items { get; } = new();
            public bool Running { get; set; }
            public Task? Barrier { get; set; }
        }

        private sealed class LaneItem(Func<Task<bool>> run)
        {
            public Func<Task<bool>> Run { get; } = run;
            public TaskCompletionSource? Done { get; set; }
        }
    }
}