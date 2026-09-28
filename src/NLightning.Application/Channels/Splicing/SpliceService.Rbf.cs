using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Channels.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using InteractiveTx;
using InteractiveTx.Models;
using Interfaces;

/// <summary>
/// Splice RBF (wave SPR, SPR-T1/T2; BOLT 2 "Channel Splicing" <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>): our bump of the
/// pending splice (<see cref="BumpAsync(SpliceBumpRequest, CancellationToken)"/>) and the peer's, each a new
/// interactive-tx attempt that becomes an RBF sibling of the pending splice.
/// </summary>
/// <remarks>
/// <para>Any quiescence initiator may RBF, not only the splice initiator; the sender of <c>tx_init_rbf</c> is the
/// initiator of the new interactive-tx session (it adds the shared input and the new funding output and pays the
/// common fields). The funding keys stay those of <c>splice_init</c>/<c>splice_ack</c> (<c>tx_init_rbf</c> carries
/// none), so every attempt pays the same 2-of-2 script and spends the same current funding output: the attempts
/// double-spend each other (<see cref="SpliceRules.CheckRbfDoubleSpends"/>).</para>
/// <para>Our contribution to an attempt is rebuilt from the latest attempt's stored one (<see cref="PlanRbfContribution"/>):
/// the same wallet inputs under the same reservation (a splice-in keeps its amount and pays the new fee from its
/// change), or the same splice-out output with our fee share taken from our channel balance (a negative
/// <c>funding_output_contribution</c>, NL-481); as initiator without either we still pay the common fields and the
/// shared input and output from our balance.</para>
/// <para>Fund safety: the attempt is checked against the pending ones before our <c>commitment_signed</c>
/// (<see cref="CheckRbfAttempt"/>), its funding row and commitments are saved before anything is sent (as a splice),
/// commitments on every sibling share the commitment numbers (one revocation revokes them all), and the lock of any
/// sibling discards the others in the lock's own save (<see cref="FundingSet.Lock"/>).</para>
/// </remarks>
public sealed partial class SpliceService
{
    /// <summary>The smallest splice-out output an RBF attempt keeps (any script type is above its dust limit).</summary>
    private const long MinSpliceOutOutputSatoshis = 546;

    /// <inheritdoc />
    public async Task<SpliceResult> BumpAsync(SpliceBumpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channelId = request.ChannelId;
        var quiescence = _serviceProvider.GetService<IQuiescenceService>()
                      ?? throw new InvalidOperationException("Splicing needs quiescence, which is not available");
        var driver = GetDriver();
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var unlocked))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");

        // The I/O that needs no lock: a splice-out destination for a new splice-out, the quick-confirmation estimate
        // once more than 10 RBF attempts are pending
        BitcoinScript? newSpliceOutScript = null;
        if (request.ContributionSatoshis < 0)
            newSpliceOutScript = await (_serviceProvider.GetService<ISpliceOutDestination>()
                                     ?? throw new InvalidOperationException("No splice-out destination is available"))
                                    .ResolveAsync(null, cancellationToken);
        var quickFeerate = await GetQuickConfirmationFeerateAsync(unlocked, cancellationToken);

        SpliceNegotiation negotiation;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");
            if (_negotiations.TryGetValue(channelId, out var existing))
                throw new InvalidOperationException(
                    $"[SPR-T1] A splice of channel {channelId} is already {Enum.GetName(existing.State)}");
            if (_serviceProvider.GetService<IPeerLivenessProbe>() is { } probe
             && !await probe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
                throw new InvalidOperationException($"The peer of channel {channelId} is not connected");

            var fundings = _statePort.GetFundings(channel);
            var latest = fundings.LatestAttempt
                      ?? throw new InvalidOperationException($"[SPR-T1] Channel {channelId} has no pending splice");
            var previous = await GetLatestAttemptSessionAsync(channel, fundings, cancellationToken);
            var plan = PlanRbfContribution(previous?.LocalContribution ?? InteractiveTxContribution.Empty,
                                           latest.LocalBalanceDeltaMsat / 1_000, true, request.FeeratePerKw,
                                           request.ContributionSatoshis, newSpliceOutScript)
                    ?? throw new InvalidOperationException(
                           $"[SPR-T1] Our contribution to the splice of channel {channelId} cannot be rebuilt at "
                         + $"{request.FeeratePerKw} sat/kw (the pending attempt's wallet inputs cannot pay it, or the "
                         + "requested contribution needs new wallet inputs)");

            // The rules that do not depend on the quiescence itself, now; the rest once quiescent
            var conditions = GetRbfConditions(channel, GetNegotiatedFeatures(channel.RemoteNodeId),
                                              QuiescenceState.None, fundings, null, quickFeerate);
            conditions = conditions with
            {
                Channel = conditions.Channel with { IsQuiescent = true, LocalIsQuiescenceInitiator = true }
            };
            if (SpliceRules.CheckSendRbf(conditions, request.FeeratePerKw, plan.SignedContributionSatoshis) is
                { } violation)
                throw new InvalidOperationException($"[{violation.RequirementId}] {violation.Reason}");
            if (request.MaxFeeSatoshis is { } maxFee && plan.FeeSatoshis > maxFee)
                throw new InvalidOperationException(
                    $"[SPR-T1] Our share of the bumped splice's fee, {plan.FeeSatoshis} sat, is above the limit of "
                  + $"{maxFee} sat");

            negotiation = CreateRbfNegotiation(channel, fundings, latest, previous, plan, true,
                                               null, request.FeeratePerKw, 0, false,
                                               SpliceNegotiationState.AwaitingQuiescence);
            _negotiations[channelId] = negotiation;
        }

        QuiescenceInitiator initiator;
        try
        {
            initiator = await quiescence.RequestAsync(channelId, QuiescencePurpose.SpliceRbf, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await EndBeforeNegotiationAsync(negotiation, "cancelled before the channel was quiescent");
            throw;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
        {
            return await EndBeforeNegotiationAsync(negotiation, $"no quiescence: {e.Message}");
        }

        if (initiator == QuiescenceInitiator.Remote)
            // Q-R-05: the peer's simultaneous request won; only the quiescence initiator may send tx_init_rbf
            return await EndBeforeNegotiationAsync(negotiation, "the peer is the quiescence initiator");

        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!ReferenceEquals(Get(channelId), negotiation)
             || negotiation.State != SpliceNegotiationState.AwaitingQuiescence
             || !_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                return negotiation.Result.Task.IsCompleted
                           ? await negotiation.Result.Task
                           : negotiation.ToResult("the negotiation ended before tx_init_rbf");

            var fundings = _statePort.GetFundings(channel);
            var previous = await GetLatestAttemptSessionAsync(channel, fundings, cancellationToken);
            var conditions = GetRbfConditions(channel, GetNegotiatedFeatures(channel.RemoteNodeId),
                                              quiescence.GetState(channelId), fundings, null, quickFeerate);
            string? reason = null;
            if (SpliceRules.CheckSendRbf(conditions, negotiation.Model.FeeratePerKw,
                                         negotiation.Model.LocalContributionSatoshis) is { } violation)
                reason = $"[{violation.RequirementId}] {violation.Reason}";
            else if (fundings.LatestAttempt?.FundingTxId != negotiation.Model.RbfOf)
                reason = "[SPR-T1] the pending splice changed before tx_init_rbf";
            else if (!await PrepareDriverForRbfAsync(channel, negotiation, fundings, cancellationToken))
                reason = "[SPR-T1] the signed attempts of the pending splice are not stored";

            if (reason is not null)
            {
                // Quiescent for nothing: our tx_abort ends it (SP-Q-01)
                GetPublisher()?.Publish(channel.RemoteNodeId,
                                        EndQuiescenceWithTxAbort(channelId, channel.RemoteNodeId, reason));
                return await EndBeforeNegotiationAsync(negotiation, reason);
            }

            var locktime = GetTip();
            negotiation.Model = negotiation.Model with
            {
                Locktime = locktime,
                State = SpliceNegotiationState.InitSent
            };
            negotiation.PreviousAttemptFeeSatoshis = GetFee(previous);

            IReadOnlyList<IChannelMessage> messages;
            try
            {
                messages = await driver.RequestRbfAsync(CreateTerms(negotiation, negotiation.RbfContribution!),
                                                        LightningMoney.Zero, cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                var text = $"cannot request the rbf: {e.Message}";
                GetPublisher()?.Publish(channel.RemoteNodeId,
                                        EndQuiescenceWithTxAbort(channelId, channel.RemoteNodeId, text));
                return await EndBeforeNegotiationAsync(negotiation, text);
            }

            _logger.LogInformation(
                "Sending tx_init_rbf on channel {ChannelId} for splice {TxId}: contribution {Contribution} sat at "
              + "{Feerate} sat/kw", channelId, negotiation.Model.RbfOf, negotiation.Model.LocalContributionSatoshis,
                negotiation.Model.FeeratePerKw);
            GetPublisher()?.Publish(channel.RemoteNodeId,
                                    WithContribution(messages, negotiation.Model.LocalContributionSatoshis));
        }

        return await negotiation.Result.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> on <paramref name="channelId"/> belongs to a splice: the channel
    /// is past <c>channel_ready</c> and <c>option_splice</c> is negotiated. A dual-funded open is RBF-ed before
    /// <c>channel_ready</c>, through the interactive-tx driver alone, which also answers a late one with
    /// <c>tx_abort</c> on a channel without splicing.
    /// </summary>
    public bool HandlesRbf(ChannelId channelId, FeatureOptions negotiatedFeatures) =>
        negotiatedFeatures is { OptionSplice: not FeatureSupport.No }
     && _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.State is Domain.Channels.Enums.ChannelState.Open or Domain.Channels.Enums.ChannelState.ShuttingDown
                                                                  or Domain.Channels.Enums.ChannelState.Negotiating;

    /// <summary>
    /// Under the channel's lock: the peer's <c>tx_init_rbf</c> of the pending splice (SPR-T1). The BOLT 2 splice rules
    /// (<see cref="SpliceRules.CheckReceiveRbf"/>: a warning and close for a broken MUST, <c>tx_abort</c> otherwise),
    /// then the RBF negotiation (we are the interactive-tx non-initiator, with our contribution rebuilt from the latest
    /// attempt's) and the driver's <c>tx_ack_rbf</c>, carrying our signed contribution.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> HandleTxInitRbfAsync(TxInitRbfMessage message,
                                                                           FeatureOptions negotiatedFeatures,
                                                                           CompactPubKey peerPubKey,
                                                                           IUnitOfWork unitOfWork,
                                                                           CancellationToken cancellationToken =
                                                                               default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var payload = message.Payload;
        var channelId = payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "tx_init_rbf");
        var quiescenceState = _serviceProvider.GetService<IQuiescenceService>()?.GetState(channelId)
                           ?? QuiescenceState.None;

        var existing = Get(channelId);
        if (existing is { State: SpliceNegotiationState.AwaitingQuiescence }
         && quiescenceState.Initiator == QuiescenceInitiator.Remote)
        {
            // Our request lost the quiescence (Q-R-05): the peer's RBF goes first
            await EndBeforeNegotiationAsync(existing, "the peer is the quiescence initiator");
            existing = null;
        }

        var fundings = _statePort.GetFundings(channel);
        var previous = await GetLatestAttemptSessionAsync(channel, fundings, cancellationToken, unitOfWork);
        var contribution = message.FundingOutputContributionTlv?.Satoshis;
        var conditions = GetRbfConditions(channel, negotiatedFeatures, quiescenceState, fundings, previous?.CreatedAt,
                                          await GetQuickConfirmationFeerateAsync(channel, cancellationToken));
        conditions = conditions with
        {
            Channel = conditions.Channel with { SpliceNegotiating = existing is { IsInProgress: true } }
        };
        if (SpliceRules.CheckReceiveRbf(conditions, payload, contribution) is { } violation)
            return Reject(channelId, peerPubKey, violation);

        var latest = fundings.LatestAttempt!;

        // Our side of the new attempt: the latest one's rebuilt at the new feerate as non-initiator, or nothing when
        // that cannot be paid (BOLT 2: a peer may stop contributing rather than fail the RBF)
        var plan = PlanRbfContribution(previous?.LocalContribution ?? InteractiveTxContribution.Empty,
                                       latest.LocalBalanceDeltaMsat / 1_000, false, payload.Feerate, null, null);
        if (plan is null)
        {
            _logger.LogWarning("Our contribution to the splice of channel {ChannelId} cannot be rebuilt at {Feerate} "
                             + "sat/kw; not contributing to the peer's RBF attempt", channelId, payload.Feerate);
            plan = PlanRbfContribution(InteractiveTxContribution.Empty, 0, false, payload.Feerate, null, null)!;
        }

        var negotiation = CreateRbfNegotiation(channel, fundings, latest, previous, plan, false, contribution ?? 0,
                                               payload.Feerate, payload.Locktime,
                                               message.RequireConfirmedInputsTlv is not null,
                                               SpliceNegotiationState.Negotiating);
        if (!TryPrepareSharedFunding(negotiation, out var reason))
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, reason);
        if (!await PrepareDriverForRbfAsync(channel, negotiation, fundings, cancellationToken, unitOfWork))
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, "the signed splice attempts are not stored");

        _negotiations[channelId] = negotiation;
        IReadOnlyList<IChannelMessage> messages;
        try
        {
            // IT-RBF-01's feerate floor, then OnRbfRequested with the terms prepared here, then our tx_ack_rbf
            messages = await GetDriver().ReceiveAsync(message, peerPubKey, unitOfWork, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            End(negotiation, e.Message);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, "cannot start the rbf negotiation");
        }

        if (messages.Any(m => m is TxAbortMessage))
        {
            // The driver rejected the attempt: its tx_abort ends the quiescence (SP-Q-01)
            End(negotiation, "the rbf attempt was rejected");
            _serviceProvider.GetService<IQuiescenceService>()?.Terminate(channelId, QuiescenceEndReason.TxAbort);
            return messages;
        }

        _logger.LogInformation("Accepting the RBF of splice {TxId} on channel {ChannelId} by {Peer}: its contribution "
                             + "{Contribution} sat, ours {Ours} sat at {Feerate} sat/kw", latest.FundingTxId, channelId,
                               peerPubKey, contribution ?? 0, plan.SignedContributionSatoshis, payload.Feerate);
        return WithContribution(messages, plan.SignedContributionSatoshis);
    }

    /// <summary>
    /// Under the channel's lock: the peer's <c>tx_ack_rbf</c> to our <c>tx_init_rbf</c> (SPR-T1,
    /// <see cref="SpliceRules.CheckReceiveAckRbf"/>): the new attempt starts with us as the interactive-tx initiator.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> HandleTxAckRbfAsync(TxAckRbfMessage message,
                                                                          FeatureOptions negotiatedFeatures,
                                                                          CompactPubKey peerPubKey,
                                                                          IUnitOfWork unitOfWork,
                                                                          CancellationToken cancellationToken =
                                                                              default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var channelId = message.Payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "tx_ack_rbf");
        var quiescenceState = _serviceProvider.GetService<IQuiescenceService>()?.GetState(channelId)
                           ?? QuiescenceState.None;

        var negotiation = Get(channelId);
        var rbfSent = negotiation is { State: SpliceNegotiationState.InitSent, IsInitiator: true, Model.IsRbf: true };
        var contribution = message.FundingOutputContributionTlv?.Satoshis;
        var conditions = GetRbfConditions(channel, negotiatedFeatures, quiescenceState,
                                          _statePort.GetFundings(channel), null, null);
        if (SpliceRules.CheckReceiveAckRbf(conditions, contribution, rbfSent) is { } violation)
            return Reject(channelId, peerPubKey, violation);

        negotiation!.Model = negotiation.Model with
        {
            RemoteContributionSatoshis = contribution ?? 0,
            RemoteRequiresConfirmedInputs = message.RequireConfirmedInputsTlv is not null,
            State = SpliceNegotiationState.Negotiating
        };
        var driver = GetDriver();
        if (!TryPrepareSharedFunding(negotiation, out var reason))
        {
            End(negotiation, reason);
            var abort = await driver.AbortAsync(channelId, reason, unitOfWork, cancellationToken);
            _serviceProvider.GetService<IQuiescenceService>()?.Terminate(channelId, QuiescenceEndReason.TxAbort);
            return abort.Count > 0 ? abort : [InteractiveTxDriver.CreateTxAbort(channelId, reason)];
        }

        IReadOnlyList<IChannelMessage> messages;
        try
        {
            // The attempt starts from the terms of our tx_init_rbf (our contribution) with us as initiator
            messages = await driver.ReceiveAsync(message, peerPubKey, unitOfWork, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Our own contribution broke a rule: the driver told the host, nothing of the attempt went out
            End(negotiation, e.Message);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, "cannot start the rbf negotiation");
        }

        if (messages.Any(m => m is TxAbortMessage))
        {
            End(negotiation, "the rbf attempt could not start");
            _serviceProvider.GetService<IQuiescenceService>()?.Terminate(channelId, QuiescenceEndReason.TxAbort);
            return messages;
        }

        _logger.LogInformation("RBF of splice {TxId} on channel {ChannelId} accepted by {Peer} (its contribution "
                             + "{Contribution} sat); negotiating the attempt", negotiation.Model.RbfOf, channelId,
                               peerPubKey, contribution ?? 0);
        return messages;
    }

    /// <summary>
    /// The host's answer to the peer's <c>tx_init_rbf</c> (driver callback, under the channel's lock): the terms of the
    /// RBF negotiation <see cref="HandleTxInitRbfAsync"/> prepared for exactly this message, or a rejection when none
    /// was (the message reached the driver without the splice rules).
    /// </summary>
    internal InteractiveTxRbfDecision OnRbfRequested(SpliceNegotiation negotiation, TxInitRbfMessage message)
    {
        if (!ReferenceEquals(Get(negotiation.ChannelId), negotiation)
         || negotiation is not
         {
             IsInitiator: false, State: SpliceNegotiationState.Negotiating, Model.IsRbf: true,
             RbfContribution: { } contribution
         }
         || negotiation.Model.FeeratePerKw != message.Payload.Feerate)
            return InteractiveTxRbfDecision.Reject("no splice rbf was prepared for this tx_init_rbf");

        // The contribution TLV of our tx_ack_rbf is signed (NL-481): the service writes it on the driver's message
        return InteractiveTxRbfDecision.Accept(CreateTerms(negotiation, contribution), LightningMoney.Zero);
    }

    /// <summary>
    /// SPR-T1/T2 before we sign anything for an RBF attempt: it double-spends every pending attempt (its shared input
    /// is the current funding output) and is a valid sibling of the latest one (<see cref="FundingSet.AddRbfSibling"/>:
    /// replaces it, beats its feerate, keeps the batch within 20).
    /// </summary>
    private void CheckRbfAttempt(ChannelModel channel, ConstructedInteractiveTx transaction, ChannelFunding funding)
    {
        var fundings = _statePort.GetFundings(channel);
        var shared = transaction.Inputs.FirstOrDefault(i => i.IsShared)
                  ?? throw new InvalidOperationException("[IT-RBF-01] the rbf attempt spends no shared input");
        if (SpliceRules.CheckRbfDoubleSpends(shared.PrevTxId, shared.PrevTxVout, fundings) is { } violation)
            throw new InvalidOperationException($"[{violation.RequirementId}] {violation.Reason}");

        fundings.AddRbfSibling(funding);
    }

    /// <summary>
    /// The driver runs the RBF attempt as the next attempt of the pending splice: it must know the signed attempts
    /// (the IT-RBF-01 feerate floor and double-spend check) and hand its callbacks to the new negotiation. The attempts
    /// come from their stored rows (the driver forgets them at a restart); the channel's host is pointed at
    /// <paramref name="negotiation"/>. False when no signed attempt of the pending splice is stored.
    /// </summary>
    private async Task<bool> PrepareDriverForRbfAsync(ChannelModel channel, SpliceNegotiation negotiation,
                                                      FundingSet fundings, CancellationToken cancellationToken,
                                                      IUnitOfWork? unitOfWork = null)
    {
        var signed = await LoadSignedAttemptsAsync(channel.ChannelId, fundings, unitOfWork);
        if (signed.Count == 0)
        {
            _logger.LogWarning("No signed attempt of the pending splice of channel {ChannelId} is stored; no RBF",
                               channel.ChannelId);
            return false;
        }

        if (_hosts.TryGetValue(channel.ChannelId, out var host))
        {
            host.Negotiation = negotiation;
            negotiation.Host = host;
        }
        else
        {
            host = CreateHost(negotiation);
        }

        try
        {
            await GetDriver().ResumeAsync(signed[^1], CreateTerms(negotiation, InteractiveTxContribution.Empty), host,
                                          cancellationToken, signed.Take(signed.Count - 1).ToList());
            return true;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogWarning("The interactive-tx driver cannot take an RBF of channel {ChannelId}: {Reason}",
                               channel.ChannelId, e.Message);
            return false;
        }
    }

    /// <summary>The stored, fully signed interactive-tx rows of the pending splice attempts, oldest first.</summary>
    private async Task<IReadOnlyList<InteractiveTxSessionModel>> LoadSignedAttemptsAsync(ChannelId channelId,
        FundingSet fundings, IUnitOfWork? unitOfWork)
    {
        var pending = fundings.Pending.Select(f => f.FundingTxId).ToHashSet();
        if (pending.Count == 0)
            return [];

        IReadOnlyList<InteractiveTxSessionModel> rows;
        if (unitOfWork is not null)
        {
            rows = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
        }
        else
        {
            using var scope = _serviceProvider.CreateScope();
            rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().InteractiveTxSessionDbRepository
                              .GetByChannelIdAsync(channelId);
        }

        return rows.Where(r => r is
        {
            Purpose: InteractiveTxPurpose.Splice, State: InteractiveTxSessionState.Signed,
            ConstructedTx: { } tx
        }
                            && pending.Contains(tx.TxId))
                   .OrderBy(r => r.CreatedAt)
                   .ToList();
    }

    /// <summary>The stored row of the latest pending attempt (its contribution and creation time), or null.</summary>
    private async Task<InteractiveTxSessionModel?> GetLatestAttemptSessionAsync(ChannelModel channel,
                                                                               FundingSet fundings,
                                                                               CancellationToken cancellationToken,
                                                                               IUnitOfWork? unitOfWork = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fundings.LatestAttempt is not { } latest)
            return null;

        var signed = await LoadSignedAttemptsAsync(channel.ChannelId, fundings, unitOfWork);
        return signed.LastOrDefault(r => r.ConstructedTx!.TxId == latest.FundingTxId);
    }

    /// <summary>A new negotiation for an RBF attempt of <paramref name="latest"/>: its funding keys, our planned
    /// contribution.</summary>
    private SpliceNegotiation CreateRbfNegotiation(ChannelModel channel, FundingSet fundings, ChannelFunding latest,
                                                   InteractiveTxSessionModel? previous, RbfContributionPlan plan,
                                                   bool isInitiator, long? remoteContribution, uint feeratePerKw,
                                                   uint locktime, bool remoteRequiresConfirmedInputs,
                                                   SpliceNegotiationState state)
    {
        var negotiation = CreateNegotiation(channel, fundings, isInitiator, plan.SignedContributionSatoshis,
                                            remoteContribution, feeratePerKw, locktime, latest.LocalFundingPubKey,
                                            latest.LocalFundingKeyIndex, latest.RemoteFundingPubKey,
                                            _options.RequireConfirmedInputs, remoteRequiresConfirmedInputs,
                                            plan.SpliceOutScript, null, state);
        negotiation.Model = negotiation.Model with { RbfOf = latest.FundingTxId };
        negotiation.RbfContribution = plan.Contribution;
        negotiation.PreviousAttemptFeeSatoshis = GetFee(previous);
        return negotiation;
    }

    /// <summary>The splice RBF rules' facts (<see cref="SpliceRbfConditions"/>), under the channel's lock.</summary>
    private SpliceRbfConditions GetRbfConditions(ChannelModel channel, FeatureOptions? negotiatedFeatures,
                                                 QuiescenceState quiescence, FundingSet fundings,
                                                 DateTimeOffset? lastAttemptCreatedAt, uint? quickFeerate) =>
        new(GetConditions(channel, negotiatedFeatures, quiescence, fundings),
            fundings.Pending.Count,
            fundings.LatestAttempt?.FeeratePerKw ?? 0,
            fundings.Pending.Any(f => f.SpliceLockedSent),
            fundings.Pending.Any(f => f.SpliceLockedReceived),
            negotiatedFeatures is { ZeroConf: not FeatureSupport.No },
            lastAttemptCreatedAt is { } createdAt && DateTimeOffset.UtcNow - createdAt < _options.MinRbfInterval,
            quickFeerate,
            _options.MaxRbfAttempts);

    /// <summary>
    /// The feerate our fee service deems high enough for quick confirmation (the next-block estimate), asked only when
    /// more than <see cref="SpliceRules.MaxRbfAttemptsAtAnyFeerate"/> RBF attempts are pending; null otherwise or when
    /// unknown.
    /// </summary>
    private async Task<uint?> GetQuickConfirmationFeerateAsync(ChannelModel channel,
                                                              CancellationToken cancellationToken)
    {
        try
        {
            if (_statePort.GetFundings(channel).Pending.Count - 1 <= SpliceRules.MaxRbfAttemptsAtAnyFeerate
             || _serviceProvider.GetService<IFeeService>() is not { } feeService)
                return null;

            var estimate = await feeService.GetFeeRatePerKwAsync(1, cancellationToken);
            return estimate is { Satoshi: > 0 } ? (uint)Math.Min(estimate.Satoshi, uint.MaxValue) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "No quick-confirmation feerate for channel {ChannelId}", channel.ChannelId);
            return null;
        }
    }

    /// <summary>
    /// Our contribution to an RBF attempt rebuilt from the latest attempt's (<paramref name="previous"/>, its signed
    /// value <paramref name="previousSignedSatoshis"/>) at <paramref name="feeratePerKw"/>, as the interactive-tx
    /// initiator (we pay the common fields and the shared input and output, IT-S-03) or not.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>With wallet inputs (a splice-in): the same inputs under the same reservation, the requested (or the same)
    /// amount, the new fee paid from the change (dropped below the P2WPKH dust limit); null when the inputs cannot pay
    /// it, or a negative amount is requested.</item>
    /// <item>Without wallet inputs: our non-change outputs (a splice-out) and our fee share come from our channel
    /// balance, so the contribution is -(outputs + fee) (D16, NL-481); a negative <paramref name="requestedSatoshis"/>
    /// is the new signed contribution (the output is what is left after the fee, to the previous splice-out script or
    /// <paramref name="newSpliceOutScript"/>), 0 drops our outputs; a positive one needs new wallet inputs and gives
    /// null.</item>
    /// </list>
    /// </remarks>
    internal static RbfContributionPlan? PlanRbfContribution(InteractiveTxContribution previous,
                                                             long previousSignedSatoshis, bool isInitiator,
                                                             uint feeratePerKw, long? requestedSatoshis,
                                                             BitcoinScript? newSpliceOutScript)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var nonChange = previous.Outputs.Where(o => !o.IsChange).ToList();
        var sharedWeight = isInitiator ? GetInitiatorSharedWeight() : 0;

        if (previous.Inputs.Count > 0)
        {
            var target = requestedSatoshis ?? previousSignedSatoshis;
            if (target < 0)
                return null;

            var inputTotal = previous.Inputs.Sum(i => i.Amount.Satoshi);
            var outputTotal = nonChange.Sum(o => o.Amount.Satoshi);
            var baseWeight = sharedWeight + previous.Inputs.Sum(i => (long)i.InputWeight)
                           + nonChange.Sum(o => CollaborativeFeeCalculator.OutputWeight(o.ScriptPubKey));
            if (previous.Outputs.FirstOrDefault(o => o.IsChange)?.ScriptPubKey is { } changeScript)
            {
                var fee = CollaborativeFeeCalculator
                         .FeeForWeight(baseWeight + CollaborativeFeeCalculator.OutputWeight(changeScript), feeratePerKw)
                         .Satoshi;
                var change = inputTotal - target - outputTotal - fee;
                if (change >= WalletWeights.P2WpkhDustLimitSat)
                    return new RbfContributionPlan(
                        new InteractiveTxContribution(previous.Inputs,
                                                      [
                                                          .. nonChange,
                                                          new ContributedOutput(LightningMoney.Satoshis(change),
                                                                                changeScript, true)
                                                      ], previous.ReservationId),
                        target, checked((ulong)fee), nonChange.FirstOrDefault()?.ScriptPubKey);
            }

            var leftover = inputTotal - target - outputTotal;
            if (leftover < CollaborativeFeeCalculator.FeeForWeight(baseWeight, feeratePerKw).Satoshi)
                return null;

            // Below the dust limit the change goes to the fee
            return new RbfContributionPlan(new InteractiveTxContribution(previous.Inputs, nonChange,
                                                                         previous.ReservationId),
                                           target, checked((ulong)leftover), nonChange.FirstOrDefault()?.ScriptPubKey);
        }

        switch (requestedSatoshis)
        {
            case > 0:
                return null;
            case < 0:
                {
                    var script = nonChange.FirstOrDefault()?.ScriptPubKey ?? newSpliceOutScript;
                    if (script is not { } destination)
                        return null;

                    var fee = CollaborativeFeeCalculator
                             .FeeForWeight(sharedWeight + CollaborativeFeeCalculator.OutputWeight(destination),
                                           feeratePerKw).Satoshi;
                    var amount = -requestedSatoshis.Value - fee;
                    if (amount < MinSpliceOutOutputSatoshis)
                        return null;

                    return new RbfContributionPlan(
                        new InteractiveTxContribution([],
                                                      [
                                                          new ContributedOutput(LightningMoney.Satoshis(amount),
                                                                                destination, false)
                                                      ], null),
                        requestedSatoshis.Value, checked((ulong)fee), destination);
                }
        }

        // Keep our outputs (none when 0 is requested); our part of the fee comes from our channel balance
        var outputs = requestedSatoshis == 0 ? [] : nonChange;
        var weight = sharedWeight + outputs.Sum(o => CollaborativeFeeCalculator.OutputWeight(o.ScriptPubKey));
        var ownFee = weight == 0 ? 0 : CollaborativeFeeCalculator.FeeForWeight(weight, feeratePerKw).Satoshi;
        var signed = -checked(outputs.Sum(o => o.Amount.Satoshi) + ownFee);
        return new RbfContributionPlan(outputs.Count == 0 ? InteractiveTxContribution.Empty
                                                          : new InteractiveTxContribution([], outputs, null),
                                       signed, checked((ulong)ownFee), outputs.FirstOrDefault()?.ScriptPubKey);
    }

    /// <summary>A stored attempt's total fee (inputs minus outputs), or null without one.</summary>
    private static ulong? GetFee(InteractiveTxSessionModel? attempt)
    {
        if (attempt?.ConstructedTx is not { } transaction)
            return null;

        var inputs = transaction.Inputs.Aggregate(0UL, (sum, i) => checked(sum + (ulong)i.Amount.Satoshi));
        var outputs = transaction.Outputs.Aggregate(0UL, (sum, o) => checked(sum + (ulong)o.Amount.Satoshi));
        return inputs >= outputs ? inputs - outputs : 0;
    }

    /// <summary>
    /// The driver's <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> with our signed <c>funding_output_contribution</c> (NL-481: a
    /// splice-out RBF carries a negative one; 0 omits the TLV).
    /// </summary>
    private static IReadOnlyList<IChannelMessage> WithContribution(IReadOnlyList<IChannelMessage> messages,
                                                                   long contributionSatoshis) =>
        messages.Select(m => m switch
                 {
                     TxInitRbfMessage init => new TxInitRbfMessage(
                         init.Payload, InteractiveTxDriver.CreateContributionTlv(contributionSatoshis),
                         init.RequireConfirmedInputsTlv),
                     TxAckRbfMessage ack => new TxAckRbfMessage(
                         ack.Payload, InteractiveTxDriver.CreateContributionTlv(contributionSatoshis),
                         ack.RequireConfirmedInputsTlv),
                     _ => m
                 })
                .ToList();

    /// <summary>
    /// Our contribution to an RBF attempt (<see cref="PlanRbfContribution"/>): what we add, our signed
    /// <c>funding_output_contribution</c>, our share of the fee and the splice-out destination, if any.
    /// </summary>
    internal sealed record RbfContributionPlan(
        InteractiveTxContribution Contribution,
        long SignedContributionSatoshis,
        ulong FeeSatoshis,
        BitcoinScript? SpliceOutScript);
}