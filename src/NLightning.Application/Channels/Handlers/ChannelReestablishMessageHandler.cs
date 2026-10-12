using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Close;
using Close.Simple;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using DualFunding;
using Gossip.Announcements.Interfaces;
using InteractiveTx.Interfaces;
using Interfaces;
using Reestablish;
using Services;
using Splicing;
using Taproot;

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
    private readonly ClosingNegotiationRegistry? _closingRegistry;
    private readonly DualFundReestablish? _dualFundReestablish;
    private readonly IServiceProvider? _serviceProvider;

    public ChannelReestablishMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                            ILightningSigner lightningSigner,
                                            ILogger<ChannelReestablishMessageHandler> logger,
                                            IMessageFactory messageFactory, IMessageSerializer messageSerializer,
                                            ReestablishService reestablishService, ReestablishTracker tracker,
                                            ChannelStateTransitionService transitions, IUnitOfWork unitOfWork,
                                            ClosingNegotiationRegistry? closingRegistry = null,
                                            DualFundReestablish? dualFundReestablish = null,
                                            IServiceProvider? serviceProvider = null)
    {
        _closingRegistry = closingRegistry;
        _dualFundReestablish = dualFundReestablish;
        _serviceProvider = serviceProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _messageSerializer = messageSerializer;
        _reestablishService = reestablishService;
        _tracker = tracker;
        _transitions = transitions;
        _unitOfWork = unitOfWork;
        _taproot = new TaprootReestablish(logger, messageSerializer, transitions);
    }

    /// <summary>The simple taproot rules (nonces required, commitment_signed signed again; NL-877 T3).</summary>
    private readonly TaprootReestablish _taproot;

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
                               or ChannelState.ReadyForUs or ChannelState.Open or ChannelState.ShuttingDown
                               or ChannelState.Negotiating or ChannelState.Closing))
            throw new ChannelWarningException(
                $"Ignoring channel_reestablish on channel {channelId} in state {Enum.GetName(channel.State)}", channelId,
                "channel_reestablish ignored: channel not active");

        // A previous reconnect may have lost its signer reply on this connection. Complete its owned, persisted
        // snapshot before installing this handshake's replacement verification nonces or changing fundings.
        await _transitions.ResumeSigningWorkflowsAsync(channel);

        // NL-867: an RBF attempt of a dual-funded open whose earlier attempt confirmed is abandoned before anything else
        // (our own channel_reestablish then names no next_funding for it), so the peer's next_funding for it gets our
        // tx_abort and never its commitment_signed again (which it could not sign: its inputs are spent)
        var abandoned = _dualFundReestablish is not null
                            ? await _dualFundReestablish.AbandonConfirmedRbfAttemptAsync(channel)
                            : [];

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
                replies.Add(await _reestablishService.CreateOwnAsync(channel, negotiatedFeatures));
                _tracker.MarkSent(channelId, peerPubKey);
                break;
        }

        var local = await _reestablishService.GetLocalStateAsync(channel, negotiatedFeatures);

        // NL-867: the same for a splice RBF attempt whose pending sibling confirmed (resumed from its rows first)
        if (abandoned.Count == 0 && local.LatestInteractiveTx is { IsSplice: true, TxSignaturesSent: false }
                                 && _serviceProvider?.GetService<SpliceService>() is { } spliceService)
        {
            abandoned = await spliceService.AbandonConfirmedRbfAttemptAsync(channel, _unitOfWork);
            if (abandoned.Count > 0)
                local = await _reestablishService.GetLocalStateAsync(channel, negotiatedFeatures);
        }

        var peer = new PeerReestablish(payload.NextCommitmentNumber, payload.NextRevocationNumber,
                                       payload.YourLastPerCommitmentSecret, message.NextFundingTlv is not null,
                                       message.NextFundingTlv is { } nextFunding
                                           ? new ReestablishFundingField(new TxId(nextFunding.NextFundingTxId),
                                                                         nextFunding.RetransmitFlags)
                                           : null,
                                       message.MyCurrentFundingLockedTlv is { } fundingLocked
                                           ? new ReestablishFundingField(fundingLocked.FundingTxId,
                                                                         fundingLocked.RetransmitFlags)
                                           : null);
        var plan = ReestablishPlanner.Plan(local, peer,
                                           (number, secret) => _reestablishService.IsOurSecret(channel, number, secret));

        _logger.LogInformation(
            "channel_reestablish for {ChannelId}: ours {LocalNext}/{LocalRevocation}, theirs {Next}/{Revocation} (next_funding {NextFunding}, my_current_funding_locked {FundingLocked}) -> {Outcome} [{Steps}]",
            channelId, local.LocalCommitmentNumber + 1, local.RemoteCommitmentNumber, peer.NextCommitmentNumber,
            peer.NextRevocationNumber, peer.NextFunding, peer.MyCurrentFundingLocked, plan.Outcome,
            string.Join(", ", plan.Steps));

        // A Closing channel's transaction is agreed, persisted and broadcast: a mismatch can't fail it (that would
        // broadcast a commitment against the close); only our shutdown is retransmitted
        if (channel.State == ChannelState.Closing && plan.Outcome != ReestablishOutcome.Resume)
        {
            _logger.LogWarning(
                "channel_reestablish mismatch on closing channel {ChannelId} ({Requirement}: {Reason}); waiting for the closing transaction",
                channelId, plan.RequirementId, plan.Reason);
            plan = plan with { Steps = [] };
        }

        switch (plan.Outcome)
        {
            case ReestablishOutcome.DataLoss when channel.State != ChannelState.Closing:
                await PersistDataLossAsync(channel, plan);
                throw new ChannelFailedException(channelId, $"[{plan.RequirementId}] {plan.Reason}",
                                                 "we lost channel state, please fail the channel")
                {
                    RequirementId = plan.RequirementId
                };
            case ReestablishOutcome.Fail when channel.State != ChannelState.Closing:
                throw new ChannelFailedException(channelId, $"[{plan.RequirementId}] {plan.Reason}",
                                                 $"channel_reestablish mismatch: {plan.Reason}")
                {
                    RequirementId = plan.RequirementId,
                    MustBroadcast = plan.MustBroadcast
                };
        }

        // Simple taproot channels: the peer's next_local_nonces replace its verification nonces before anything is
        // retransmitted (a missing map or entry fails the channel; NL-877 T3)
        // (a dual-funded open waiting for its funding: one entry per signed RBF attempt, NL-970)
        await _taproot.ReceiveNoncesAsync(channel, message,
                                          channel.ChannelParams.OptionSimpleTaproot
                                              ? await _reestablishService.GetSignedOpenAttemptsAsync(channel)
                                              : null);

        // A dual-funded open waiting for its funding resumes its negotiation first (the driver forgot it on a restart),
        // so the peer's retransmitted tx_signatures and our own rebuilt ones find it
        if (_dualFundReestablish is not null && DualFundReestablish.IsPendingOpen(channel)
                                             && local.LatestInteractiveTx is not null)
        {
            await _dualFundReestablish.EnsureLoadedAsync(channel);

            // A simple taproot open re-signs its commitment_signed against the peer's current_commit_nonce (PR #1324)
            await _dualFundReestablish.ReceiveCurrentCommitNonceAsync(channel, message.CurrentCommitNonceTlv?.Nonce);
        }

        // A splice our commitment_signed started resumes too (the driver and the splice service forgot it on a
        // restart), so the peer's retransmitted splice commitment_signed and tx_signatures complete it
        if (local.LatestInteractiveTx is { IsSplice: true, TxSignaturesReceived: false }
         && _serviceProvider?.GetService<SpliceService>() is { } splices)
        {
            await splices.EnsureLoadedAsync(channel, _unitOfWork);

            // A taproot splice takes the peer's nonces for it (BOLTs PR #1324 types 22 and 24, NL-965)
            splices.ReceiveReestablishNonces(channel, message);
        }

        // SP-RE-04: the peer's my_current_funding_locked processed as its splice_locked, before the retransmissions
        if (plan.PeerSpliceLocked is { } lockedTxId && plan.Outcome == ReestablishOutcome.Resume)
            replies.AddRange(await ProcessPeerSpliceLockedAsync(channel, lockedTxId));

        // NL-867: the abandoned attempt's tx_abort answers the peer's next_funding (the planner's TxAbort step too)
        replies.AddRange(abandoned);
        foreach (var step in plan.Steps.Where(s => s != ReestablishStep.TxAbort || abandoned.Count == 0))
            replies.AddRange(await BuildStepAsync(channel, step, local, peer, peerPubKey));

        // B2-RE-28: our shutdown again, after the retransmitted updates; the fee negotiation restarts (B2-RE-29)
        // (a simple taproot channel's carries a fresh closee nonce: the old secrets are forgotten)
        if (channel.LocalShutdownScript is { } shutdownScript)
        {
            var closingEntry = _closingRegistry?.Get(channelId);
            replies.Add(TaprootCloseNonces.CreateShutdown(channel, shutdownScript, _messageFactory, _lightningSigner,
                                                          closingEntry));
            closingEntry?.ShutdownSentOnConnection = true;
        }

        return replies;
    }

    private async Task<IReadOnlyList<IChannelMessage>> BuildStepAsync(ChannelModel channel, ReestablishStep step,
                                                                      ReestablishLocalState local,
                                                                      PeerReestablish peer, CompactPubKey peerPubKey)
    {
        var channelId = channel.ChannelId;
        switch (step)
        {
            case ReestablishStep.NextFundingCommitmentSigned:
                return await CreateInteractiveCommitmentSignedAsync(channel, local.LatestInteractiveTx!);

            case ReestablishStep.NextFundingTxSignatures:
                // BOLT 2: "if it has already received tx_signatures for that funding transaction: MUST send its
                // tx_signatures". The driver's copy while it holds the negotiation, else (after a restart) the one
                // rebuilt from the stored row: our witnesses were saved before the first one went out
                var latest = local.LatestInteractiveTx!;
                if (_serviceProvider?.GetService<IInteractiveTxDriver>()?.CreateTxSignaturesRetransmission(
                        channelId, latest.TxId) is { } txSignatures)
                    return [txSignatures];

                if (await _reestablishService.CreateStoredTxSignaturesAsync(channel, latest.TxId) is { } stored)
                {
                    _logger.LogInformation(
                        "Retransmitting our stored tx_signatures for {TxId} of channel {ChannelId} (next_funding)",
                        latest.TxId, channelId);
                    return [stored];
                }

                _logger.LogWarning(
                    "Our tx_signatures for {TxId} of channel {ChannelId} are due again but none were signed yet",
                    latest.TxId, channelId);
                return [];

            case ReestablishStep.AnnouncementSignatures:
                return await CreateAnnouncementSignaturesAsync(channel, peer.MyCurrentFundingLocked!.TxId, peerPubKey);

            case ReestablishStep.TxAbort:
                // Through the interactive-tx driver when there is one, so the peer's echo is taken as the echo and not
                // answered again (a tx_abort ping-pong otherwise). The driver sends none while it holds a negotiation
                // or an RBF request, or its tx_abort already waits for the echo: a raw one would abort that
                // negotiation behind its back (or answer the echo again), so nothing goes out then
                const string abortReason = "unknown next_funding_txid";
                if (_serviceProvider?.GetService<IInteractiveTxDriver>() is { } abortDriver)
                {
                    var abort = abortDriver.AbortQuiescence(channelId, peerPubKey, abortReason);
                    if (abort.Count == 0 && abortDriver.GetInfo(channelId) is { AwaitingAbortEcho: true, SessionId: null })
                        // Our tx_abort for that negotiation went out already (e.g. an RBF attempt abandoned when an
                        // earlier attempt confirmed, NL-867): it answers the next_funding as well
                        _logger.LogInformation(
                            "Our tx_abort for the next_funding {TxId} of channel {ChannelId} went out already",
                            peer.NextFunding?.TxId, channelId);
                    else if (abort.Count == 0)
                        _logger.LogWarning(
                            "No tx_abort for the unknown next_funding {TxId} of channel {ChannelId}: the interactive-tx "
                          + "driver holds a negotiation or already waits for its tx_abort's echo",
                            peer.NextFunding?.TxId, channelId);
                    return abort;
                }

                return [_messageFactory.CreateTxAbortMessage(channelId, Encoding.ASCII.GetBytes(abortReason))];

            case ReestablishStep.ChannelReady:
                // Only a channel_ready we already sent is retransmitted (not while we wait for our own confirmation). A
                // closing channel was Open before its first shutdown, so it sent one too (B2-RE-15: both next numbers 1)
                if (channel.State is not (ChannelState.ReadyForUs or ChannelState.Open or ChannelState.ShuttingDown
                                       or ChannelState.Negotiating or ChannelState.Closing))
                    return [];

                var secondPoint = _lightningSigner.GetPerCommitmentPoint(channelId, local.LocalCommitmentNumber + 1);

                // A simple taproot channel's carries our verification nonce for that commitment again (NL-877 T3)
                if (channel.ChannelParams.OptionSimpleTaproot)
                    return CreateTaprootChannelReady(channel, local.LocalCommitmentNumber + 1, secondPoint);

                // One channel_ready per local alias, as the funding confirmation sent them (the peer must recognize
                // every alias for incoming HTLCs; NL-260), else the real scid (as at funding confirmation), or none
                // in the TLV while both are unknown
                if (channel.LocalAliases is { Count: > 0 } localAliases)
                    return localAliases
                          .Select(alias => (IChannelMessage)_messageFactory.CreateChannelReadyMessage(
                                      channelId, secondPoint, alias))
                          .ToList();

                // No ternary here: `IsSet(...) ? channel.ShortChannelId : null` would type the conditional as
                // ShortChannelId and convert the null via the implicit byte[] operator (the NL-369 trap)
                ShortChannelId? aliasOrScid = null;
                if (IsSet(channel.ShortChannelId))
                    aliasOrScid = channel.ShortChannelId;

                return [_messageFactory.CreateChannelReadyMessage(channelId, secondPoint, aliasOrScid)];

            case ReestablishStep.RevokeAndAck:
                // Deterministic from the seed (D4): the secret of L - 1 and the point of L + 1
                var l = local.LocalCommitmentNumber;
                return [await _transitions.CreateRevokeAndAckAsync(channel, new OutboundRevokeAndAck(l - 1, l + 1))];

            case ReestablishStep.CommitDiff:
                var diff = channel.SentCommitDiff
                        ?? throw new InvalidOperationException($"Channel {channelId} has no stored commitment diff");

                // A MuSig2 signature is never replayed: signed again against the peer's new nonce (NL-877 T3)
                if (channel.ChannelParams.OptionSimpleTaproot)
                    return await _taproot.ResignCommitDiffAsync(channel, diff);

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

    /// <summary>
    /// SP-RE-03 / SP2-A-T2: our <c>commitment_signed</c> for the latest interactive funding transaction, byte-identical
    /// to the original: a splice's from the signatures stored with the peer's commitment on that funding (the save that
    /// preceded the first one), a dual-funded open's re-signed by the open (RFC 6979, the same signature).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> CreateInteractiveCommitmentSignedAsync(
        ChannelModel channel, ReestablishInteractiveTxState latest)
    {
        if (!latest.IsSplice)
        {
            return _dualFundReestablish is not null
                       ? await _dualFundReestablish.CreateCommitmentSignedRetransmissionAsync(channel, latest.TxId)
                       : [];
        }

        // A MuSig2 signature is never replayed: a taproot splice's is signed again against the peer's
        // current_commit_nonce (BOLTs PR #1324, NL-965)
        if (channel.ChannelParams.OptionSimpleTaproot)
            return _serviceProvider?.GetService<SpliceService>() is { } spliceService
                       ? await spliceService.ResignSpliceCommitmentAsync(channel, latest.TxId, _unitOfWork)
                       : [];

        (Domain.Channels.Commitments.RemoteCommit Commit, Domain.Channels.Commitments.CommitmentSignatures? Sent)?
            stored = null;
        try
        {
            if (_unitOfWork.ChannelFundingDbRepository is { } fundings)
                stored = await fundings.GetRemoteCommitmentAsync(channel.ChannelId, latest.TxId);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            stored = null;
        }

        if (stored is not { Sent: { } signatures })
        {
            _logger.LogWarning(
                "Our commitment_signed for splice {TxId} of channel {ChannelId} is due again but it is not stored",
                latest.TxId, channel.ChannelId);
            return [];
        }

        return
        [
            _messageFactory.CreateCommitmentSignedMessage(channel.ChannelId, signatures.Signature,
                                                          signatures.HtlcSignatures, latest.TxId)
        ];
    }

    /// <summary>
    /// SP-RE-04: the peer's <c>my_current_funding_locked</c> for a pending splice whose <c>splice_locked</c> we lack is
    /// processed as that <c>splice_locked</c> (<see cref="ISpliceService.HandlePeerFundingLockedAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> ProcessPeerSpliceLockedAsync(ChannelModel channel,
                                                                                    TxId fundingTxId)
    {
        if (_serviceProvider?.GetService<ISpliceService>() is not { } spliceService)
        {
            _logger.LogWarning("my_current_funding_locked {TxId} of channel {ChannelId} names a pending splice but no "
                             + "splice service is registered", fundingTxId, channel.ChannelId);
            return [];
        }

        _logger.LogInformation(
            "my_current_funding_locked {TxId} of channel {ChannelId} taken as the peer's splice_locked (SP-RE-04)",
            fundingTxId, channel.ChannelId);
        return await spliceService.HandlePeerFundingLockedAsync(channel, fundingTxId, _unitOfWork);
    }

    /// <summary>
    /// SP-RE-04: our <c>announcement_signatures</c> again for the funding the peer's <c>my_current_funding_locked</c>
    /// names with bit 0, when that is the channel's current funding, we are ready to send them (SP-G-01) and they did
    /// not go out on this connection yet; the sent time is saved first (as the announcement service does).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> CreateAnnouncementSignaturesAsync(ChannelModel channel,
                                                                                         TxId fundingTxId,
                                                                                         CompactPubKey peerPubKey)
    {
        if (_serviceProvider?.GetService<IChannelAnnouncementService>() is not { } announcements
         || !channel.AnnounceChannel || announcements.WasSentOnConnection(channel.ChannelId)
         || channel.FundingOutput?.TransactionId != fundingTxId
         || !ReestablishService.IsReadyForAnnouncementSignatures(announcements, channel, fundingTxId))
            return [];

        var message = announcements.CreateAnnouncementSignatures(channel);
        channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UtcNow);
        _channelMemoryRepository.UpdateChannel(channel);
        await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await _unitOfWork.SaveChangesAsync();
        announcements.MarkSentOnConnection(channel.ChannelId, peerPubKey);
        _logger.LogInformation("Retransmitting our announcement_signatures for channel {ChannelId} (SP-RE-04)",
                               channel.ChannelId);
        return [message];
    }

    /// <summary>
    /// A simple taproot channel's retransmitted <c>channel_ready</c>s (one per local alias, else the real scid or none):
    /// with <c>next_local_nonce</c>, our verification nonce for local commitment <paramref name="number"/> on the current
    /// funding.
    /// </summary>
    private IReadOnlyList<IChannelMessage> CreateTaprootChannelReady(ChannelModel channel, ulong number,
                                                                     CompactPubKey secondPoint)
    {
        var nonce = TaprootChannelNonces.GetCurrentFundingNonce(_lightningSigner, channel, number);
        if (channel.LocalAliases is { Count: > 0 } localAliases)
            return localAliases.Select(alias => (IChannelMessage)_messageFactory.CreateChannelReadyMessage(
                                           channel.ChannelId, secondPoint, alias, nonce))
                               .ToList();

        ShortChannelId? aliasOrScid = null;
        if (IsSet(channel.ShortChannelId))
            aliasOrScid = channel.ShortChannelId;

        return [_messageFactory.CreateChannelReadyMessage(channel.ChannelId, secondPoint, aliasOrScid, nonce)];
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

        // The signer refuses every signature for this channel from now on, broadcast included (I12)
        _lightningSigner.MarkDataLoss(channel.ChannelId);
    }
}