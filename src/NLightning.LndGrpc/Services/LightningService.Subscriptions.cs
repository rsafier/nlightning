using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Node.Events;
using Domain.Onchain.Enums;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>Live initialized peer sessions; no initial snapshot or replay (LND semantics).</summary>
    public override async Task SubscribePeerEvents(PeerEventSubscription request,
                                                   IServerStreamWriter<PeerEvent> responseStream,
                                                   ServerCallContext context)
    {
        using var queue = new LiveEventQueue<PeerEvent>();
        void Changed(object? _, PeerStateChangedEventArgs args)
        {
            try
            {
                queue.Publish(new PeerEvent
                {
                    PubKey = args.PeerPubKey.ToString(),
                    Type = args.Online ? PeerEvent.Types.EventType.PeerOnline : PeerEvent.Types.EventType.PeerOffline
                });
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not publish peer subscription event");
                queue.Fail(new RpcException(new Status(StatusCode.Internal, "peer subscription event mapping failed")));
            }
        }
        _peerManager.OnPeerStateChanged += Changed;
        try
        {
            await context.WriteResponseHeadersAsync(new Metadata());
            await queue.WriteToAsync(responseStream, context.CancellationToken);
        }
        finally
        {
            _peerManager.OnPeerStateChanged -= Changed;
        }
    }

    /// <summary>Live persisted funding/open/close transitions and connection usability changes.</summary>
    public override async Task SubscribeChannelEvents(ChannelEventSubscription request,
                                                       IServerStreamWriter<ChannelEventUpdate> responseStream,
                                                       ServerCallContext context)
    {
        using var queue = new LiveEventQueue<ChannelNotice>();
        var gate = new Lock();
        var known = new Dictionary<ChannelId, ChannelSubscriptionState>();

        void Publish(ChannelModel channel, bool newlyAdded = false, bool committedUpdate = false)
        {
            try
            {
                PublishCore(channel, newlyAdded, committedUpdate);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not publish channel subscription event for {ChannelId}",
                                   channel.ChannelId);
                queue.Fail(new RpcException(new Status(StatusCode.Internal, "channel subscription event mapping failed")));
            }
        }

        void PublishCore(ChannelModel channel, bool newlyAdded, bool committedUpdate)
        {
            lock (gate)
            {
                var previous = known.GetValueOrDefault(channel.ChannelId);
                var pending = IsPendingOpen(channel) && channel.State >= ChannelState.V1FundingSigned;
                var open = channel.State is >= ChannelState.Open and < ChannelState.Closed;
                var active = IsActive(channel);
                var closed = channel.State is ChannelState.OnchainResolving or ChannelState.Closed;
                var resolved = channel.State == ChannelState.Closed;
                var point = ToSubscriptionChannelPoint(channel);
                void Emit(ChannelEventUpdate value) => queue.Publish(new ChannelNotice(channel.ChannelId, value));
                if (pending && (!previous.Pending || newlyAdded) && point is not null)
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.PendingOpenChannel,
                        PendingOpenChannel = new PendingUpdate
                        {
                            Txid = point.FundingTxidBytes,
                            OutputIndex = point.OutputIndex
                        }
                    });
                if (open && (!previous.Open || previous.Closed) && channel.State == ChannelState.Open)
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.OpenChannel,
                        OpenChannel = ToLndChannel(channel, active)
                    });
                if (point is not null && active != previous.Active)
                    Emit(new ChannelEventUpdate
                    {
                        Type = active ? ChannelEventUpdate.Types.UpdateType.ActiveChannel
                                      : ChannelEventUpdate.Types.UpdateType.InactiveChannel,
                        ActiveChannel = active ? point : null,
                        InactiveChannel = active ? null : point
                    });
                if (closed && !previous.Closed)
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.ClosedChannel,
                        ClosedChannel = SubscriptionCloseSummary(channel)
                    });
                if (resolved && !previous.Resolved && point is not null)
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.FullyResolvedChannel,
                        FullyResolvedChannel = point
                    });
                if (channel.State == ChannelState.Stale && !previous.TimedOut && point is not null)
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.ChannelFundingTimeout,
                        ChannelFundingTimeout = point
                    });
                // Pinned LND includes high-frequency committed channel snapshots on its default feed.
                // The opening transition already has OPEN_CHANNEL; usability-only callbacks have their own type.
                if (committedUpdate && previous.Open && !previous.Closed && IsListed(channel))
                    Emit(new ChannelEventUpdate
                    {
                        Type = ChannelEventUpdate.Types.UpdateType.ChannelUpdate,
                        UpdatedChannel = new ChannelCommitUpdate { Channel = ToLndChannel(channel, active) }
                    });
                known[channel.ChannelId] = new ChannelSubscriptionState(pending, open, active, closed, resolved,
                                                                        channel.State == ChannelState.Stale);
            }
        }

        void Added(object? _, ChannelUpdatedEventArgs args) => Publish(args.Channel, true);
        void Updated(object? _, ChannelUpdatedEventArgs args)
        {
            if (args.IsPersisted)
                Publish(args.Channel, committedUpdate: true);
        }
        void Removed(object? _, ChannelUpdatedEventArgs args) => Publish(args.Channel);
        void UsabilityChanged(object? _, ChannelId id)
        {
            if (_channels.TryGetChannel(id, out var channel))
                Publish(channel);
        }
        void PeerChanged(object? _, PeerStateChangedEventArgs args)
        {
            foreach (var channel in _channels.FindChannels(c => c.RemoteNodeId == args.PeerPubKey))
                Publish(channel);
        }
        _channels.OnChannelAdded += Added;
        _channels.OnChannelUpdated += Updated;
        _channels.OnChannelRemoved += Removed;
        _peerManager.OnPeerStateChanged += PeerChanged;
        _reestablish?.OnUsabilityChanged += UsabilityChanged;
        try
        {
            // Attach first, then merge the initial state under the callback gate. Events that win the race are
            // retained; existing state is reconciled by ListChannels/PendingChannels rather than replayed here.
            lock (gate)
                foreach (var channel in _channels.FindChannels(_ => true))
                    known.TryAdd(channel.ChannelId, new ChannelSubscriptionState(
                        IsPendingOpen(channel) && channel.State >= ChannelState.V1FundingSigned,
                        channel.State is >= ChannelState.Open and < ChannelState.Closed, IsActive(channel),
                        channel.State is ChannelState.OnchainResolving or ChannelState.Closed,
                        channel.State == ChannelState.Closed, channel.State == ChannelState.Stale));
            await context.WriteResponseHeadersAsync(new Metadata());
            await queue.WriteToAsync(new ChannelNoticeWriter(this, responseStream), context.CancellationToken);
        }
        finally
        {
            _channels.OnChannelAdded -= Added;
            _channels.OnChannelUpdated -= Updated;
            _channels.OnChannelRemoved -= Removed;
            _peerManager.OnPeerStateChanged -= PeerChanged;
            _reestablish?.OnUsabilityChanged -= UsabilityChanged;
        }
    }

    private static ChannelPoint? ToSubscriptionChannelPoint(ChannelModel channel) =>
        channel.FundingOutput is { TransactionId: { } txid, Index: { } index }
            ? new ChannelPoint { FundingTxidBytes = ByteString.CopyFrom((byte[])txid), OutputIndex = index }
            : null;

    private ChannelCloseSummary SubscriptionCloseSummary(ChannelModel channel) => new()
    {
        ChannelPoint = ChannelPoint(channel),
        ChanId = ToChanId(channel.ShortChannelId),
        ChainHash = _nodeOptions.BitcoinNetwork.ChainHash.ToString(),
        ClosingTxHash = channel.ClosingTransaction?.TxId.ToString() ?? string.Empty,
        RemotePubkey = channel.RemoteNodeId.ToString(),
        Capacity = channel.FundingOutput?.Amount.Satoshi ?? 0,
        OpenInitiator = channel.IsInitiator ? Initiator.Local : Initiator.Remote,
        CloseInitiator = channel.LocalIsCloser switch
        {
            true => Initiator.Local,
            false => Initiator.Remote,
            null => Initiator.Unknown
        }
    };

    private readonly record struct ChannelSubscriptionState(bool Pending, bool Open, bool Active, bool Closed,
                                                            bool Resolved, bool TimedOut);
    private sealed record ChannelNotice(ChannelId ChannelId, ChannelEventUpdate Update);

    private sealed class ChannelNoticeWriter(LightningService service, IServerStreamWriter<ChannelEventUpdate> target)
        : IServerStreamWriter<ChannelNotice>
    {
        public WriteOptions? WriteOptions { get => target.WriteOptions; set => target.WriteOptions = value; }

        public Task WriteAsync(ChannelNotice message) => WriteAsync(message, CancellationToken.None);

        public async Task WriteAsync(ChannelNotice message, CancellationToken cancellationToken)
        {
            if (message.Update.ClosedChannel is { } summary)
            {
                await using var scope = service.CreateScope();
                var close = await UnitOfWork(scope).OnchainResolutionDbRepository.GetCloseAsync(message.ChannelId);
                if (close is not null)
                {
                    summary.ClosingTxHash = close.CommitmentTransactionId.ToString();
                    summary.CloseHeight = close.SpentAtHeight;
                    summary.CloseType = close.Kind switch
                    {
                        ChannelCloseKind.LocalCommitment => ChannelCloseSummary.Types.ClosureType.LocalForceClose,
                        ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment
                         or ChannelCloseKind.FutureCommitment =>
                            ChannelCloseSummary.Types.ClosureType.RemoteForceClose,
                        ChannelCloseKind.RevokedCommitment => ChannelCloseSummary.Types.ClosureType.BreachClose,
                        _ => ChannelCloseSummary.Types.ClosureType.CooperativeClose
                    };
                    summary.CloseInitiator = close.Kind == ChannelCloseKind.LocalCommitment
                                                ? Initiator.Local : Initiator.Remote;
                }
            }
            await target.WriteAsync(message.Update, cancellationToken);
        }
    }
}