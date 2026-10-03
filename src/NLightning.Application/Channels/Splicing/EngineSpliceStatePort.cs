using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Exceptions;
using Interfaces;

/// <summary>
/// The <see cref="ISpliceStatePort"/> over the several-funding commitment engine (lane SP1-B,
/// <see cref="ChannelCommitments.SignSpliceCommitment"/>, <see cref="ChannelCommitments.ReceiveSpliceCommitment"/>,
/// <see cref="ChannelCommitments.LockFunding"/>, <see cref="ChannelCommitments.DiscardPendingFundings"/>), the
/// per-funding signer (lane SP1-C, <c>RegisterFunding</c>, <c>MarkSpliceCommitmentPersisted</c>,
/// <c>LockFunding</c>) and the <c>ChannelFundings</c> rows (<see cref="IChannelFundingDbRepository"/>, migration
/// <c>AddSpliceFundings</c>).
/// </summary>
/// <remarks>
/// <para>The engine decides which fundings are active: the peer's verified splice <c>commitment_signed</c> makes the new
/// funding pending at once (SP-CS-02), before <c>tx_signatures</c>. The <c>splice_locked</c> flags and the confirmation
/// height of a pending funding, which the engine does not keep, are overlaid here from the last applied set and stored
/// on its <c>ChannelFundings</c> row.</para>
/// <para>Every member runs under the channel's lock; the <c>Stage*</c>/<c>Sign*</c>/<c>Receive*</c> members only stage
/// on the given unit of work and remember the engine result, which the matching <c>On*Saved</c>/<c>Apply*</c> member
/// applies to the channel after the save (persist, then memory, then send; SP-I7).</para>
/// </remarks>
public sealed class EngineSpliceStatePort : ISpliceStatePort
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ICommitmentSigner _commitmentSigner;
    private readonly ICommitmentVerifier _commitmentVerifier;
    private readonly ILogger<EngineSpliceStatePort> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly ILightningSigner _signer;
    private readonly ConcurrentDictionary<ChannelId, ChannelSpliceState> _states = new();

    public EngineSpliceStatePort(IChannelMemoryRepository channelMemoryRepository,
                                 ICommitmentSigner commitmentSigner, ICommitmentVerifier commitmentVerifier,
                                 ILogger<EngineSpliceStatePort> logger, IMessageFactory messageFactory,
                                 ILightningSigner signer)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentSigner = commitmentSigner;
        _commitmentVerifier = commitmentVerifier;
        _logger = logger;
        _messageFactory = messageFactory;
        _signer = signer;
    }

    /// <inheritdoc />
    public FundingSet GetFundings(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Commitments?.Fundings is not { } engine)
        {
            var current = channel.FundingOutput is { } output ? ChannelFunding.FromFundingOutput(output) : null;
            return current is null
                       ? throw new InvalidOperationException(
                             $"The funding outpoint of channel {channel.ChannelId} is unknown")
                       : FundingSet.Single(current);
        }

        if (!_states.TryGetValue(channel.ChannelId, out var state))
            return engine;

        lock (state)
            return new FundingSet(engine.Current,
                                  engine.Pending
                                        .Select(f => state.Pending.TryGetValue(f.FundingTxId, out var known)
                                                         ? known
                                                         : f)
                                        .ToList());
    }

    /// <inheritdoc />
    public async Task<CommitmentSignedMessage> SignSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                                         IUnitOfWork unitOfWork,
                                                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var commitments = GetCommitments(channel);
        var pending = funding with { Status = ChannelFundingStatus.Pending };

        // The signer signs a funding it knows (SP-OP-01); registering the same funding again is a no-op
        _signer.RegisterFunding(channel.ChannelId, pending);
        var result = commitments.SignSpliceCommitment(pending, _commitmentSigner);
        var signed = result.Outbound.OfType<OutboundCommitmentSigned>().Single();

        // The funding row and the peer's commitment on it with our signatures, in the driver's save before our
        // commitment_signed goes out (SP-I7; the retransmission and the peer's commitment on chain need them)
        var fundings = unitOfWork.ChannelFundingDbRepository;
        await fundings.UpsertAsync(channel.ChannelId, pending);
        var remote = commitments.RemoteCommit with
        {
            Spec = ChannelCommitments.SpecFor(commitments.RemoteCommit.Spec, pending)
        };
        await fundings.StageRemoteCommitmentAsync(channel.ChannelId, pending.FundingTxId, remote, signed.Signatures);

        return _messageFactory.CreateCommitmentSignedMessage(channel.ChannelId, signed.Signatures.Signature,
                                                             signed.Signatures.HtlcSignatures, pending.FundingTxId);
    }

    /// <inheritdoc />
    public async Task ReceiveSpliceCommitmentAsync(ChannelModel channel, ChannelFunding funding,
                                                   CommitmentSignedMessage message, IUnitOfWork unitOfWork,
                                                   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var commitments = GetCommitments(channel);
        var pending = funding with { Status = ChannelFundingStatus.Pending };
        _signer.RegisterFunding(channel.ChannelId, pending);

        var signatures = new CommitmentSignatures(message.Payload.Signature, message.Payload.HtlcSignatures.ToList());
        CommitmentsResult result;
        try
        {
            // SP-CS-02: our commitment at the current number, moved to the new funding; the funding becomes pending
            result = commitments.ReceiveSpliceCommitment(pending, signatures, _commitmentVerifier);
        }
        catch (Exception e) when (e is CommitmentViolationException or ArgumentException)
        {
            throw new SpliceCommitmentException(e.Message, e);
        }

        // SP-I2: our commitment on the new funding with the peer's signatures, before our shared_input_signature exists
        await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
        var fundings = unitOfWork.ChannelFundingDbRepository;
        await fundings.UpsertAsync(channel.ChannelId, pending);
        var local = new LocalCommit(commitments.LocalCommit.Number,
                                    ChannelCommitments.SpecFor(commitments.LocalCommit.Spec, pending), signatures);
        await fundings.StageLocalCommitmentAsync(channel.ChannelId, pending.FundingTxId, local);

        var state = GetState(channel.ChannelId);
        lock (state)
            state.StagedReceive = (pending.FundingTxId, result);
    }

    /// <inheritdoc />
    public void OnSpliceCommitmentSaved(ChannelModel channel, ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(funding);
        var state = GetState(channel.ChannelId);
        CommitmentsResult? result;
        lock (state)
        {
            result = state.StagedReceive is { } staged && staged.FundingTxId == funding.FundingTxId
                         ? staged.Result
                         : null;
            state.StagedReceive = null;
        }

        if (result is null)
            throw new InvalidOperationException(
                $"No splice commitment of channel {channel.ChannelId} for {funding.FundingTxId} was staged");

        channel.UpdateCommitments(result.Next);
        _channelMemoryRepository.UpdateChannel(channel);

        // SP-I1: only now may the signer release our shared_input_signature for this funding
        _signer.MarkSpliceCommitmentPersisted(channel.ChannelId, funding.FundingTxId, result.Next.LocalCommit.Number);
    }

    /// <inheritdoc />
    public FundingSet AddPending(FundingSet fundings, ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        ArgumentNullException.ThrowIfNull(funding);
        var pending = funding with { Status = ChannelFundingStatus.Pending };

        // Already pending since the peer's splice commitment_signed (SP-CS-02): keep its place, take the new record
        return fundings.Pending.Any(f => f.FundingTxId == funding.FundingTxId)
                   ? fundings with
                   {
                       Pending = fundings.Pending.Select(f => f.FundingTxId == funding.FundingTxId ? pending : f)
                                         .ToList()
                   }
                   : pending.Kind == ChannelFundingKind.SpliceRbf
                       ? fundings.AddRbfSibling(pending)
                       : fundings.AddPending(pending);
    }

    /// <inheritdoc />
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(FundingSet fundings, TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        return fundings.Lock(fundingTxId);
    }

    /// <inheritdoc />
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Discard(FundingSet fundings, TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        return fundings.Pending.FirstOrDefault(f => f.FundingTxId == fundingTxId) is { } discarded
                   ? (fundings with { Pending = fundings.Pending.Where(f => f.FundingTxId != fundingTxId).ToList() },
                      [discarded with { Status = ChannelFundingStatus.Discarded }])
                   : (fundings, []);
    }

    /// <inheritdoc />
    public async Task StageFundingsAsync(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired,
                                         IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var commitments = GetCommitments(channel);
        var engineFundings = commitments.Fundings
                          ?? throw new InvalidOperationException(
                                 $"The commitment state of channel {channel.ChannelId} has no funding data");
        var fundings = unitOfWork.ChannelFundingDbRepository;

        CommitmentsResult? result = null;
        var isLock = next.Current.FundingTxId != engineFundings.Current.FundingTxId;
        if (isLock)
        {
            // SP-LK-03: the locked funding becomes the current one. The funding rows move first so the state machine's
            // slots written by ApplyAsync are the new funding's (same save)
            result = commitments.LockFunding(next.Current.FundingTxId);
            await fundings.ApplyLockAsync(channel.ChannelId, next.Current, retired);
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
        }
        else
        {
            // A pending funding that leaves the set without a lock was discarded (tx_abort after the commitment step)
            foreach (var discarded in retired)
            {
                var current = result?.Next ?? commitments;
                if (current.PendingFundings.All(f => f.FundingTxId != discarded.FundingTxId))
                    continue;

                result = current.DiscardPendingFundings(discarded.FundingTxId);
                await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
                await fundings.UpsertAsync(channel.ChannelId,
                                           discarded with { Status = ChannelFundingStatus.Discarded });
            }

            // The splice_locked flags and the confirmation height of every pending funding
            foreach (var funding in next.Pending)
                await fundings.UpsertAsync(channel.ChannelId, funding);
        }

        var state = GetState(channel.ChannelId);
        lock (state)
            state.StagedFundings = (next, isLock, result);
    }

    /// <inheritdoc />
    public void ApplyFundings(ChannelModel channel, FundingSet next, IReadOnlyList<ChannelFunding> retired)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(retired);
        var state = GetState(channel.ChannelId);
        (FundingSet Next, bool IsLock, CommitmentsResult? Result)? staged;
        lock (state)
        {
            staged = state.StagedFundings is { } s && ReferenceEquals(s.Next, next) ? s : null;
            state.StagedFundings = null;
            state.Pending = next.Pending.ToDictionary(f => f.FundingTxId);
        }

        if (staged is not { } applied)
        {
            // Nothing was staged for this set (a completion applied twice): only the overlay changes
            _logger.LogDebug("Fundings of channel {ChannelId} applied without a staged change", channel.ChannelId);
            return;
        }

        if (applied.Result is { } result)
        {
            channel.UpdateCommitments(result.Next);
            _channelMemoryRepository.UpdateChannel(channel);
        }

        if (applied.IsLock)
        {
            // The channel model's funding output follows the lock (listchannels, the close, the failure broadcast and
            // every single-funding reader); the locked funding is the signer's current one from here on; the retired
            // ones stay known (SP-I5)
            var locked = next.Current;
            channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(locked.CapacitySatoshis),
                                                               locked.LocalFundingPubKey, locked.RemoteFundingPubKey,
                                                               locked.FundingTxId, locked.OutputIndex));
            // The rotated key's index goes with it, so the signing info the model reports is the locked funding's
            // (NL-495)
            channel.SetLocalFundingKeyIndex(locked.LocalFundingKeyIndex);
            // The channel's short channel id follows the lock too (the ChannelFundings save wrote it to the channel
            // row); the signer needs it to sign the new channel_announcement
            if (locked.ShortChannelId is { } shortChannelId)
                channel.ShortChannelId = shortChannelId;
            _channelMemoryRepository.UpdateChannel(channel);
            _signer.LockFunding(channel.ChannelId, next.Current.FundingTxId, locked.ShortChannelId);
            _logger.LogInformation("Channel {ChannelId} now runs on funding {FundingTxId} ({Capacity} sat); {Retired} "
                                 + "funding(s) retired", channel.ChannelId, next.Current.FundingTxId,
                                   next.Current.CapacitySatoshis, retired.Count);
        }
    }

    private static ChannelCommitments GetCommitments(ChannelModel channel) =>
        channel.Commitments
     ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no commitment state");

    private ChannelSpliceState GetState(ChannelId channelId) => _states.GetOrAdd(channelId, _ => new ChannelSpliceState());

    /// <summary>What this port remembers of one channel between a stage and its apply, and the pending overlay.</summary>
    private sealed class ChannelSpliceState
    {
        public Dictionary<TxId, ChannelFunding> Pending { get; set; } = [];
        public (TxId FundingTxId, CommitmentsResult Result)? StagedReceive { get; set; }
        public (FundingSet Next, bool IsLock, CommitmentsResult? Result)? StagedFundings { get; set; }
    }
}