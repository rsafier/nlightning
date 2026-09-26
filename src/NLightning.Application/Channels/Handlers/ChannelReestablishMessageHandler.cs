using System.Text;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Interfaces;
using Reestablish;
using Services;

/// <summary>
/// Receives the peer's <c>channel_reestablish</c> (BOLT 2 Message Retransmission, plan N7-T1..T4): runs the
/// <see cref="ReestablishPlanner"/> and returns what must be (re)sent, in wire order: our own
/// <c>channel_reestablish</c> first if it did not go out on this connection yet, then <c>tx_abort</c>,
/// <c>channel_ready</c>, our last <c>revoke_and_ack</c> (regenerated from the seed) and the stored
/// <c>commitment_signed</c> diff (verbatim) in their original order, then our unsigned updates with their original ids.
/// </summary>
/// <remarks>
/// A mismatch fails the channel (<see cref="ChannelFailedException"/>: <c>ChannelManager</c> persists Failed and the
/// error). Proven data loss (B2-RE-23) first persists <see cref="ChannelModel.DataLossDetected"/>, so we never sign or
/// broadcast our commitment again (I12), then fails the channel without broadcast. On success the channel is marked
/// reestablished in the <see cref="ReestablishTracker"/> by <c>ChannelManager</c>, which then pins the link and replays
/// the pending HTLC events. A <c>channel_reestablish</c> on a channel that turned Open on this connection is still
/// answered (with ours first, then the plan; LND sends one after channel_ready for a channel that was pending when the
/// connection started, and waits for ours forever). Only a second one after we answered one on the same connection is
/// ignored.
/// </remarks>
public class ChannelReestablishMessageHandler : IChannelMessageHandler<ChannelReestablishMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelReestablishMessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageSerializer _messageSerializer;
    private readonly ReestablishService _reestablishService;
    private readonly ReestablishTracker _tracker;
    private readonly ChannelStateTransitionService _transitions;
    private readonly IUnitOfWork _unitOfWork;

    public ChannelReestablishMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                            ILightningSigner lightningSigner,
                                            ILogger<ChannelReestablishMessageHandler> logger,
                                            IMessageFactory messageFactory, IMessageSerializer messageSerializer,
                                            ReestablishService reestablishService, ReestablishTracker tracker,
                                            ChannelStateTransitionService transitions, IUnitOfWork unitOfWork)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _messageSerializer = messageSerializer;
        _reestablishService = reestablishService;
        _tracker = tracker;
        _transitions = transitions;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(ChannelReestablishMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channelId = payload.ChannelId;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            // Known to the database only: closed or forgotten. BOLT 1: tell the peer, so it fails its copy
            throw new ChannelErrorException($"channel_reestablish for channel {channelId}, which is not active",
                                            channelId, "unknown channel");

        if (channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"channel_reestablish for channel {channelId} from another peer",
                                            channelId, "unknown channel");

        if (channel.State == ChannelState.Failed)
            throw new ChannelFailedException(channelId, $"channel_reestablish on failed channel {channelId}");

        if (channel.State is not (ChannelState.V1FundingSigned or ChannelState.ReadyForThem
                               or ChannelState.ReadyForUs or ChannelState.Open))
            throw new ChannelWarningException(
                $"Ignoring channel_reestablish on channel {channelId} in state {Enum.GetName(channel.State)}", channelId,
                "channel_reestablish ignored: channel not active");

        var replies = new List<IChannelMessage>();
        switch (_tracker.GetStatus(channelId))
        {
            case ReestablishStatus.Reestablished:
                // Answered on this connection already (turning Open here does not count: see ReestablishTracker)
                _logger.LogWarning("Ignoring a repeated channel_reestablish for channel {ChannelId}", channelId);
                return [];
            case ReestablishStatus.Awaiting:
                // Not sent on this connection yet (a channel waiting for channel_ready, or one that turned Open on this
                // connection before the peer's reestablish arrived): ours goes first
                replies.Add(await _reestablishService.CreateOwnAsync(channel));
                _tracker.MarkSent(channelId, peerPubKey);
                break;
        }

        var local = ReestablishService.GetLocalState(channel);
        var peer = new PeerReestablish(payload.NextCommitmentNumber, payload.NextRevocationNumber,
                                       payload.YourLastPerCommitmentSecret, message.NextFundingTlv is not null);
        var plan = ReestablishPlanner.Plan(local, peer,
                                           (number, secret) => _reestablishService.IsOurSecret(channel, number, secret));

        _logger.LogInformation(
            "channel_reestablish for {ChannelId}: ours {LocalNext}/{LocalRevocation}, theirs {Next}/{Revocation} -> {Outcome} [{Steps}]",
            channelId, local.LocalCommitmentNumber + 1, local.RemoteCommitmentNumber, peer.NextCommitmentNumber,
            peer.NextRevocationNumber, plan.Outcome, string.Join(", ", plan.Steps));

        switch (plan.Outcome)
        {
            case ReestablishOutcome.DataLoss:
                await PersistDataLossAsync(channel, plan);
                throw new ChannelFailedException(channelId, $"[{plan.RequirementId}] {plan.Reason}",
                                                 "we lost channel state, please fail the channel")
                {
                    RequirementId = plan.RequirementId
                };
            case ReestablishOutcome.Fail:
                throw new ChannelFailedException(channelId, $"[{plan.RequirementId}] {plan.Reason}",
                                                 $"channel_reestablish mismatch: {plan.Reason}")
                {
                    RequirementId = plan.RequirementId,
                    MustBroadcast = plan.MustBroadcast
                };
        }

        foreach (var step in plan.Steps)
            replies.AddRange(await BuildStepAsync(channel, step, local));

        return replies;
    }

    private async Task<IReadOnlyList<IChannelMessage>> BuildStepAsync(ChannelModel channel, ReestablishStep step,
                                                                      ReestablishLocalState local)
    {
        var channelId = channel.ChannelId;
        switch (step)
        {
            case ReestablishStep.TxAbort:
                return
                [
                    _messageFactory.CreateTxAbortMessage(
                        channelId, Encoding.ASCII.GetBytes("no interactive funding transaction on this channel"))
                ];

            case ReestablishStep.ChannelReady:
                // Only a channel_ready we already sent is retransmitted (not while we wait for our own confirmation)
                if (channel.State is not (ChannelState.ReadyForUs or ChannelState.Open))
                    return [];

                var secondPoint = _lightningSigner.GetPerCommitmentPoint(channelId, local.LocalCommitmentNumber + 1);
                // The alias we gave the peer, else the real scid (as at funding confirmation); none while unknown
                ShortChannelId? alias = null;
                if (channel.LocalAliases is { Count: > 0 } aliases)
                    alias = aliases.First();
                else if (IsSet(channel.ShortChannelId))
                    alias = channel.ShortChannelId;
                return [_messageFactory.CreateChannelReadyMessage(channelId, secondPoint, alias)];

            case ReestablishStep.RevokeAndAck:
                // Deterministic from the seed (D4): the secret of L - 1 and the point of L + 1
                var l = local.LocalCommitmentNumber;
                return [_transitions.CreateRevokeAndAck(channel, new OutboundRevokeAndAck(l - 1, l + 1))];

            case ReestablishStep.CommitDiff:
                var diff = channel.SentCommitDiff
                        ?? throw new InvalidOperationException($"Channel {channelId} has no stored commitment diff");
                var messages = await SentCommitDiffCodec.DecodeAsync(_messageSerializer, diff);
                return messages.Select(m => m as IChannelMessage
                                         ?? throw new InvalidOperationException(
                                                $"The stored diff of channel {channelId} holds a {m.Type}"))
                               .ToList();

            case ReestablishStep.UnsignedUpdates:
                return ChannelStateTransitionService.PendingLocalUpdates(channel.Commitments!)
                                                    .Select(u => _transitions.ToWireMessage(channel, u))
                                                    .ToList();

            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown reestablish step");
        }
    }

    /// <summary><c>default(ShortChannelId)</c> (a channel not confirmed yet) has no bytes.</summary>
    private static bool IsSet(ShortChannelId scid)
    {
        byte[]? bytes = scid;
        return bytes is { Length: ShortChannelId.Length };
    }

    /// <summary>Persists the data-loss flag before anything else (I12), then keeps it in memory.</summary>
    private async Task PersistDataLossAsync(ChannelModel channel, ReestablishPlan plan)
    {
        _logger.LogCritical(
            "DATA LOSS on channel {ChannelId}: {Reason}. Our commitment must never be broadcast; asking the peer to close",
            channel.ChannelId, plan.Reason);

        channel.MarkDataLossDetected();
        _channelMemoryRepository.UpdateChannel(channel);
        await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await _unitOfWork.SaveChangesAsync();
    }
}