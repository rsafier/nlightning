using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Keysend;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Interfaces;
using Payments.Events;
using Quiescence;
using Taproot;

/// <summary>
/// Runs commitment state machine transitions for the BOLT 2 normal-operation handlers (plan N6-T1, decision D3):
/// the state guard, persist-before-send, the in-memory swap after the save, the <c>revoke_and_ack</c> secret derived
/// only after the save, signing the peer's next commitment when changes are pending, and the mapping of engine
/// violations to what the peer receives.
/// </summary>
/// <remarks>
/// Scoped (one per message): it uses the scope's <see cref="IUnitOfWork"/>. Callers hold the channel's lock. Every
/// transition is persisted with exactly one <see cref="IUnitOfWork.SaveChangesAsync"/>
/// (<see cref="IChannelStateDbRepository.ApplyAsync"/>), then <see cref="ChannelModel.UpdateCommitments"/> swaps the
/// snapshot (invariant I2), then the caller sends (I1); the transition's events go to the
/// <see cref="ChannelDomainEventQueue"/>, which <c>ChannelManager</c> hands to the HTLC switch once the lock is released.
/// </remarks>
public sealed class ChannelStateTransitionService
{
    /// <summary>The lowest <c>feerate_per_kw</c> we accept in <c>update_fee</c> (BOLT 3's 253 sat/kw floor).</summary>
    public const uint MinAcceptableFeeratePerKw = 253;

    /// <summary>
    /// The highest <c>feerate_per_kw</c> we accept in <c>update_fee</c> before calling it "unreasonably large"
    /// (B2-FEE-R01): 250,000 sat/kw = 1,000 sat/vB.
    /// </summary>
    public const uint MaxAcceptableFeeratePerKw = 250_000;

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ChannelDomainEventQueue _eventQueue;
    private readonly ICommitmentSigner _commitmentSigner;
    private readonly ICommitmentVerifier? _commitmentVerifier;
    private readonly ICommitScheduler? _commitScheduler;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelStateTransitionService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageSerializer _messageSerializer;
    private readonly NodeOptions _nodeOptions;
    private readonly ISecretStorageServiceFactory _secretStorageServiceFactory;
    private readonly IStfuReleaseScheduler? _stfuReleaseScheduler;
    private readonly IUnitOfWork _unitOfWork;
    private readonly HtlcEventMonitor? _htlcMonitor;

    public ChannelStateTransitionService(IChannelMemoryRepository channelMemoryRepository,
                                         ChannelDomainEventQueue eventQueue, ICommitmentSigner commitmentSigner,
                                         ILightningSigner lightningSigner,
                                         ILogger<ChannelStateTransitionService> logger, IMessageFactory messageFactory,
                                         IMessageSerializer messageSerializer, IOptions<NodeOptions> nodeOptions,
                                         ISecretStorageServiceFactory secretStorageServiceFactory,
                                         IUnitOfWork unitOfWork, IStfuReleaseScheduler? stfuReleaseScheduler = null,
                                         ICommitmentVerifier? commitmentVerifier = null,
                                         ICommitScheduler? commitScheduler = null, HtlcEventMonitor? htlcMonitor = null)
    {
        _htlcMonitor = htlcMonitor;
        _stfuReleaseScheduler = stfuReleaseScheduler;
        _commitmentVerifier = commitmentVerifier;
        _commitScheduler = commitScheduler;
        _channelMemoryRepository = channelMemoryRepository;
        _eventQueue = eventQueue;
        _commitmentSigner = commitmentSigner;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _messageSerializer = messageSerializer;
        _nodeOptions = nodeOptions.Value;
        _secretStorageServiceFactory = secretStorageServiceFactory;
        _unitOfWork = unitOfWork;
    }

    #region Guard

    /// <summary>
    /// The channel a normal-operation message may change (BOLT2 plan N6-T1 state guard, B2-NO-02).
    /// </summary>
    /// <param name="channelId">The message's channel.</param>
    /// <param name="currentState">The channel's state as seen by the dispatcher.</param>
    /// <param name="messageName">The message name, for the texts.</param>
    /// <exception cref="ChannelWarningException">HTLCs are disabled (<see cref="NodeOptions.HtlcsEnabled"/>; the
    /// message is ignored as before N6), the channel is not in memory, has no commitment state (opened before this
    /// wiring, NL-232), or is neither <see cref="ChannelState.Open"/> nor <see cref="ChannelState.ShuttingDown"/> (warning
    /// and close the connection; HTLCs in flight still settle after a <c>shutdown</c>, BOLT 2).</exception>
    /// <exception cref="ChannelErrorException">The channel was failed: every update is refused and the peer gets the
    /// <c>error</c> again.</exception>
    public ChannelModel GetUpdatableChannel(ChannelId channelId, ChannelState currentState, string messageName)
    {
        if (!_nodeOptions.HtlcsEnabled)
            throw new ChannelWarningException($"Ignoring {messageName}: HTLCs are disabled on this node", channelId,
                                              $"{messageName} is not supported yet, message ignored");

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new ChannelWarningException($"Ignoring {messageName}: channel {channelId} is not loaded", channelId,
                                              $"{messageName} ignored: channel not ready");

        if (currentState == ChannelState.Failed || channel.State == ChannelState.Failed)
            throw new ChannelErrorException($"Refusing {messageName} on failed channel {channelId}", channelId,
                                            ChannelFailedException.DefaultPeerMessage);

        if (!CarriesUpdates(currentState) || !CarriesUpdates(channel.State))
            throw new ChannelWarningException(
                $"[B2-NO-02] {messageName} on channel {channelId} in state {Enum.GetName(channel.State)}", channelId,
                $"{messageName} before the channel is open")
            {
                CloseConnection = true
            };

        if (channel.Commitments is null)
            throw new ChannelWarningException(
                $"Ignoring {messageName}: channel {channelId} has no commitment state (opened before HTLC support)",
                channelId, $"{messageName} is not supported on this channel, message ignored");

        return channel;
    }

    /// <summary>
    /// True for the states in which updates, <c>commitment_signed</c> and <c>revoke_and_ack</c> flow:
    /// <see cref="ChannelState.Open"/>, and <see cref="ChannelState.ShuttingDown"/> until no HTLC is left (BOLT 2: after
    /// <c>shutdown</c> no new HTLC, but the ones in flight are still fulfilled or failed).
    /// </summary>
    public static bool CarriesUpdates(ChannelState state) => state is ChannelState.Open or ChannelState.ShuttingDown;

    /// <summary>
    /// What the peer receives for a rule it broke (the engine state is unchanged): "send an <c>error</c> and fail the
    /// channel" when BOLT 2 allows nothing else (<see cref="CommitmentViolationException.MustFailChannel"/>), else
    /// "send a <c>warning</c> and close the connection", which BOLT 2 allows for every other normal-operation
    /// violation and which the reconnection (reestablish) recovers from.
    /// </summary>
    public static ChannelErrorException ToFailure(CommitmentViolationException violation, ChannelId channelId)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return new ChannelFailedException(channelId, violation.Message, violation, violation.PeerMessage)
        {
            RequirementId = violation.RequirementId
        };
    }

    /// <inheritdoc cref="ToFailure"/>
    public static Exception ToPeerException(CommitmentViolationException violation, ChannelId channelId)
    {
        ArgumentNullException.ThrowIfNull(violation);
        if (violation.MustFailChannel)
            return ToFailure(violation, channelId);

        return new ChannelWarningException(violation.Message, channelId, violation, violation.PeerMessage)
        {
            CloseConnection = true
        };
    }

    #endregion

    #region Transitions

    /// <summary>
    /// Persists <paramref name="result"/> in one save, then swaps the snapshot in memory and queues its events. Nothing
    /// changes in memory when the save fails.
    /// </summary>
    /// <param name="channel">The channel the transition belongs to.</param>
    /// <param name="result">The engine transition.</param>
    /// <param name="extras">What else the transition writes (sent diff, shachain, ...).</param>
    /// <param name="stageWithTransition">Stages more writes on the same unit of work after the transition is staged and
    /// before the one save (for example the <c>HtlcOrigin</c> of an offered HTLC, NL-250), so they commit or fail
    /// together with it.</param>
    /// <remarks>
    /// Quiescence (splicing plan Q1-T3): after the swap, a <c>stfu</c> this channel owes (our request, or our reply to
    /// the peer's) is scheduled for release (<see cref="IStfuReleaseScheduler"/>): the <c>commitment_signed</c> /
    /// <c>revoke_and_ack</c> transitions are what drain our pending updates (Q-S-02). The release runs after the lock
    /// is released, so the <c>stfu</c> follows this transition's own messages (the <c>revoke_and_ack</c> the handler
    /// returns) on the wire.
    /// </remarks>
    public async Task CommitAsync(ChannelModel channel, CommitmentsResult result, ChannelStateExtras? extras = null,
                                  Func<IUnitOfWork, Task>? stageWithTransition = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(result);

        await _unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition, extras);
        if (stageWithTransition is not null)
            await stageWithTransition(_unitOfWork);
        await _unitOfWork.SaveChangesAsync();

        channel.UpdateCommitments(result.Next, extras);
        _channelMemoryRepository.UpdateChannel(channel);
        _eventQueue.Enqueue(result.Events);
        _htlcMonitor?.ObserveCommitted(channel, result);
        _stfuReleaseScheduler?.ScheduleRelease(channel.ChannelId);
    }

    /// <summary>
    /// Builds our <c>revoke_and_ack</c> for a persisted <c>commitment_signed</c>: tells the signer the new local
    /// commitment is persisted, then releases the secret of the revoked one (invariant I3: only after the save). A
    /// simple taproot channel's carries <c>next_local_nonces</c>: per active funding, our verification nonce for the
    /// commitment the peer's next <c>commitment_signed</c> creates (<see cref="OutboundRevokeAndAck.NextCommitmentNumber"/>;
    /// counter-derived, so a retransmission repeats it).
    /// </summary>
    public RevokeAndAckMessage CreateRevokeAndAck(ChannelModel channel, OutboundRevokeAndAck revokeAndAck)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(revokeAndAck);

        var newLocalNumber = checked(revokeAndAck.NextCommitmentNumber - 1);
        _lightningSigner.AdvanceLocalCommitment(channel.ChannelId, newLocalNumber);
        var secret = _lightningSigner.RevealPerCommitmentSecret(channel.ChannelId,
                                                                revokeAndAck.RevokedCommitmentNumber);
        var nextPoint = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, revokeAndAck.NextCommitmentNumber);
        if (!channel.ChannelParams.OptionSimpleTaproot)
            return _messageFactory.CreateRevokeAndAckMessage(channel.ChannelId, secret, nextPoint);

        var nonces = TaprootChannelNonces.CreateLocalNonces(_lightningSigner, channel,
                                                            revokeAndAck.NextCommitmentNumber);
        return _messageFactory.CreateRevokeAndAckMessage(channel.ChannelId, secret, nextPoint, nonces);
    }

    /// <summary>
    /// Simple taproot channels (NL-877 T3): <see cref="ChannelCommitments.ResignRemoteNextCommit"/> with this scope's
    /// commitment signer, for the retransmission of our unacked <c>commitment_signed</c> after a reconnection. The
    /// caller persists the result (<see cref="CommitAsync"/>) before it sends anything.
    /// </summary>
    public CommitmentsResult ResignRemoteNextCommit(ChannelCommitments commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        return commitments.ResignRemoteNextCommit(_commitmentSigner);
    }

    /// <summary>
    /// The peer's signatures of a received <c>commitment_signed</c> as the engine takes them. On a simple taproot
    /// channel (bolt-simple-taproot.md §Channel Operation) the 64-byte <c>signature</c> must be all zeros and the
    /// MuSig2 partial signature is <c>partial_signature_with_nonce</c> (its absence is refused by the engine,
    /// TAPROOT-CS-R01); the TLV is ignored on the other channel types.
    /// </summary>
    /// <exception cref="ChannelFailedException">A non-zero <c>signature</c> on a simple taproot channel (MUST fail the
    /// channel).</exception>
    public static CommitmentSignatures ToReceivedSignatures(ChannelModel channel, CommitmentSignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        if (!channel.ChannelParams.OptionSimpleTaproot)
            return new CommitmentSignatures(payload.Signature, payload.HtlcSignatures.ToList());

        if (!payload.Signature.IsZero)
            throw new ChannelFailedException(channel.ChannelId,
                                             "[TAPROOT-CS-R00] commitment_signed with a non-zero signature on a simple "
                                           + "taproot channel", "commitment_signed signature must be zero")
            {
                RequirementId = "TAPROOT-CS-R00"
            };

        return new CommitmentSignatures(payload.Signature, payload.HtlcSignatures.ToList())
        {
            PartialSignature = message.PartialSignatureWithNonceTlv?.PartialSignatureWithNonce
        };
    }

    /// <summary>
    /// Signs the peer's next commitment when changes are pending and no <c>commitment_signed</c> is waiting for its
    /// <c>revoke_and_ack</c> (D7), persisting it together with the sent diff (D4) before returning the message.
    /// </summary>
    /// <returns>The <c>commitment_signed</c> to send, or null when there is nothing to sign.</returns>
    /// <remarks>
    /// With a pending splice the signature is a batch (<c>start_batch</c> and one <c>commitment_signed</c> per active
    /// funding, SP-OP-03), which this single-message form cannot return: nothing is signed here then, and the
    /// signature is handed to the <see cref="ICommitScheduler"/> (when one is registered), which signs and publishes the
    /// whole batch through <see cref="SignPendingAsync"/> after the caller's replies. Callers that can send several
    /// messages should call <see cref="SignPendingAsync"/> directly.
    /// </remarks>
    public async Task<CommitmentSignedMessage?> SignIfPendingAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Commitments is { PendingFundings.IsEmpty: false, CanSendCommit: true })
        {
            if (_commitScheduler is null)
                _logger.LogWarning(
                    "Channel {ChannelId} has a pending splice: its commitment_signed batch is signed by the next update or reestablish",
                    channel.ChannelId);
            else
                _commitScheduler.Schedule(channel.ChannelId);

            return null;
        }

        var messages = await SignPendingAsync(channel);
        return messages.Count == 0 ? null : (CommitmentSignedMessage)messages.Single();
    }

    /// <summary>
    /// Signs the peer's next commitment when changes are pending and no <c>commitment_signed</c> is waiting for its
    /// <c>revoke_and_ack</c> (D7), persisting it together with the sent diff (D4) before returning the messages: one
    /// <c>commitment_signed</c>, or with pending splices <c>start_batch</c> then one <c>commitment_signed</c> per active
    /// funding, the current funding first (SP-OP-03). The messages go out together, nothing between them.
    /// </summary>
    /// <returns>The messages to send in order; empty when there is nothing to sign.</returns>
    public async Task<IReadOnlyList<IChannelMessage>> SignPendingAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var commitments = channel.Commitments
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no commitment state");
        if (!commitments.CanSendCommit)
            return [];

        if (channel.DataLossDetected || !CarriesUpdates(channel.State))
        {
            _logger.LogWarning("Not signing a commitment for channel {ChannelId} in state {State} (data loss: {Lost})",
                               channel.ChannelId, Enum.GetName(channel.State), channel.DataLossDetected);
            return [];
        }

        var updates = PendingLocalUpdates(commitments).Select(u => ToWireMessage(channel, u)).ToList();
        var result = commitments.SendCommit(_commitmentSigner);
        var signed = result.Outbound.Select(o => ToWireMessage(channel, o)).ToList();

        // The whole batch is retransmitted verbatim on reestablish (D4)
        var diff = await SentCommitDiffCodec.EncodeAsync(_messageSerializer, updates.Concat(signed));

        await CommitAsync(channel, result, new ChannelStateExtras
        {
            SentCommitDiff = diff,
            LastSent = LastSentCommitmentMessage.CommitmentSigned
        });

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "Signed remote commitment {Number} of channel {ChannelId} on {Fundings} funding(s) with {Updates} update(s)",
                channel.Commitments!.RemoteNextCommit!.Commit.Number, channel.ChannelId,
                commitments.PendingFundings.Count + 1, updates.Count);

        return signed;
    }

    /// <summary>
    /// Receives a <c>start_batch</c> group of <c>commitment_signed</c> (SP-OP-05/06/07): the engine verifies every
    /// member against its funding before anything changes; the transition is saved (one
    /// <see cref="IChannelStateDbRepository.ApplyAsync"/> + save) before the signer releases the revoked secret (I3);
    /// then <b>one</b> <c>revoke_and_ack</c> is returned, followed by our own signature (a batch when splices are
    /// pending) if changes are pending for the peer.
    /// </summary>
    /// <remarks>
    /// SP-I3 (every funding's signatures on disk before the secret) holds only once the repository writes
    /// <see cref="ChannelTransition.FundingsChanged"/>, the per-funding signatures of <c>LocalCommit</c>/<c>RemoteNextCommit</c>
    /// and <see cref="ChannelTransition.RevokedRemoteCommitFundings"/>, and restores the pending fundings: that is lane
    /// SP1-C (SP1-C-T4). Until then batches are refused unless <c>option_splice</c> (experimental) was negotiated
    /// (<see cref="Managers.ChannelManager.HandleCommitmentSignedBatchAsync"/>).
    /// </remarks>
    /// <param name="channel">The channel (from <see cref="GetUpdatableChannel"/>).</param>
    /// <param name="batch">The grouped messages.</param>
    /// <returns>The replies in wire order.</returns>
    /// <exception cref="ChannelFailedException">A batch rule was broken (SP-OP-05/06, fail the channel).</exception>
    /// <exception cref="ChannelWarningException">Another receiver rule was broken (warning and close).</exception>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveCommitmentSignedBatchAsync(
        ChannelModel channel, CommitmentSignedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(batch);
        var commitments = channel.Commitments
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no commitment state");
        if (_commitmentVerifier is null)
            throw new InvalidOperationException("No commitment verifier is registered");

        CommitmentsResult result;
        try
        {
            var members = batch.Messages
                               .Select(m => new ReceivedCommitmentSigned(m.FundingTxIdTlv?.FundingTxId,
                                                                         ToReceivedSignatures(channel, m)))
                               .ToList();
            result = commitments.ReceiveCommitBatch(members, _commitmentVerifier);
        }
        catch (CommitmentViolationException e)
        {
            throw ToPeerException(e, channel.ChannelId);
        }

        var revokeAndAck = result.Outbound.OfType<OutboundRevokeAndAck>().Single();

        // Persist the new local commitment before the secret exists (B2-CS-R06, I3; per-funding rows: SP1-C, SP-I3)
        await CommitAsync(channel, result, new ChannelStateExtras { LastSent = LastSentCommitmentMessage.RevokeAndAck });
        var revokeAndAckMessage = CreateRevokeAndAck(channel, revokeAndAck);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Accepted local commitment {Number} of channel {ChannelId} on {Fundings} funding(s)",
                             channel.Commitments!.LocalCommit.Number, channel.ChannelId, batch.Messages.Count);

        IReadOnlyList<IChannelMessage> followUp;
        try
        {
            followUp = await SignPendingAsync(channel);
        }
        catch (Exception e)
        {
            // The revoke_and_ack is persisted and must still go out; the next trigger signs again
            _logger.LogError(e, "Failed to sign the next remote commitment of channel {ChannelId}", channel.ChannelId);
            followUp = [];
        }

        return [revokeAndAckMessage, .. followUp];
    }

    /// <summary>
    /// The peer's shachain as persisted (<see cref="IRemoteShachainDbRepository"/>), in a new store the caller owns.
    /// </summary>
    /// <exception cref="ArgumentException">The persisted buckets are inconsistent.</exception>
    public async Task<ISecretStorageService> LoadRemoteShachainAsync(ChannelId channelId)
    {
        var entries = await _unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(channelId);
        var shachain = _secretStorageServiceFactory.CreatePerCommitmentStorage();
        try
        {
            shachain.Load(entries);
        }
        catch
        {
            shachain.Dispose();
            throw;
        }

        return shachain;
    }

    #endregion

    #region Wire mapping

    /// <summary>
    /// Our updates that the next <c>commitment_signed</c> covers (states 10 and 35, and our unsigned fee update), as
    /// engine outbound messages: adds by id, then removals by id, then the fee update.
    /// </summary>
    public static IReadOnlyList<CommitmentOutbound> PendingLocalUpdates(ChannelCommitments commitments)
    {
        ArgumentNullException.ThrowIfNull(commitments);

        var updates = new List<CommitmentOutbound>();
        updates.AddRange(commitments.Htlcs.Values
                                    .Where(h => h.State == HtlcState.SentAddHtlc)
                                    .OrderBy(h => h.Id)
                                    .Select(h => new OutboundAddHtlc(h)));
        foreach (var htlc in commitments.Htlcs.Values
                                        .Where(h => h.State == HtlcState.SentRemoveHtlc)
                                        .OrderBy(h => h.Id))
        {
            var removal = htlc.Removal
                       ?? throw new InvalidOperationException($"HTLC {htlc.Key} is being removed without a removal");
            updates.Add(removal.Kind switch
            {
                HtlcRemovalKind.Fulfill => new OutboundFulfillHtlc(htlc.Id, removal.PaymentPreimage!.Value,
                                                                   removal.AttributionData,
                                                                   removal.FulfillmentPayload),
                HtlcRemovalKind.Fail => new OutboundFailHtlc(htlc.Id, removal.Reason, removal.AttributionData),
                _ => new OutboundFailMalformedHtlc(htlc.Id, removal.FailureCode, removal.Sha256OfOnion)
            });
        }

        var pendingFee = commitments.FeeUpdates.LastOrDefault(f => f.State == HtlcState.SentAddHtlc);
        if (pendingFee is not null)
            updates.Add(new OutboundUpdateFee(pendingFee.FeeratePerKw));

        return updates;
    }

    /// <summary>Turns an engine outbound message into the BOLT 2 wire message for <paramref name="channel"/>.</summary>
    /// <exception cref="InvalidOperationException">A <c>revoke_and_ack</c>: use <see cref="CreateRevokeAndAck"/>.</exception>
    public IChannelMessage ToWireMessage(ChannelModel channel, CommitmentOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var channelId = channel.ChannelId;
        return outbound switch
        {
            // NL-1182: the add's custom records (an interceptor's RESUME_MODIFIED) go out with every transmission
            OutboundAddHtlc { Htlc: var htlc } when htlc.PathKey is not null || !htlc.WireCustomRecords.IsEmpty =>
                new UpdateAddHtlcMessage(new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(htlc.AmountMsat), channelId,
                                                                  htlc.CltvExpiry, htlc.Id, htlc.PaymentHash,
                                                                  htlc.OnionRoutingPacket),
                                         htlc.PathKey is { } pathKey ? new BlindedPathTlv(pathKey) : null,
                                         WireCustomRecordCodec.Decode(htlc.WireCustomRecords)),
            OutboundAddHtlc { Htlc: var htlc } =>
                _messageFactory.CreateUpdateAddHtlcMessage(channelId, htlc.Id, htlc.AmountMsat, htlc.PaymentHash,
                                                           htlc.CltvExpiry, htlc.OnionRoutingPacket),
            OutboundFulfillHtlc fulfill =>
                _messageFactory.CreateUpdateFulfillHtlcMessage(channelId, fulfill.Id, fulfill.PaymentPreimage,
                                                               fulfill.AttributionData, fulfill.FulfillmentPayload),
            OutboundFailHtlc fail =>
                _messageFactory.CreateUpdateFailHtlcMessage(channelId, fail.Id, fail.Reason, fail.AttributionData),
            OutboundFailMalformedHtlc malformed =>
                _messageFactory.CreateUpdateFailMalformedHtlcMessage(channelId, malformed.Id, malformed.Sha256OfOnion,
                                                                     malformed.FailureCode),
            OutboundUpdateFee fee => _messageFactory.CreateUpdateFeeMessage(channelId, fee.FeeratePerKw),
            // A simple taproot channel's: zero signature, the MuSig2 partial signature with our signing nonce in
            // partial_signature_with_nonce, the BIP 340 HTLC signatures (bolt-simple-taproot.md §Channel Operation)
            OutboundCommitmentSigned { Signatures.PartialSignature: { } partial } signed =>
                _messageFactory.CreateCommitmentSignedMessage(channelId, partial, signed.Signatures.HtlcSignatures,
                                                              GetFundingTxId(channel, signed)),
            OutboundCommitmentSigned signed =>
                _messageFactory.CreateCommitmentSignedMessage(channelId, signed.Signatures.Signature,
                                                              signed.Signatures.HtlcSignatures,
                                                              GetFundingTxId(channel, signed)),
            OutboundStartBatch batch =>
                _messageFactory.CreateStartBatchMessage(channelId, checked((ushort)batch.BatchSize)),
            OutboundRevokeAndAck =>
                throw new InvalidOperationException("revoke_and_ack is built by CreateRevokeAndAck after the save"),
            _ => throw new InvalidOperationException($"Unknown outbound message {outbound?.GetType().Name}")
        };
    }

    /// <summary>
    /// The <c>funding_txid</c> TLV of a <c>commitment_signed</c>: the engine's (after a splice lock the engine's
    /// current funding is the new one; <see cref="ChannelModel.FundingOutput"/> is not).
    /// </summary>
    private static TxId GetFundingTxId(ChannelModel channel, OutboundCommitmentSigned signed) =>
        signed.FundingTxId
     ?? channel.Commitments?.Params.Funding?.FundingTxId
     ?? channel.FundingOutput?.TransactionId
     ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding txid");

    #endregion

    #region First snapshot

    /// <summary>
    /// The commitment state of a channel right after the opening, from what the open flow stored: balances, feerate,
    /// both commitment numbers, the peer's signature of our first commitment, and the peer's current and next
    /// per-commitment points (NL-232: the current one must be taken before <c>channel_ready</c> overwrites it).
    /// </summary>
    /// <param name="channel">The opened channel.</param>
    /// <param name="remoteCurrentPerCommitmentPoint">The peer's point of its current commitment.</param>
    /// <param name="remoteNextPerCommitmentPoint">The peer's point of its next commitment.</param>
    /// <param name="maxDustHtlcExposureMsat">Our <c>max_dust_htlc_exposure_msat</c> policy
    /// (<c>NodeOptions.MaxDustHtlcExposureMsat</c>), stored with the snapshot (NL-242, NL-254); null disables the
    /// check.</param>
    /// <param name="remoteNextNonce">Simple taproot channels (NL-877 T3): the peer's <c>channel_ready</c>
    /// <c>next_local_nonce</c>, its verification nonce for its next commitment (null when not received yet). The
    /// peer's signature of our first commitment is then <see cref="ChannelModel.LastReceivedPartialSignature"/>.</param>
    /// <exception cref="InvalidOperationException">The funding output is not known.</exception>
    /// <exception cref="ArgumentException">The balances do not add up to the funding amount.</exception>
    public static ChannelCommitments CreateInitialCommitments(ChannelModel channel,
                                                              CompactPubKey remoteCurrentPerCommitmentPoint,
                                                              CompactPubKey remoteNextPerCommitmentPoint,
                                                              ulong? maxDustHtlcExposureMsat = null,
                                                              MusigPublicNonce? remoteNextNonce = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        CommitmentSignatures? signatures;
        if (channel.ChannelParams.OptionSimpleTaproot)
            signatures = channel.LastReceivedPartialSignature is { } partial
                             ? CommitmentSignatures.Taproot(partial, [])
                             : null;
        else
            signatures = channel.LastReceivedSignature is { } signature
                             ? new CommitmentSignatures(signature, [])
                             : null;
        return ChannelCommitments.Create(channel.ChannelId,
                                         CommitmentParams.FromChannel(channel, maxDustHtlcExposureMsat),
                                         channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi,
                                         checked((uint)channel.ChannelParams.FeeRateAmountPerKw.Satoshi),
                                         remoteCurrentPerCommitmentPoint, remoteNextPerCommitmentPoint, signatures,
                                         channel.LocalCommitmentNumber, channel.RemoteCommitmentNumber,
                                         remoteNextNonce);
    }

    #endregion
}