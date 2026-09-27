using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Splicing;

using Channels.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Exceptions;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx;
using InteractiveTx.Interfaces;
using InteractiveTx.Models;
using Interfaces;
using Quiescence;

/// <summary>
/// Runs splices (BOLT 2 "Channel Splicing"; splicing plan §3.5, lane SP1-D-T2): the operator's splice-in/splice-out
/// (<see cref="StartAsync"/>), the peer's <c>splice_init</c>/<c>splice_ack</c>, the splice <c>commitment_signed</c>,
/// the completion and a minimal <c>splice_locked</c> exchange (the new funding locked at acceptable depth, D8; the SCID
/// switch and announcements are wave SP2).
/// </summary>
/// <remarks>
/// <para>Singleton. The negotiations live in memory, keyed by channel id, and change under the channel's lock (every
/// <c>Handle*</c> member and every <see cref="SpliceNegotiationHost"/> callback runs under the lock
/// <c>ChannelManager</c> or the interactive-tx driver holds). The transaction itself is built by the interactive-tx
/// driver with the splice as its host; the channel state (fundings, the splice commitment, the signer) is
/// <see cref="ISpliceStatePort"/>'s (lanes SP1-B/SP1-C).</para>
/// <para>Quiescence (SP-S-01, SP-Q-01): our splice starts only once we are the quiescence initiator; every end of the
/// negotiation ends the quiescence (the driver terminates it at <c>tx_abort</c> and at the exchange of
/// <c>tx_signatures</c>; a rejected <c>splice_init</c> or <c>splice_ack</c> is answered with <c>tx_abort</c> through
/// <see cref="IInteractiveTxDriver.AbortQuiescence"/>). The end of the quiescence is also how this service learns that
/// a negotiation it waits for is over (<see cref="QuiescenceService.QuiescenceEnded"/>: a disconnection, a peer's
/// <c>tx_abort</c> answering our <c>splice_init</c>), and when a completed splice was saved (the driver ends the
/// quiescence right after the save of the last <c>tx_signatures</c>).</para>
/// <para>D10: as acceptor we contribute 0. D5: a new funding key per splice unless <see cref="SpliceOptions.RotateFundingKey"/>
/// is off. D16: a splice-out we initiate pays its fee share from our channel balance (the contribution is the amount
/// plus the fee of the common fields, the shared input and output and the splice-out output).</para>
/// </remarks>
public sealed class SpliceService : ISpliceService, ISpliceCommitmentReceiver, IDisposable
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ConcurrentDictionary<ChannelId, SpliceNegotiationModel> _lastSigned = new();
    private readonly ILogger<SpliceService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly ConcurrentDictionary<ChannelId, SpliceNegotiation> _negotiations = new();
    private readonly NodeOptions _nodeOptions;
    private readonly SpliceOptions _options;
    private readonly QuiescenceService? _quiescenceEvents;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILightningSigner _signer;
    private readonly ISpliceStatePort _statePort;
    private readonly Lock _sync = new();

    public SpliceService(IChannelLockProvider channelLockProvider, IChannelMemoryRepository channelMemoryRepository,
                         IMessageFactory messageFactory, ILightningSigner signer, ISpliceStatePort statePort,
                         IServiceProvider serviceProvider, ILogger<SpliceService> logger,
                         IOptions<SpliceOptions>? options = null, IOptions<NodeOptions>? nodeOptions = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _messageFactory = messageFactory;
        _signer = signer;
        _statePort = statePort;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options?.Value ?? new SpliceOptions();
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();

        _quiescenceEvents = serviceProvider.GetService<QuiescenceService>();
        if (_quiescenceEvents is not null)
            _quiescenceEvents.QuiescenceEnded += OnQuiescenceEnded;

        // The depth watcher follows the chain from the first splice message of the process on (it resolves this
        // service lazily); the host also resolves it at startup for its catch-up (SpliceDepthWatcher.CatchUpAsync)
        _ = serviceProvider.GetService<SpliceDepthWatcher>();
    }

    #region ISpliceService

    /// <inheritdoc />
    public async Task<SpliceResult> StartAsync(SpliceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContributionSatoshis == 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A splice adds or removes a non-zero amount");

        var channelId = request.ChannelId;
        var quiescence = _serviceProvider.GetService<IQuiescenceService>()
                      ?? throw new InvalidOperationException("Splicing needs quiescence, which is not available");
        if (_serviceProvider.GetService<IInteractiveTxDriver>() is null)
            throw new InvalidOperationException("Splicing needs the interactive-tx driver, which is not available");

        if (!_channelMemoryRepository.TryGetChannel(channelId, out _))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");

        // Everything that needs I/O is done before the lock: the feerate, the splice-out destination
        var feeratePerKw = request.FeeratePerKw ?? await EstimateFeerateAsync(cancellationToken);
        if (feeratePerKw < _options.MinFeeratePerKw)
            throw new ArgumentOutOfRangeException(nameof(request),
                                                  $"The feerate {feeratePerKw} sat/kw is below {_options.MinFeeratePerKw} sat/kw");

        long contribution;
        BitcoinScript? spliceOutScript = null;
        LightningMoney? spliceOutAmount = null;
        if (request.ContributionSatoshis < 0)
        {
            var destination = _serviceProvider.GetService<ISpliceOutDestination>()
                            ?? throw new InvalidOperationException("No splice-out destination is available");
            spliceOutScript = await destination.ResolveAsync(request.SpliceOutAddress, cancellationToken);
            spliceOutAmount = LightningMoney.Satoshis(-request.ContributionSatoshis);
            contribution = -checked(-request.ContributionSatoshis
                                  + (long)GetSpliceOutFee(spliceOutScript.Value, feeratePerKw).Satoshi);
        }
        else
        {
            contribution = request.ContributionSatoshis;
        }

        SpliceNegotiation negotiation;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");

            if (_negotiations.TryGetValue(channelId, out var existing))
                throw new InvalidOperationException(
                    $"[SP-S-01] A splice of channel {channelId} is already {Enum.GetName(existing.State)}");

            if (_serviceProvider.GetService<IPeerLivenessProbe>() is { } probe
             && !await probe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
                throw new InvalidOperationException($"The peer of channel {channelId} is not connected");

            // The rules that do not depend on the quiescence itself, now; the rest once quiescent
            var fundings = _statePort.GetFundings(channel);
            var conditions = GetConditions(channel, GetNegotiatedFeatures(channel.RemoteNodeId), QuiescenceState.None,
                                           fundings) with
            {
                IsQuiescent = true,
                LocalIsQuiescenceInitiator = true
            };
            if (SpliceRules.CheckSendInit(conditions, contribution) is { } violation)
                throw new InvalidOperationException($"[{violation.RequirementId}] {violation.Reason}");

            var (fundingPubKey, fundingKeyIndex) = GetNewFundingKey(channel, fundings);
            negotiation = CreateNegotiation(channel, fundings, true, contribution, null, feeratePerKw, 0, fundingPubKey,
                                            fundingKeyIndex, null, _options.RequireConfirmedInputs, false,
                                            spliceOutScript, spliceOutAmount,
                                            SpliceNegotiationState.AwaitingQuiescence);
            _negotiations[channelId] = negotiation;
        }

        // A splice-in reserves its wallet inputs before asking for quiescence: a wallet that cannot fund it never
        // quiesces the channel
        if (contribution > 0)
        {
            try
            {
                negotiation.WalletContribution = await ReserveWalletContributionAsync(negotiation, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await EndBeforeNegotiationAsync(negotiation, $"the wallet cannot fund the splice-in: {e.Message}");
                throw new InvalidOperationException($"The wallet cannot fund a splice-in of {contribution} sat", e);
            }
        }

        QuiescenceInitiator initiator;
        try
        {
            initiator = await quiescence.RequestAsync(channelId, QuiescencePurpose.Splice, cancellationToken);
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
            // Q-R-05: the peer's simultaneous request won; only the quiescence initiator may send splice_init
            return await EndBeforeNegotiationAsync(negotiation, "the peer is the quiescence initiator");

        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!ReferenceEquals(Get(channelId), negotiation)
             || negotiation.State != SpliceNegotiationState.AwaitingQuiescence
             || !_channelMemoryRepository.TryGetChannel(channelId, out var channel))
                return negotiation.Result.Task.IsCompleted
                           ? await negotiation.Result.Task
                           : negotiation.ToResult("the negotiation ended before splice_init");

            var fundings = _statePort.GetFundings(channel);
            var conditions = GetConditions(channel, GetNegotiatedFeatures(channel.RemoteNodeId),
                                           quiescence.GetState(channelId), fundings);
            if (SpliceRules.CheckSendInit(conditions, contribution) is { } violation)
            {
                // Quiescent for nothing: our tx_abort ends it (SP-Q-01)
                var reason = $"[{violation.RequirementId}] {violation.Reason}";
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
            var spliceInit = _messageFactory.CreateSpliceInitMessage(channelId, contribution, feeratePerKw, locktime,
                                                                     negotiation.Model.LocalFundingPubKey,
                                                                     _options.RequireConfirmedInputs);
            _logger.LogInformation(
                "Sending splice_init on channel {ChannelId}: contribution {Contribution} sat at {Feerate} sat/kw",
                channelId, contribution, feeratePerKw);
            GetPublisher()?.Publish(channel.RemoteNodeId, [spliceInit]);
        }

        return await negotiation.Result.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> HandleSpliceInitAsync(SpliceInitMessage message,
                                                                            FeatureOptions negotiatedFeatures,
                                                                            CompactPubKey peerPubKey,
                                                                            IUnitOfWork unitOfWork,
                                                                            CancellationToken cancellationToken =
                                                                                default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channelId = payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "splice_init");
        var quiescenceState = _serviceProvider.GetService<IQuiescenceService>()?.GetState(channelId)
                           ?? QuiescenceState.None;

        var existing = Get(channelId);
        if (existing is { State: SpliceNegotiationState.AwaitingQuiescence }
         && quiescenceState.Initiator == QuiescenceInitiator.Remote)
        {
            // Our request lost the quiescence (Q-R-05): the peer's splice goes first
            await EndBeforeNegotiationAsync(existing, "the peer is the quiescence initiator");
            existing = null;
        }

        var fundings = _statePort.GetFundings(channel);
        var conditions = GetConditions(channel, negotiatedFeatures, quiescenceState, fundings) with
        {
            SpliceNegotiating = existing is { IsInProgress: true }
        };
        var feerateAcceptable = payload.FundingFeeratePerKw >= _options.MinFeeratePerKw
                             && payload.FundingFeeratePerKw <= _options.MaxFeeratePerKw;
        if (SpliceRules.CheckReceiveInit(conditions, payload, feerateAcceptable) is { } violation)
            return Reject(channelId, peerPubKey, violation);

        var (fundingPubKey, fundingKeyIndex) = GetNewFundingKey(channel, fundings);
        var negotiation = CreateNegotiation(channel, fundings, false, 0, payload.FundingContributionSatoshis,
                                            payload.FundingFeeratePerKw, payload.Locktime, fundingPubKey,
                                            fundingKeyIndex, payload.FundingPubKey, _options.RequireConfirmedInputs,
                                            message.RequireConfirmedInputsTlv is not null, null, null,
                                            SpliceNegotiationState.Negotiating);
        if (!TryPrepareSharedFunding(negotiation, out var reason))
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, reason);

        _negotiations[channelId] = negotiation;
        try
        {
            // D10: we add nothing; the initiator sends the first tx_add_input
            await GetDriver().StartAsync(CreateTerms(negotiation, InteractiveTxContribution.Empty),
                                         CreateHost(negotiation), cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            await EndBeforeNegotiationAsync(negotiation, e.Message);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, "cannot start the splice negotiation");
        }

        _logger.LogInformation("Accepting the splice of channel {ChannelId} by {Peer}: its contribution {Contribution} "
                             + "sat at {Feerate} sat/kw", channelId, peerPubKey, payload.FundingContributionSatoshis,
                               payload.FundingFeeratePerKw);
        return [_messageFactory.CreateSpliceAckMessage(channelId, 0, fundingPubKey, _options.RequireConfirmedInputs)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> HandleSpliceAckAsync(SpliceAckMessage message,
                                                                           FeatureOptions negotiatedFeatures,
                                                                           CompactPubKey peerPubKey,
                                                                           IUnitOfWork unitOfWork,
                                                                           CancellationToken cancellationToken =
                                                                               default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channelId = payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "splice_ack");
        var quiescenceState = _serviceProvider.GetService<IQuiescenceService>()?.GetState(channelId)
                           ?? QuiescenceState.None;

        var negotiation = Get(channelId);
        var initSent = negotiation is { State: SpliceNegotiationState.InitSent, IsInitiator: true };
        var conditions = GetConditions(channel, negotiatedFeatures, quiescenceState, _statePort.GetFundings(channel));
        if (SpliceRules.CheckReceiveAck(conditions, payload, initSent) is { } violation)
            return Reject(channelId, peerPubKey, violation);

        negotiation!.Model = negotiation.Model with
        {
            RemoteContributionSatoshis = payload.FundingContributionSatoshis,
            RemoteFundingPubKey = payload.FundingPubKey,
            RemoteRequiresConfirmedInputs = message.RequireConfirmedInputsTlv is not null,
            State = SpliceNegotiationState.Negotiating
        };
        if (!TryPrepareSharedFunding(negotiation, out var reason))
        {
            await EndBeforeNegotiationAsync(negotiation, reason);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, reason);
        }

        // Our contribution: the wallet inputs of a splice-in (reserved before quiescence), or the splice-out output
        // paid from our channel balance (D16); the driver owns (and releases) it from here on
        var contribution = negotiation.WalletContribution
                        ?? (negotiation is { SpliceOutAmount: { } amount, Model.SpliceOutScript: { } script }
                                ? new InteractiveTxContribution([], [new ContributedOutput(amount, script, false)], null)
                                : InteractiveTxContribution.Empty);
        negotiation.WalletContribution = null;
        try
        {
            var messages = await GetDriver().StartAsync(CreateTerms(negotiation, contribution),
                                                        CreateHost(negotiation), cancellationToken);
            _logger.LogInformation("Splice of channel {ChannelId} accepted by {Peer} (its contribution {Contribution} "
                                 + "sat); negotiating the transaction", channelId, peerPubKey,
                                   payload.FundingContributionSatoshis);
            return messages;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // The driver released our contribution and told the host (a contribution the engine refused), or never
            // took it (a negotiation in progress)
            if (contribution.ReservationId is not null && Get(channelId) == negotiation)
                await ReleaseAsync(contribution);
            await EndBeforeNegotiationAsync(negotiation, e.Message);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, "cannot start the splice negotiation");
        }
    }

    /// <inheritdoc />
    public SpliceNegotiationModel? GetNegotiation(ChannelId channelId) =>
        Get(channelId)?.Model ?? _lastSigned.GetValueOrDefault(channelId);

    #endregion

    #region ISpliceCommitmentReceiver

    /// <inheritdoc />
    public bool IsSpliceCommitmentSigned(ChannelId channelId, CommitmentSignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Get(channelId) is
        {
            State: SpliceNegotiationState.CommitmentSigned, CommitmentSignedReceived: false,
            NewFunding: { } funding
        }
            && (message.FundingTxIdTlv is null || message.FundingTxIdTlv.FundingTxId == funding.FundingTxId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> HandleSpliceCommitmentSignedAsync(
        CommitmentSignedMessage message, CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var channelId = message.Payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "commitment_signed");
        if (!IsSpliceCommitmentSigned(channelId, message))
            throw new InvalidOperationException($"No splice of channel {channelId} waits for a commitment_signed");

        var negotiation = Get(channelId)!;
        var funding = negotiation.NewFunding!;
        try
        {
            // SP-CS-02: verified against the new funding at our current commitment number; no revoke_and_ack
            await _statePort.ReceiveSpliceCommitmentAsync(channel, funding, message, unitOfWork, cancellationToken);
        }
        catch (SpliceCommitmentException e)
        {
            _logger.LogWarning("Invalid splice commitment_signed on channel {ChannelId}: {Reason}", channelId,
                               e.Message);
            return await GetDriver().AbortAsync(channelId, "invalid commitment_signed for the splice", unitOfWork,
                                                cancellationToken);
        }

        // SP-I1 / SP-I7: the peer's signatures of our commitment on the new funding are saved before any
        // shared_input_signature of ours can be made
        await unitOfWork.SaveChangesAsync();
        _statePort.OnSpliceCommitmentSaved(channel, funding);
        negotiation.CommitmentSignedReceived = true;

        // IT-SIG-01/03: our tx_signatures follow when we sign first (the shared input counts for the initiator)
        return await GetDriver().OnCommitmentSignedReceivedAsync(channelId, unitOfWork, cancellationToken);
    }

    #endregion

    #region Host callbacks (under the channel's lock)

    internal async Task<IReadOnlyList<IChannelMessage>> CreateSpliceCommitmentSignedAsync(
        SpliceNegotiation negotiation, InteractiveTxSessionModel session, IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var transaction = session.ConstructedTx
                       ?? throw new InvalidOperationException("The splice transaction is not constructed");
        if (!_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel))
            throw new InvalidOperationException($"Channel {negotiation.ChannelId} is not loaded");

        // SP-TX-01..05 on the whole transaction (the session checked most of them already; the reserve row is ours)
        var facts = GetFacts(negotiation, transaction);
        if (SpliceRules.CheckTxComplete(facts) is { } violation)
            throw new InvalidOperationException($"[{violation.RequirementId}] {violation.Reason}");

        var model = negotiation.Model;
        var funding = new ChannelFunding(transaction.TxId, checked((ushort)transaction.SharedOutputIndex!.Value),
                                         facts.FundingOutputSatoshis, model.LocalFundingPubKey,
                                         model.RemoteFundingPubKey!.Value, model.LocalFundingKeyIndex,
                                         checked(model.LocalContributionSatoshis * 1_000),
                                         checked(model.RemoteContributionSatoshis!.Value * 1_000),
                                         ChannelFundingKind.Splice, ChannelFundingStatus.Pending, model.FeeratePerKw,
                                         model.Locktime);

        // SP-CS-01: our commitment_signed for the peer's commitment on the new funding (same number, no RAA)
        var commitmentSigned = await _statePort.SignSpliceCommitmentAsync(channel, funding, unitOfWork,
                                                                          cancellationToken);
        negotiation.NewFunding = funding;
        negotiation.Model = model with
        {
            State = SpliceNegotiationState.CommitmentSigned,
            SpliceTxId = transaction.TxId
        };
        _logger.LogInformation("Splice transaction {TxId} of channel {ChannelId} constructed: {Capacity} sat",
                               transaction.TxId, negotiation.ChannelId, funding.CapacitySatoshis);
        return [commitmentSigned];
    }

    internal CompactSignature SignSharedInput(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction)
    {
        var index = GetSharedInputIndex(transaction);
        var funding = negotiation.NewFunding
                   ?? throw new InvalidOperationException("The splice commitment is not signed yet");

        // SP-I1 is the signer's: it refuses until the peer's commitment on the new funding is saved
        return _signer.SignSpliceSharedInput(negotiation.ChannelId, funding.FundingTxId,
                                             new SignedTransaction(transaction.TxId, transaction.UnsignedTx), index);
    }

    internal Witness BuildSharedInputWitness(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction,
                                             CompactSignature localSignature, CompactSignature remoteSignature)
    {
        var index = GetSharedInputIndex(transaction);
        try
        {
            // SP-SIG-01: "If shared_input_signature is not valid or non-compliant with the LOW-S-standard rule: MUST
            // send an error and fail the channel"
            _signer.ValidateSpliceSharedInputSignature(negotiation.ChannelId,
                                                       new SignedTransaction(transaction.TxId, transaction.UnsignedTx),
                                                       index, remoteSignature);
        }
        catch (Exception e) when (e is SignerException or ArgumentException or FormatException)
        {
            throw new ChannelFailedException(negotiation.ChannelId,
                                             $"[SP-SIG-01] invalid shared_input_signature for splice "
                                           + $"{transaction.TxId}: {e.Message}", "invalid shared_input_signature")
            {
                MustBroadcast = true,
                RequirementId = "SP-SIG-01"
            };
        }

        var current = negotiation.CurrentFunding;
        return SpliceFundingScripts.BuildWitness(current.LocalFundingPubKey, current.RemoteFundingPubKey,
                                                 localSignature, remoteSignature);
    }

    internal async Task<IReadOnlyList<IChannelMessage>> OnSpliceSignedAsync(SpliceNegotiation negotiation,
                                                                            InteractiveTxCompletion completion,
                                                                            IUnitOfWork unitOfWork,
                                                                            CancellationToken cancellationToken)
    {
        if (!_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel))
            throw new InvalidOperationException($"Channel {negotiation.ChannelId} is not loaded");

        var funding = negotiation.NewFunding
                   ?? throw new InvalidOperationException("The splice commitment is not signed yet");

        // The splice is a pending funding (SP-OP-01 from here on), its transaction a pending broadcast (rebroadcast
        // until it confirms, O0) and its confirmation watched for splice_locked (D8), all in the driver's save
        var fundings = _statePort.AddPending(_statePort.GetFundings(channel), funding);
        await _statePort.StageFundingsAsync(channel, fundings, [], unitOfWork, cancellationToken);
        var broadcast = new BroadcastTransactionModel(completion.SignedTransaction, BroadcastPurpose.Funding,
                                                      channel.ChannelId, GetTip(), completion.FeeratePerKw);
        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);
        var watch = new WatchedTransactionModel(channel.ChannelId, funding.FundingTxId, GetLockDepth(channel));
        unitOfWork.WatchedTransactionDbRepository.Add(watch);
        negotiation.Completion = new SpliceNegotiation.StagedCompletion(completion.Session.SessionId, fundings,
                                                                         broadcast, watch);

        // The driver saves after this returns and then ends the quiescence (SP-Q-01), which applies this to memory
        // (TxSignaturesExchanged, under the lock). Without a quiescence left to end (none registered, or it already
        // ended: a disconnection, the peer's tx_signatures after the end), it is applied once the driver's save is
        // known to have committed: never here, before the save (persist, then memory, then send; SP-I7)
        if (_quiescenceEvents is null
         || _serviceProvider.GetService<IQuiescenceService>()?.GetState(channel.ChannelId) is not
         { BlocksNewLocalUpdates: true })
            TrackBackground(ApplyCompletionAfterSaveAsync(negotiation));

        return [];
    }

    internal async Task OnSpliceAbortedAsync(SpliceNegotiation negotiation, string reason,
                                             CancellationToken cancellationToken)
    {
        if (negotiation.State is SpliceNegotiationState.Signed or SpliceNegotiationState.Aborted)
            return;

        _logger.LogInformation("Splice negotiation of channel {ChannelId} ended: {Reason}", negotiation.ChannelId,
                               reason);
        End(negotiation, reason);

        // After the commitment step the peer's commitment_signed may have made the new funding pending (SP-CS-02); a
        // tx_abort before our tx_signatures forgets the splice, so it leaves the active fundings again (no batch for it)
        if (negotiation.NewFunding is not { } funding
         || !_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel))
            return;

        var fundings = _statePort.GetFundings(channel);
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (fundings.Pending.All(f => f.FundingTxId != funding.FundingTxId))
        {
            // Only our commitment_signed went out: its save stored the funding row as Pending (SignSpliceCommitmentAsync)
            // but the engine never held it. A restart would load that row as a pending splice the peer forgot (a batch
            // the peer fails the channel over), so it is discarded too
            await DiscardStoredFundingAsync(channel, funding.FundingTxId, unitOfWork);
            return;
        }

        var (next, retired) = _statePort.Discard(fundings, funding.FundingTxId);
        await _statePort.StageFundingsAsync(channel, next, retired, unitOfWork, cancellationToken);
        await unitOfWork.SaveChangesAsync();
        _statePort.ApplyFundings(channel, next, retired);
        _logger.LogInformation("Splice funding {TxId} of channel {ChannelId} discarded after the abort",
                               funding.FundingTxId, negotiation.ChannelId);
    }

    /// <summary>
    /// Marks the stored <c>ChannelFundings</c> row of an aborted splice <c>Discarded</c> when it is still
    /// <c>Pending</c> (own save).
    /// </summary>
    private async Task DiscardStoredFundingAsync(ChannelModel channel, TxId fundingTxId, IUnitOfWork unitOfWork)
    {
        var rows = unitOfWork.ChannelFundingDbRepository;
        if ((await rows.GetByChannelIdAsync(channel.ChannelId))
               .FirstOrDefault(f => f.FundingTxId == fundingTxId) is not { Status: ChannelFundingStatus.Pending } stored)
            return;

        await rows.UpsertAsync(channel.ChannelId, stored with { Status = ChannelFundingStatus.Discarded });
        await unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Stored splice funding {TxId} of channel {ChannelId} discarded after the abort",
                               fundingTxId, channel.ChannelId);
    }

    #endregion

    #region splice_locked (minimal, SP-LK-01/02; the SCID switch and announcements are wave SP2)

    /// <summary>
    /// Under the lock: the peer's <c>splice_locked</c>. SP-LK-02: a <c>splice_txid</c> that is none of our pending
    /// splices is a <c>warning</c> and close (a retransmission for the funding we already locked is ignored). When we
    /// sent ours for the same txid, the funding is locked (SP-LK-03); otherwise the peer's is remembered.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> HandleSpliceLockedAsync(SpliceLockedMessage message,
                                                                              CompactPubKey peerPubKey,
                                                                              IUnitOfWork unitOfWork,
                                                                              CancellationToken cancellationToken =
                                                                                  default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var channelId = message.Payload.ChannelId;
        var channel = GetPeerChannel(channelId, peerPubKey, "splice_locked");
        var fundings = _statePort.GetFundings(channel);
        var txId = message.Payload.SpliceTxId;
        if (fundings.Current.FundingTxId == txId)
        {
            _logger.LogDebug("splice_locked for the current funding {TxId} of channel {ChannelId}; already locked",
                             txId, channelId);
            return [];
        }

        var funding = fundings.Pending.FirstOrDefault(f => f.FundingTxId == txId)
                   ?? throw new ChannelWarningException(
                          $"[SP-LK-02] splice_locked for {txId}, which is no pending splice of channel {channelId}",
                          channelId, "splice_locked for an unknown splice transaction")
                   {
                       CloseConnection = true
                   };
        if (funding.SpliceLockedReceived)
            return [];

        await AdvanceLockAsync(channel, fundings, funding with { SpliceLockedReceived = true }, unitOfWork,
                               cancellationToken);
        return [];
    }

    /// <summary>
    /// A pending splice transaction reached acceptable depth (D8: the channel's <c>minimum_depth</c>; the depth
    /// watcher calls it off any lock): we send <c>splice_locked</c> (SP-LK-01), after saving that we did, and lock the
    /// funding when the peer's arrived for the same txid (SP-LK-03).
    /// </summary>
    public async Task OnSpliceDepthReachedAsync(ChannelId channelId, TxId spliceTxId, uint height,
                                                uint? transactionIndex = null,
                                                CancellationToken cancellationToken = default)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            return;

        var fundings = _statePort.GetFundings(channel);
        if (fundings.Pending.FirstOrDefault(f => f.FundingTxId == spliceTxId) is not { SpliceLockedSent: false } funding)
            return;

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        // The splice's real short channel id (its block, its index in the block and the funding output), which the
        // channel takes when the funding locks (listchannels, forwarding, the new announcement)
        var shortChannelId = transactionIndex is { } txIndex
                                 ? new ShortChannelId(height, txIndex, funding.OutputIndex)
                                 : funding.ShortChannelId;
        await AdvanceLockAsync(channel, fundings,
                               funding with
                               {
                                   SpliceLockedSent = true,
                                   ConfirmedHeight = height,
                                   ShortChannelId = shortChannelId
                               },
                               unitOfWork, cancellationToken);

        _logger.LogInformation("Splice {TxId} of channel {ChannelId} reached its depth at {Height}; sending "
                             + "splice_locked", spliceTxId, channelId, height);
        GetPublisher()?.Publish(channel.RemoteNodeId, [_messageFactory.CreateSpliceLockedMessage(channelId, spliceTxId)]);
    }

    private async Task AdvanceLockAsync(ChannelModel channel, FundingSet fundings, ChannelFunding updated,
                                        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var withFlags = new FundingSet(fundings.Current,
                                       fundings.Pending
                                               .Select(f => f.FundingTxId == updated.FundingTxId ? updated : f)
                                               .ToList());
        FundingSet next;
        IReadOnlyList<ChannelFunding> retired = [];
        if (updated is { SpliceLockedSent: true, SpliceLockedReceived: true })
            (next, retired) = _statePort.Lock(withFlags, updated.FundingTxId);
        else
            next = withFlags;

        await _statePort.StageFundingsAsync(channel, next, retired, unitOfWork, cancellationToken);

        // The lock moves the channel's funding output: the new outpoint is watched for a spend from this save on (a
        // commitment on it, revoked or not, must reach the on-chain watcher), as the funding output of an open is
        // (BOLT 5 plan O0-T2); the startup backfill covers it only after a restart
        WatchedOutpointModel? fundingWatch = null;
        if (retired.Count > 0
         && await unitOfWork.WatchedOutpointDbRepository.GetAsync(next.Current.FundingTxId, next.Current.OutputIndex)
                is null)
        {
            fundingWatch = new WatchedOutpointModel(next.Current.FundingTxId, next.Current.OutputIndex,
                                                    channel.ChannelId, WatchedOutpointPurpose.FundingOutput);
            unitOfWork.WatchedOutpointDbRepository.Add(fundingWatch);
        }

        await unitOfWork.SaveChangesAsync();
        _statePort.ApplyFundings(channel, next, retired);
        if (fundingWatch is not null)
            _serviceProvider.GetService<IBlockchainMonitor>()?.TrackWatchedOutpoint(fundingWatch);

        if (retired.Count > 0)
        {
            _lastSigned.TryRemove(channel.ChannelId, out _);
            _logger.LogInformation("Splice {TxId} of channel {ChannelId} locked: {Capacity} sat", updated.FundingTxId,
                                   channel.ChannelId, updated.CapacitySatoshis);
        }
    }

    #endregion

    /// <summary>Completes when no background work of this service is running (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_quiescenceEvents is not null)
            _quiescenceEvents.QuiescenceEnded -= OnQuiescenceEnded;
    }

    #region Negotiation lifecycle

    private SpliceNegotiation? Get(ChannelId channelId) => _negotiations.GetValueOrDefault(channelId);

    /// <summary>
    /// The end of the channel's quiescence (NL-470): after the save of the last <c>tx_signatures</c> the staged
    /// completion is applied; any other end (disconnection, <c>tx_abort</c>, timeout) ends a negotiation that is not
    /// remembered yet. From our <c>commitment_signed</c> on, a negotiation is kept for the reconnection
    /// (<c>next_funding</c>, wave SP2); its waiter is told where it stopped.
    /// </summary>
    private void OnQuiescenceEnded(object? sender, QuiescenceEndedEventArgs args)
    {
        if (Get(args.ChannelId) is not { } negotiation)
            return;

        if (negotiation.Completion is not null)
        {
            // TxSignaturesExchanged is raised by the driver after its save, under the channel's lock. Any other end
            // (a disconnection, raised without the lock) may come while the driver's save is still running, or after
            // it failed: the completion is applied only under the lock, once its save is known to have committed
            if (args.Reason == QuiescenceEndReason.TxSignaturesExchanged)
                ApplyCompletion(negotiation);
            else
                TrackBackground(ApplyCompletionAfterSaveAsync(negotiation));
            return;
        }

        switch (negotiation.State)
        {
            case SpliceNegotiationState.AwaitingQuiescence or SpliceNegotiationState.InitSent
              or SpliceNegotiationState.Negotiating:
                End(negotiation, $"quiescence ended ({args.Reason})");
                if (negotiation.WalletContribution is { } contribution)
                {
                    negotiation.WalletContribution = null;
                    TrackBackground(ReleaseAsync(contribution));
                }

                break;
            case SpliceNegotiationState.CommitmentSigned when args.Reason != QuiescenceEndReason.TxSignaturesExchanged:
                _logger.LogInformation("Splice {TxId} of channel {ChannelId} stopped before tx_signatures ({Reason}); "
                                     + "it resumes at the reconnection", negotiation.Model.SpliceTxId,
                                       negotiation.ChannelId, args.Reason);
                negotiation.Result.TrySetResult(negotiation.ToResult($"stopped before tx_signatures: {args.Reason}"));
                break;
        }
    }

    /// <summary>After the save that holds the completion: memory, the watch, the broadcast, the waiter.</summary>
    private void ApplyCompletion(SpliceNegotiation negotiation)
    {
        SpliceNegotiation.StagedCompletion? completion;
        lock (_sync)
        {
            completion = negotiation.Completion;
            if (completion is null || negotiation.State == SpliceNegotiationState.Signed)
                return;

            negotiation.Model = negotiation.Model with { State = SpliceNegotiationState.Signed };
            _negotiations.TryRemove(new KeyValuePair<ChannelId, SpliceNegotiation>(negotiation.ChannelId,
                                                                                   negotiation));
            _lastSigned[negotiation.ChannelId] = negotiation.Model;
        }

        if (_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel))
            _statePort.ApplyFundings(channel, completion.Fundings, []);

        _logger.LogInformation("Splice {TxId} of channel {ChannelId} signed; broadcasting it",
                               completion.Broadcast.TransactionId, negotiation.ChannelId);
        if (_serviceProvider.GetService<IBlockchainMonitor>() is { } monitor)
        {
            monitor.TrackWatchedTransaction(completion.Watch);
            TrackBackground(PublishAsync(monitor, completion.Broadcast));
        }

        negotiation.Result.TrySetResult(negotiation.ToResult());
    }

    /// <summary>
    /// Applies a staged completion once the driver's save that holds it has committed: under the channel's lock (the
    /// driver saves under it, so its save is over), and only when the negotiation's row is stored <c>Signed</c>. A
    /// save that failed leaves the row behind: nothing is applied, tracked or published (the driver restored its
    /// negotiation; a retry stages a new completion).
    /// </summary>
    private async Task ApplyCompletionAfterSaveAsync(SpliceNegotiation negotiation)
    {
        try
        {
            await Task.Yield();
            using var channelLock = await _channelLockProvider.AcquireAsync(negotiation.ChannelId);
            if (negotiation.Completion is not { } completion || negotiation.State == SpliceNegotiationState.Signed)
                return;

            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var row = await unitOfWork.InteractiveTxSessionDbRepository.GetByIdAsync(negotiation.ChannelId,
                                                                                      completion.SessionId);
            if (row is not { State: InteractiveTxSessionState.Signed })
            {
                _logger.LogWarning("The completion of splice {TxId} of channel {ChannelId} was not saved; nothing "
                                 + "is applied", completion.Broadcast.TransactionId, negotiation.ChannelId);
                if (ReferenceEquals(negotiation.Completion, completion))
                    negotiation.Completion = null;
                return;
            }

            ApplyCompletion(negotiation);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not apply the completed splice of channel {ChannelId}", negotiation.ChannelId);
        }
    }

    private async Task PublishAsync(IBlockchainMonitor monitor, BroadcastTransactionModel broadcast)
    {
        try
        {
            await Task.Yield();
            if (!await monitor.PublishAsync(broadcast))
                _logger.LogWarning("The splice transaction {TxId} was refused; the chain monitor sends it again",
                                   broadcast.TransactionId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not publish the splice transaction {TxId}; the chain monitor sends it again",
                               broadcast.TransactionId);
        }
    }

    private void End(SpliceNegotiation negotiation, string reason)
    {
        lock (_sync)
        {
            if (negotiation.State is SpliceNegotiationState.Signed)
                return;

            negotiation.Model = negotiation.Model with { State = SpliceNegotiationState.Aborted };
            _negotiations.TryRemove(new KeyValuePair<ChannelId, SpliceNegotiation>(negotiation.ChannelId,
                                                                                   negotiation));
        }

        negotiation.Result.TrySetResult(negotiation.ToResult(reason));
    }

    /// <summary>Ends a negotiation the driver never took (our wallet reservation is ours to release).</summary>
    private async Task<SpliceResult> EndBeforeNegotiationAsync(SpliceNegotiation negotiation, string reason)
    {
        End(negotiation, reason);
        if (negotiation.WalletContribution is { } contribution)
        {
            negotiation.WalletContribution = null;
            await ReleaseAsync(contribution);
        }

        _logger.LogInformation("Splice of channel {ChannelId} not started: {Reason}", negotiation.ChannelId, reason);
        return await negotiation.Result.Task;
    }

    private async Task ReleaseAsync(InteractiveTxContribution contribution)
    {
        if (contribution.ReservationId is null
         || _serviceProvider.GetService<IInteractiveTxContributor>() is not { } contributor)
            return;

        try
        {
            await contributor.ReleaseAsync(contribution);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The reservation is persisted; the orphaned-reservation sweep frees it at the next start
            _logger.LogError(e, "Could not release wallet reservation {ReservationId}", contribution.ReservationId);
        }
    }

    private async Task<InteractiveTxContribution> ReserveWalletContributionAsync(SpliceNegotiation negotiation,
                                                                                 CancellationToken cancellationToken)
    {
        var contributor = _serviceProvider.GetService<IInteractiveTxContributor>()
                       ?? throw new InvalidOperationException("No wallet contributor is available");
        var request = new InteractiveTxContributionRequest(negotiation.ChannelId, InteractiveTxPurpose.Splice,
                                                           LightningMoney.Satoshis(
                                                               negotiation.Model.LocalContributionSatoshis), [],
                                                           negotiation.Model.FeeratePerKw,
                                                           (int)GetInitiatorSharedWeight(), true);
        return await contributor.ContributeAsync(request, cancellationToken);
    }

    #endregion

    #region Helpers

    private SpliceNegotiation CreateNegotiation(ChannelModel channel, FundingSet fundings, bool isInitiator,
                                                long localContribution, long? remoteContribution, uint feeratePerKw,
                                                uint locktime, CompactPubKey localFundingPubKey,
                                                uint localFundingKeyIndex, CompactPubKey? remoteFundingPubKey,
                                                bool localRequiresConfirmedInputs, bool remoteRequiresConfirmedInputs,
                                                BitcoinScript? spliceOutScript, LightningMoney? spliceOutAmount,
                                                SpliceNegotiationState state)
    {
        var commitments = channel.Commitments
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no commitment state");
        var model = new SpliceNegotiationModel(channel.ChannelId, isInitiator, localContribution, remoteContribution,
                                               feeratePerKw, locktime, localFundingPubKey, localFundingKeyIndex,
                                               remoteFundingPubKey, localRequiresConfirmedInputs,
                                               remoteRequiresConfirmedInputs, spliceOutScript, state, null,
                                               DateTimeOffset.UtcNow);
        return new SpliceNegotiation(model, channel.RemoteNodeId, fundings.Current)
        {
            LocalGrossMsat = commitments.LocalBalanceMsat,
            RemoteGrossMsat = commitments.RemoteBalanceMsat,
            LocalMainMsat = commitments.LocalCommit.Spec.LocalMsat,
            RemoteMainMsat = commitments.LocalCommit.Spec.RemoteMsat,
            LocalReserveSatoshis = (ulong)channel.ChannelParams.Remote.ChannelReserveAmount.Satoshi,
            RemoteReserveSatoshis = (ulong)channel.ChannelParams.Local.ChannelReserveAmount.Satoshi,
            SpliceOutAmount = spliceOutAmount
        };
    }

    /// <summary>
    /// The shared input and output (SP-TX-01, SP-TX-03) once both funding keys and contributions are known; each
    /// side's share of the input is its balance, of the output its balance plus its contribution (SP-TX-05).
    /// </summary>
    private static bool TryPrepareSharedFunding(SpliceNegotiation negotiation, out string reason)
    {
        var model = negotiation.Model;
        var current = negotiation.CurrentFunding;
        var remoteContribution = model.RemoteContributionSatoshis ?? 0;
        var capacity = SpliceRules.GetNewCapacitySatoshis(current.CapacitySatoshis, model.LocalContributionSatoshis,
                                                          remoteContribution);
        var localOut = SpliceRules.GetBalanceAfterMsat(negotiation.LocalGrossMsat, model.LocalContributionSatoshis);
        var remoteOut = SpliceRules.GetBalanceAfterMsat(negotiation.RemoteGrossMsat, remoteContribution);
        if (capacity is null || localOut is null || remoteOut is null || model.RemoteFundingPubKey is not { } remoteKey)
        {
            reason = "[SP-TX-03] the contributions leave no valid funding output";
            return false;
        }

        if (remoteKey == model.LocalFundingPubKey)
        {
            reason = "both funding keys are the same";
            return false;
        }

        var (currentScript, _) = SpliceFundingScripts.Create(current.LocalFundingPubKey, current.RemoteFundingPubKey);
        var (newScript, _) = SpliceFundingScripts.Create(model.LocalFundingPubKey, remoteKey);
        negotiation.NewFundingScript = newScript;
        negotiation.SharedFunding = new SharedFundingSpec(
            new SharedFundingInput(current.FundingTxId, current.OutputIndex,
                                   LightningMoney.Satoshis(current.CapacitySatoshis), currentScript,
                                   SpliceFundingScripts.SharedInputWeight),
            newScript, LightningMoney.Satoshis(capacity.Value), LightningMoney.MilliSatoshis(negotiation.LocalGrossMsat),
            LightningMoney.MilliSatoshis(negotiation.RemoteGrossMsat), LightningMoney.MilliSatoshis(localOut.Value),
            LightningMoney.MilliSatoshis(remoteOut.Value));
        reason = string.Empty;
        return true;
    }

    private static SpliceTxCompleteFacts GetFacts(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction)
    {
        var current = negotiation.CurrentFunding;
        var script = negotiation.NewFundingScript
                  ?? throw new InvalidOperationException("The new funding script is not known");
        var fundingOutputs = transaction.Outputs.Where(o => o.ScriptPubKey == script).ToList();
        var inputTotal = transaction.Inputs.Aggregate(0UL, (sum, i) => checked(sum + (ulong)i.Amount.Satoshi));
        var outputTotal = transaction.Outputs.Aggregate(0UL, (sum, o) => checked(sum + (ulong)o.Amount.Satoshi));
        var model = negotiation.Model;
        return new SpliceTxCompleteFacts(
            transaction.Inputs.Count(i => i.IsShared
                                       || (i.PrevTxId == current.FundingTxId && i.PrevTxVout == current.OutputIndex)),
            fundingOutputs.Count, fundingOutputs.Count == 1 ? (ulong)fundingOutputs[0].Amount.Satoshi : 0,
            current.CapacitySatoshis, model.LocalContributionSatoshis, model.RemoteContributionSatoshis ?? 0,
            negotiation.LocalMainMsat, negotiation.RemoteMainMsat, negotiation.LocalReserveSatoshis,
            negotiation.RemoteReserveSatoshis,
            transaction.Outputs.Any(o => o.AddedBy == InteractiveTxParty.Local && o.ScriptPubKey != script),
            transaction.Outputs.Any(o => o.AddedBy == InteractiveTxParty.Remote && o.ScriptPubKey != script),
            inputTotal >= outputTotal ? inputTotal - outputTotal : 0);
    }

    private InteractiveTxTerms CreateTerms(SpliceNegotiation negotiation, InteractiveTxContribution contribution)
    {
        var model = negotiation.Model;
        var nodeId = _serviceProvider.GetRequiredService<ISecureKeyManager>().GetNodePubKey();
        return new InteractiveTxTerms(negotiation.ChannelId, nodeId, negotiation.PeerPubKey, negotiation.IsInitiator,
                                      model.FeeratePerKw, model.Locktime, model.LocalRequiresConfirmedInputs,
                                      model.RemoteRequiresConfirmedInputs, null, contribution);
    }

    private SpliceNegotiationHost CreateHost(SpliceNegotiation negotiation)
    {
        var host = new SpliceNegotiationHost(this, negotiation);
        negotiation.Host = host;
        return host;
    }

    private SpliceConditions GetConditions(ChannelModel channel, FeatureOptions? negotiatedFeatures,
                                           QuiescenceState quiescence, FundingSet fundings)
    {
        var existing = Get(channel.ChannelId);
        var spec = channel.Commitments?.LocalCommit.Spec;
        return new SpliceConditions(
            negotiatedFeatures is { OptionQuiesce: not FeatureSupport.No, OptionSplice: not FeatureSupport.No },
            quiescence.IsQuiescent, quiescence.Initiator == QuiescenceInitiator.Local,
            channel is { State: ChannelState.Open, Commitments: not null }, existing is { IsInProgress: true },
            fundings.HasPending, channel.LocalShutdownScript is not null, channel.RemoteShutdownScript is not null,
            spec?.LocalMsat ?? 0, spec?.RemoteMsat ?? 0);
    }

    /// <summary>
    /// The features negotiated with the peer (its current connection's); without a peer manager (in-process
    /// harnesses) our own.
    /// </summary>
    private FeatureOptions? GetNegotiatedFeatures(CompactPubKey peerPubKey)
    {
        if (_serviceProvider.GetService<IPeerManager>() is not { } peerManager)
            return _nodeOptions.Features;

        return peerManager.GetPeer(peerPubKey) is { } peer && peer.TryGetPeerService(out var peerService)
                   ? peerService.Features
                   : null;
    }

    /// <summary>D5: a new funding key per splice (the next index after every active funding's), or the current one.</summary>
    private (CompactPubKey PubKey, uint Index) GetNewFundingKey(ChannelModel channel, FundingSet fundings)
    {
        if (!_options.RotateFundingKey)
            return (fundings.Current.LocalFundingPubKey, fundings.Current.LocalFundingKeyIndex);

        var index = checked(fundings.Active.Max(f => f.LocalFundingKeyIndex) + 1);
        return (_signer.GetFundingPubKey(channel.ChannelId, index), index);
    }

    private ChannelModel GetPeerChannel(ChannelId channelId, CompactPubKey peerPubKey, string messageName)
    {
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new ChannelWarningException($"{messageName} for channel {channelId}, which is not loaded", channelId,
                                              $"{messageName} for a channel that is not active, message ignored");

        if (channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"{messageName} for channel {channelId} from {peerPubKey}, not its peer",
                                            channelId, "unknown channel");
        return channel;
    }

    /// <summary>A peer's <c>splice_init</c> or <c>splice_ack</c> broke a rule: warning and close, or tx_abort.</summary>
    private IReadOnlyList<IChannelMessage> Reject(ChannelId channelId, CompactPubKey peerPubKey,
                                                  SpliceRuleViolation violation)
    {
        var text = $"[{violation.RequirementId}] {violation.Reason}";
        switch (violation.Action)
        {
            case SpliceRuleAction.TxAbort:
                _logger.LogInformation("Rejecting the splice of channel {ChannelId}: {Reason}", channelId, text);
                return EndQuiescenceWithTxAbort(channelId, peerPubKey, violation.Reason);
            case SpliceRuleAction.ErrorAndFail:
                throw new ChannelFailedException(channelId, text, violation.Reason)
                {
                    RequirementId = violation.RequirementId
                };
            default:
                throw new ChannelWarningException(text, channelId, violation.Reason) { CloseConnection = true };
        }
    }

    /// <summary>
    /// Our <c>tx_abort</c> that ends a quiescence without a negotiation in the driver (a rejected splice, SP-Q-01): the
    /// driver records it (its echo is not answered) and ends the quiescence.
    /// </summary>
    private IReadOnlyList<IChannelMessage> EndQuiescenceWithTxAbort(ChannelId channelId, CompactPubKey peerPubKey,
                                                                    string reason)
    {
        if (_serviceProvider.GetService<IInteractiveTxDriver>() is { } driver)
        {
            var messages = driver.AbortQuiescence(channelId, peerPubKey, reason);
            if (messages.Count > 0)
                return messages;
        }

        _serviceProvider.GetService<IQuiescenceService>()?.Terminate(channelId, QuiescenceEndReason.TxAbort);
        return [InteractiveTxDriver.CreateTxAbort(channelId, reason)];
    }

    private IInteractiveTxDriver GetDriver() =>
        _serviceProvider.GetService<IInteractiveTxDriver>()
     ?? throw new InvalidOperationException("The interactive-tx driver is not available");

    private IChannelMessagePublisher? GetPublisher() => _serviceProvider.GetService<IChannelMessagePublisher>();

    private uint GetTip() => _serviceProvider.GetService<IBlockchainMonitor>()?.LastProcessedBlockHeight ?? 0;

    /// <summary>D8: acceptable depth is the channel's <c>minimum_depth</c> (at least 1).</summary>
    private static uint GetLockDepth(ChannelModel channel) => Math.Max(1, channel.ChannelParams.MinimumDepth);

    private async Task<uint> EstimateFeerateAsync(CancellationToken cancellationToken)
    {
        if (_serviceProvider.GetService<IFeeService>() is not { } feeService)
            throw new InvalidOperationException("No feerate given and no fee estimate available");

        var estimate = await feeService.GetFeeRatePerKwAsync(cancellationToken);
        return (uint)Math.Clamp(estimate.Satoshi, _options.MinFeeratePerKw, uint.MaxValue);
    }

    /// <summary>
    /// The weight a splice initiator pays for besides its own inputs and outputs (IT-S-03, SP-TX-03): the common
    /// fields, the shared input and the new funding output (a P2WSH output, 34-byte script).
    /// </summary>
    private static long GetInitiatorSharedWeight() =>
        CollaborativeFeeCalculator.CommonFieldsWeight + SpliceFundingScripts.SharedInputWeight
      + CollaborativeFeeCalculator.OutputWeight(new BitcoinScript(new byte[34]));

    /// <summary>D16: the fee a splice-out we initiate pays from our channel balance.</summary>
    private static LightningMoney GetSpliceOutFee(BitcoinScript destination, uint feeratePerKw) =>
        CollaborativeFeeCalculator.FeeForWeight(GetInitiatorSharedWeight()
                                              + CollaborativeFeeCalculator.OutputWeight(destination), feeratePerKw);

    private static int GetSharedInputIndex(ConstructedInteractiveTx transaction)
    {
        for (var i = 0; i < transaction.Inputs.Count; i++)
        {
            if (transaction.Inputs[i].IsShared)
                return i;
        }

        throw new InvalidOperationException("The splice transaction spends no shared input");
    }

    private void TrackBackground(Task task)
    {
        _running[task] = 0;
        task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                          TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (task.IsCompleted)
            _running.TryRemove(task, out _);
    }

    #endregion
}