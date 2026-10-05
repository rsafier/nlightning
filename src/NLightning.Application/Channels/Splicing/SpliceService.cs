using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Splicing;

using Accounting;
using Channels.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Constants;
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
using Domain.Protocol.Tlv;
using Exceptions;
using Gossip.Announcements.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx;
using InteractiveTx.Interfaces;
using InteractiveTx.Models;
using Interfaces;
using Quiescence;

/// <summary>
/// Runs splices (BOLT 2 "Channel Splicing"; splicing plan §3.5, lane SP1-D-T2): the operator's splice-in/splice-out
/// (<see cref="StartAsync"/>), the peer's <c>splice_init</c>/<c>splice_ack</c>, the splice <c>commitment_signed</c>,
/// the completion and the <c>splice_locked</c> exchange (the new funding locked at acceptable depth, D8, when both
/// sides named the same txid; wave SP2 lane SP2-B: the short channel id switch with the old one retired for 72 blocks,
/// D12, and the announcement of a public channel restarted on the splice, SP-G-01).
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
/// <para>D10: as acceptor we contribute 0, unless the peer buys liquidity from us (liquidity ads, NL-850:
/// <c>SpliceService.Liquidity.cs</c>). D5: a new funding key per splice unless <see cref="SpliceOptions.RotateFundingKey"/>
/// is off. D16: a splice-out we initiate pays its fee share from our channel balance (the contribution is the amount
/// plus the fee of the common fields, the shared input and output and the splice-out output).</para>
/// </remarks>
public sealed partial class SpliceService : ISpliceService, ISpliceCommitmentReceiver, IDisposable
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ConcurrentDictionary<ChannelId, SpliceNegotiationHost> _hosts = new();
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
        if (_options.MinRbfInterval is { } minRbfInterval)
            // NL-520: the operator's wall-clock rule replaces the default block rule (Splice:MinRbfBlocks)
            _logger.LogInformation("Splice:MinRbfInterval is set ({MinRbfInterval}): a peer's splice RBF is refused "
                                 + "while the latest attempt is younger than that, instead of until "
                                 + "Splice:MinRbfBlocks ({MinRbfBlocks}) new block(s)", minRbfInterval,
                                   _options.MinRbfBlocks);

        _quiescenceEvents = serviceProvider.GetService<QuiescenceService>();
        _quiescenceEvents?.QuiescenceEnded += OnQuiescenceEnded;

        // The depth watcher follows the chain from the first splice message of the process on (it resolves this
        // service lazily); the host also resolves it at startup for its catch-up (SpliceDepthWatcher.CatchUpAsync)
        _ = serviceProvider.GetService<SpliceDepthWatcher>();
    }

    /// <summary>
    /// Liquidity ads are not sold or bought with a splice of a simple taproot channel yet (NL-971: the buyer's and the
    /// seller's checks were never exercised with the P2TR funding script and the taproot commitment weight).
    /// </summary>
    internal const string TaprootLiquidityRefusal = "liquidity ads are not supported with simple taproot channels yet";

    #region ISpliceService

    /// <inheritdoc />
    public async Task<SpliceResult> StartAsync(SpliceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContributionSatoshis == 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A splice adds or removes a non-zero amount");
        if (IsDraining())
            throw new InvalidOperationException(NodeDrain.Refusal("splice"));

        var channelId = request.ChannelId;
        var quiescence = _serviceProvider.GetService<IQuiescenceService>()
                      ?? throw new InvalidOperationException("Splicing needs quiescence, which is not available");
        if (_serviceProvider.GetService<IInteractiveTxDriver>() is null)
            throw new InvalidOperationException("Splicing needs the interactive-tx driver, which is not available");

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var unlocked))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");
        var simpleTaproot = unlocked.ChannelParams.OptionSimpleTaproot;
        if (simpleTaproot && request.Liquidity is not null)
            throw new InvalidOperationException($"Channel {channelId} is a simple taproot channel: "
                                              + TaprootLiquidityRefusal + " (NL-971)");

        // Everything that needs I/O is done before the lock: the feerate, the splice-out destination
        var feeratePerKw = request.FeeratePerKw ?? await EstimateFeerateAsync(cancellationToken);
        if (feeratePerKw < _options.MinFeeratePerKw)
            throw new ArgumentOutOfRangeException(nameof(request),
                                                  $"The feerate {feeratePerKw} sat/kw is below {_options.MinFeeratePerKw} sat/kw");

        // Liquidity ads (NL-850): the inbound liquidity we buy with this splice, at the seller's rate
        var purchase = request.Liquidity is { } liquidity
                           ? CreatePurchaseRequest(unlocked.RemoteNodeId, liquidity, feeratePerKw)
                           : null;

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
                                  + (long)GetSpliceOutFee(spliceOutScript.Value, feeratePerKw, simpleTaproot).Satoshi);
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

            // Before the link check: a channel that is not Open yet has no link, which is not a disconnection (NL-568)
            if (channel.State != ChannelState.Open)
                throw new InvalidOperationException(
                    $"Channel {channelId} is {Enum.GetName(channel.State)}, not Open; it can be spliced once it is open");

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
            if (purchase is not null
             && CheckBuyerCanPay(channel, fundings, purchase, contribution, feeratePerKw) is { } unaffordable)
                throw new InvalidOperationException($"[{unaffordable.RequirementId}] {unaffordable.Reason}");

            var (fundingPubKey, fundingKeyIndex) = GetNewFundingKey(channel, fundings);
            negotiation = CreateNegotiation(channel, fundings, true, contribution, null, feeratePerKw, 0, fundingPubKey,
                                            fundingKeyIndex, null, _options.RequireConfirmedInputs, false,
                                            spliceOutScript, spliceOutAmount,
                                            SpliceNegotiationState.AwaitingQuiescence);
            negotiation.Liquidity = purchase;
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
            if ((SpliceRules.CheckSendInit(conditions, contribution)
              ?? (negotiation.Liquidity is { } buying
                      ? CheckBuyerCanPay(channel, fundings, buying, contribution, feeratePerKw)
                      : null)) is { } violation)
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
                                                                     _options.RequireConfirmedInputs,
                                                                     negotiation.Liquidity?.Request);
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

        // NL-591: a node draining for its shutdown starts no splice (BOLT 2: MAY send tx_abort for any reason)
        if (IsDraining())
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, NodeDrain.Refusal("splice_init"));

        var fundings = _statePort.GetFundings(channel);
        var conditions = GetConditions(channel, negotiatedFeatures, quiescenceState, fundings) with
        {
            SpliceNegotiating = existing is { IsInProgress: true }
        };
        var feerateAcceptable = payload.FundingFeeratePerKw >= _options.MinFeeratePerKw
                             && payload.FundingFeeratePerKw <= _options.MaxFeeratePerKw;
        if (SpliceRules.CheckReceiveInit(conditions, payload, feerateAcceptable) is { } violation)
            return Reject(channelId, peerPubKey, violation);

        // D10: we add nothing, unless the peer buys liquidity from us (NL-850): then we contribute exactly the
        // requested amount from our wallet and sign our rate over the new funding script
        SpliceLiquidity? sale = null;
        var contribution = InteractiveTxContribution.Empty;
        if (message.RequestFundingTlv?.Request is { } request)
        {
            if (TryStartSpliceSale(channel, peerPubKey, request, payload.FundingFeeratePerKw,
                                   payload.FundingContributionSatoshis, fundings, out sale) is { } refusal)
            {
                _logger.LogInformation("Refusing the liquidity request of {Peer} on channel {ChannelId}: {Reason}",
                                       peerPubKey, channelId, refusal);
                return EndQuiescenceWithTxAbort(channelId, peerPubKey, $"liquidity ads: {refusal}");
            }

            try
            {
                contribution = await ReserveSaleContributionAsync(channelId, request.RequestedSat,
                                                                  payload.FundingFeeratePerKw, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                sale!.EndSale();
                _logger.LogWarning("The wallet cannot fund the {Amount} sat of liquidity {Peer} asked for on channel "
                                 + "{ChannelId}: {Reason}", request.RequestedSat, peerPubKey, channelId, e.Message);
                return EndQuiescenceWithTxAbort(channelId, peerPubKey,
                                                "liquidity ads: the seller cannot fund the requested amount");
            }
        }

        var localContribution = sale is null ? 0 : checked((long)sale.Request.RequestedSat);
        var (fundingPubKey, fundingKeyIndex) = GetNewFundingKey(channel, fundings);
        var negotiation = CreateNegotiation(channel, fundings, false, localContribution,
                                            payload.FundingContributionSatoshis, payload.FundingFeeratePerKw,
                                            payload.Locktime, fundingPubKey, fundingKeyIndex, payload.FundingPubKey,
                                            _options.RequireConfirmedInputs,
                                            message.RequireConfirmedInputsTlv is not null, null, null,
                                            SpliceNegotiationState.Negotiating);
        negotiation.Liquidity = sale;
        if (!TryPrepareSharedFunding(negotiation, out var reason))
        {
            sale?.EndSale();
            await ReleaseAsync(contribution);
            return EndQuiescenceWithTxAbort(channelId, peerPubKey, reason);
        }

        SignSale(negotiation);
        _negotiations[channelId] = negotiation;
        try
        {
            // The initiator sends the first tx_add_input
            await GetDriver().StartAsync(CreateTerms(negotiation, contribution), CreateHost(negotiation),
                                         cancellationToken);
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

        _logger.LogInformation("Accepting the splice of channel {ChannelId} by {Peer}: its contribution {Contribution} "
                             + "sat, ours {Ours} sat at {Feerate} sat/kw", channelId, peerPubKey,
                               payload.FundingContributionSatoshis, localContribution, payload.FundingFeeratePerKw);
        return
        [
            _messageFactory.CreateSpliceAckMessage(channelId, localContribution, fundingPubKey,
                                                   _options.RequireConfirmedInputs, sale?.WillFund)
        ];
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
        // Liquidity ads (NL-850): the seller's answer to our request is checked over the new funding script
        var prepared = TryPrepareSharedFunding(negotiation, out var reason);
        if (prepared && ValidateSellerAnswer(negotiation, message.ProvideFundingTlv?.WillFund,
                                             payload.FundingContributionSatoshis) is { } refusal)
        {
            prepared = false;
            reason = refusal;
        }

        if (!prepared)
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

    /// <summary>
    /// Under the channel's lock, on <c>channel_reestablish</c>: the channel's splice negotiation rebuilt after a restart
    /// from its stored rows (the latest constructed, not aborted, not fully signed <c>InteractiveTxSessions</c> row of
    /// purpose <see cref="InteractiveTxPurpose.Splice"/> and the <c>ChannelFundings</c> row our
    /// <c>commitment_signed</c>'s save wrote), and resumed in the interactive-tx driver, so the peer's retransmitted
    /// splice <c>commitment_signed</c> and <c>tx_signatures</c> complete it instead of a <c>tx_abort</c> (BOLT 2
    /// <c>next_funding</c>; wave sp2 integration). Nothing when a negotiation is in memory or none is stored.
    /// </summary>
    public async Task EnsureLoadedAsync(ChannelModel channel, IUnitOfWork unitOfWork,
                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var channelId = channel.ChannelId;
        if (Get(channelId) is not null || channel.Commitments is null)
            return;

        var sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
        var stored = sessions.Where(m => m.Purpose == InteractiveTxPurpose.Splice
                                      && m.State != InteractiveTxSessionState.Aborted && m.ConstructedTx is not null)
                             .OrderBy(m => m.CreatedAt)
                             .ToList();
        if (stored.Count == 0 || stored[^1] is not { State: not InteractiveTxSessionState.Signed } latest)
            return;

        var txId = latest.ConstructedTx!.TxId;
        var funding = (await unitOfWork.ChannelFundingDbRepository.GetByChannelIdAsync(channelId))
           .FirstOrDefault(f => f.FundingTxId == txId && f.Status == ChannelFundingStatus.Pending);
        if (funding is null)
        {
            _logger.LogInformation("Splice {TxId} of channel {ChannelId} has no pending funding row; not resumed",
                                   txId, channelId);
            return;
        }

        // The deltas include a liquidity purchase's fee (NL-850): its row, saved with our commitment_signed like the
        // funding row, takes it back out of the contributions
        var purchase = await GetPurchaseAsync(channelId, txId, unitOfWork);
        var (localContribution, remoteContribution) = GetContributions(funding, purchase);
        var fundings = _statePort.GetFundings(channel);
        var negotiation = CreateNegotiation(channel, fundings, latest.IsInitiator, localContribution,
                                            remoteContribution, latest.FeeratePerKw, latest.Locktime,
                                            funding.LocalFundingPubKey, funding.LocalFundingKeyIndex,
                                            funding.RemoteFundingPubKey, false, false, null, null,
                                            SpliceNegotiationState.CommitmentSigned);
        // An RBF attempt stays one (its funding row names the attempt it replaces, wave SPR)
        negotiation.Model = negotiation.Model with { SpliceTxId = txId, RbfOf = funding.RbfOf };
        negotiation.Liquidity = purchase is null ? null : SpliceLiquidity.FromPurchase(purchase);
        if (!TryPrepareSharedFunding(negotiation, out var reason))
        {
            _logger.LogWarning("Cannot resume splice {TxId} of channel {ChannelId}: {Reason}", txId, channelId, reason);
            return;
        }

        negotiation.NewFunding = funding;
        negotiation.CommitmentSignedReceived = latest.CommitmentSignedReceived
                                            || fundings.Pending.Any(f => f.FundingTxId == txId);
        var host = CreateHost(negotiation);
        var driver = GetDriver();
        var resumed = false;
        if (driver.GetInfo(channelId) is null)
        {
            await driver.ResumeAsync(latest, CreateTerms(negotiation, latest.LocalContribution), host,
                                     cancellationToken, stored.Where(m => m.SessionId != latest.SessionId).ToList());
            resumed = true;
        }

        _negotiations[channelId] = negotiation;

        // NL-698: the peer's commitment_signed is saved in the engine (SP-I2) one save before the session row records
        // it (the driver's step, which also signs when we sign first). A crash between the two left the row saying it
        // never came: that step runs now, so the session takes the peer's tx_signatures and, when we sign first, our
        // tx_signatures exist for the next_funding retransmission (the channel_reestablish plan sends them)
        if (resumed && negotiation.CommitmentSignedReceived && !latest.CommitmentSignedReceived)
        {
            _logger.LogInformation("Splice {TxId} of channel {ChannelId}: the peer's commitment_signed was saved but "
                                 + "its negotiation step was not; completing it", txId, channelId);
            await driver.OnCommitmentSignedReceivedAsync(channelId, unitOfWork, cancellationToken);
        }

        _logger.LogInformation("Resumed splice {TxId} of channel {ChannelId} (peer's commitment_signed {Received})",
                               txId, channelId, negotiation.CommitmentSignedReceived ? "received" : "awaited");
    }

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
            await _statePort.ReceiveSpliceCommitmentAsync(channel, funding, message, unitOfWork, cancellationToken,
                                                          negotiation.RemoteNextCommitNonce);
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

        // SP-TX-01..05 on the whole transaction (the session checked most of them already; the reserve row is ours,
        // and for an RBF attempt the fee of the attempt it replaces)
        // A liquidity purchase's fee (NL-850) moves from the buyer's balance to the seller's on the new funding: it is in
        // the balance deltas (the shared funding's shares stay the contributions), and the buyer keeps its reserve
        var liquidityFeeMsat = negotiation.Liquidity?.FeeMsat ?? 0;
        var facts = GetFacts(negotiation, transaction) with
        {
            PreviousAttemptFeeSatoshis = negotiation.PreviousAttemptFeeSatoshis,
            LiquidityFeeMsat = liquidityFeeMsat
        };
        if (SpliceRules.CheckTxComplete(facts) is { } violation)
            throw new InvalidOperationException($"[{violation.RequirementId}] {violation.Reason}");

        var model = negotiation.Model;
        var funding = new ChannelFunding(transaction.TxId, checked((ushort)transaction.SharedOutputIndex!.Value),
                                         facts.FundingOutputSatoshis, model.LocalFundingPubKey,
                                         model.RemoteFundingPubKey!.Value, model.LocalFundingKeyIndex,
                                         checked(model.LocalContributionSatoshis * 1_000 - liquidityFeeMsat),
                                         checked(model.RemoteContributionSatoshis!.Value * 1_000 + liquidityFeeMsat),
                                         model.IsRbf ? ChannelFundingKind.SpliceRbf : ChannelFundingKind.Splice,
                                         ChannelFundingStatus.Pending, model.FeeratePerKw, model.Locktime,
                                         model.RbfOf);

        // SPR-T1/T2: an RBF attempt spends the current funding output like every pending attempt (so they
        // double-spend each other, IT-RBF-01) and is a valid sibling of the latest one (feerate, batch of 20), checked
        // before anything is signed for it
        if (model.IsRbf)
            CheckRbfAttempt(channel, transaction, funding);

        // SP-CS-01: our commitment_signed for the peer's commitment on the new funding (same number, no RAA)
        var commitmentSigned = await _statePort.SignSpliceCommitmentAsync(channel, funding, unitOfWork,
                                                                          cancellationToken,
                                                                          negotiation.RemoteCurrentCommitNonce);
        negotiation.NewFunding = funding;

        // The attempt's purchase row rides in this save (NL-850), like the funding row
        await StagePurchaseAsync(negotiation, funding.FundingTxId, unitOfWork);

        // SP2-C (splicing plan §3.6): the new funding output is watched from this save on (our tx_signatures follow)
        if (await Onchain.SpliceFundingWatch.StageAsync(unitOfWork, channel.ChannelId, funding) is { } fundingWatch)
            TrackBackground(Onchain.SpliceFundingWatch.TrackAfterSaveAsync(_serviceProvider, _channelLockProvider,
                                                                           fundingWatch, _logger));
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

    #region Simple taproot splices (NL-965, BOLTs PR #1324)

    /// <summary>
    /// Our <c>commit_nonces</c> for a <c>tx_complete</c> of a taproot splice: our verification nonces of our current
    /// local commitment and the next one on the transaction negotiated so far, <paramref name="fundingTxId"/>, with the
    /// splice's rotated funding key (Eclair 0.14.3 <c>InteractiveTxBuilder</c>: <c>localCommitIndex</c> and
    /// <c>localCommitIndex + 1</c>). Null for any other channel.
    /// </summary>
    internal CommitNoncesTlv? GetLocalCommitNonces(SpliceNegotiation negotiation, TxId fundingTxId)
    {
        if (!negotiation.IsSimpleTaproot
         || !_channelMemoryRepository.TryGetChannel(negotiation.ChannelId, out var channel)
         || channel.Commitments is not { } commitments)
            return null;

        var number = commitments.LocalCommit.Number;
        var keyIndex = negotiation.Model.LocalFundingKeyIndex;
        return new CommitNoncesTlv(_signer.GetLocalVerificationNonce(channel.ChannelId, keyIndex, fundingTxId, number),
                                   _signer.GetLocalVerificationNonce(channel.ChannelId, keyIndex, fundingTxId,
                                                                     number + 1));
    }

    /// <summary>
    /// Our <c>funding_nonce</c> of a taproot splice attempt: created once (the signer keeps its secret half) and sent in
    /// every <c>tx_complete</c> of the attempt. Null for any other channel.
    /// </summary>
    internal FundingNonceTlv? GetLocalFundingNonce(SpliceNegotiation negotiation)
    {
        if (!negotiation.IsSimpleTaproot)
            return null;

        negotiation.LocalFundingNonce ??= _signer.CreateSpliceFundingNonce(negotiation.ChannelId);
        return new FundingNonceTlv(negotiation.LocalFundingNonce.Value);
    }

    /// <summary>
    /// The peer's <c>commit_nonces</c> of the constructed taproot splice: required (Eclair's
    /// <c>MissingCommitNonce</c>), parsed as two points each, kept for our splice <c>commitment_signed</c> (the current
    /// one) and the new funding's next commitment (the next one).
    /// </summary>
    internal string? AcceptRemoteCommitNonces(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction,
                                              CommitNoncesTlv? remoteNonces)
    {
        if (!negotiation.IsSimpleTaproot)
            return null;

        if (remoteNonces is null)
            return $"MissingCommitNonce: tx_complete without commit_nonces for splice {transaction.TxId}";
        if (!IsValidNonce(remoteNonces.CommitNonce) || !IsValidNonce(remoteNonces.NextCommitNonce))
            return $"InvalidCommitNonce: commit_nonces for splice {transaction.TxId} are not two points each";

        negotiation.RemoteCurrentCommitNonce = remoteNonces.CommitNonce;
        negotiation.RemoteNextCommitNonce = remoteNonces.NextCommitNonce;
        return null;
    }

    /// <summary>
    /// The peer's <c>funding_nonce</c> of the constructed taproot splice: required (BOLTs PR #1324, Eclair's
    /// <c>MissingFundingNonce</c>), kept for the shared input's signature.
    /// </summary>
    internal string? AcceptRemoteFundingNonce(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction,
                                              FundingNonceTlv? remoteNonce)
    {
        if (!negotiation.IsSimpleTaproot)
            return null;

        if (remoteNonce is null)
            return $"MissingFundingNonce: tx_complete without funding_nonce for splice {transaction.TxId}";
        if (!IsValidNonce(remoteNonce.Nonce))
            return $"InvalidFundingNonce: funding_nonce for splice {transaction.TxId} is not two points";

        negotiation.RemoteFundingNonce = remoteNonce.Nonce;
        return null;
    }

    /// <summary>
    /// The commitment step of a taproot splice (after our splice <c>commitment_signed</c> was made, which registered the
    /// new funding with the signer): our MuSig2 partial signature of the shared input with the attempt's
    /// <c>funding_nonce</c>, stored with the session row before our <c>commitment_signed</c> goes out (D-T4).
    /// </summary>
    internal MusigPartialSignatureWithNonce? SignSharedInputPartial(SpliceNegotiation negotiation,
                                                                    ConstructedInteractiveTx transaction)
    {
        if (!negotiation.IsSimpleTaproot)
            return null;

        var funding = negotiation.NewFunding
                   ?? throw new InvalidOperationException("The splice commitment is not signed yet");
        var localNonce = negotiation.LocalFundingNonce
                      ?? throw new InvalidOperationException("No funding_nonce of ours was sent for this splice");
        var remoteNonce = negotiation.RemoteFundingNonce
                       ?? throw new InvalidOperationException("The peer sent no funding_nonce for this splice");
        return _signer.SignSpliceSharedInputPartial(negotiation.ChannelId, funding.FundingTxId,
                                                    new SignedTransaction(transaction.TxId, transaction.UnsignedTx),
                                                    GetSharedInputIndex(transaction), GetSpentOutputs(transaction),
                                                    localNonce, remoteNonce);
    }

    /// <summary>
    /// The key-path witness of a taproot splice's shared input from both partial signatures; the peer's is checked
    /// first (SP-SIG-01, BOLTs PR #1324: "If shared_input_partial_signature is not a valid partial signature ...: MUST
    /// send an error and fail the channel").
    /// </summary>
    internal Witness BuildSharedInputWitness(SpliceNegotiation negotiation, ConstructedInteractiveTx transaction,
                                             MusigPartialSignatureWithNonce localSignature,
                                             MusigPartialSignatureWithNonce remoteSignature)
    {
        try
        {
            var signature = _signer.AggregateSpliceSharedInputSignature(
                negotiation.ChannelId, new SignedTransaction(transaction.TxId, transaction.UnsignedTx),
                GetSharedInputIndex(transaction), GetSpentOutputs(transaction), localSignature, remoteSignature);
            return SpliceFundingScripts.BuildTaprootKeyPathWitness(signature);
        }
        catch (Exception e) when (e is SignerException or ArgumentException or FormatException)
        {
            throw new ChannelFailedException(negotiation.ChannelId,
                                             $"[SP-SIG-01] invalid shared_input_partial_signature for splice "
                                           + $"{transaction.TxId}: {e.Message}",
                                             "invalid shared_input_partial_signature")
            {
                MustBroadcast = true,
                RequirementId = "SP-SIG-01"
            };
        }
    }

    /// <summary>
    /// Under the channel's lock, on the peer's <c>channel_reestablish</c> of a simple taproot channel (after
    /// <see cref="EnsureLoadedAsync"/>): the peer's nonces of a splice in negotiation, which a restart forgot and a
    /// reconnection may have renewed (BOLTs PR #1324): its <c>current_commit_nonce</c> (type 24, sent while it misses our
    /// splice <c>commitment_signed</c>), which our retransmission is signed against, and its <c>next_local_nonces</c>
    /// entry for the splice, which the engine takes with the peer's splice <c>commitment_signed</c>. A nonce that does
    /// not parse is ignored (logged): the retransmission then waits for a valid one (NL-969).
    /// </summary>
    public void ReceiveReestablishNonces(ChannelModel channel, ChannelReestablishMessage message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (!channel.ChannelParams.OptionSimpleTaproot
         || Get(channel.ChannelId) is not { State: SpliceNegotiationState.CommitmentSigned, NewFunding: { } funding }
                negotiation)
            return;

        if (message.CurrentCommitNonceTlv?.Nonce is { } current)
        {
            if (IsValidNonce(current))
                negotiation.RemoteCurrentCommitNonce = current;
            else
                _logger.LogWarning("current_commit_nonce of channel {ChannelId} does not parse; ignored",
                                   channel.ChannelId);
        }

        if (message.NextLocalNoncesTlv?.Nonces.Entries.FirstOrDefault(e => e.FundingTxId == funding.FundingTxId) is
            { Nonce: var next } && IsValidNonce(next))
            negotiation.RemoteNextCommitNonce = next;
    }

    /// <summary>
    /// Our splice <c>commitment_signed</c> again for a simple taproot channel (the peer's <c>next_funding</c> asked for
    /// it): never replayed, signed again with a fresh signing nonce against the peer's <c>current_commit_nonce</c>
    /// (BOLTs PR #1324: "MUST use the current_commit_nonce provided"), the new signatures saved before it is returned.
    /// Empty (logged) when no splice of <paramref name="spliceTxId"/> is in negotiation or the peer gave no nonce.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ResignSpliceCommitmentAsync(ChannelModel channel,
                                                                                  TxId spliceTxId,
                                                                                  IUnitOfWork unitOfWork,
                                                                                  CancellationToken cancellationToken =
                                                                                      default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (Get(channel.ChannelId) is not { NewFunding: { } funding } negotiation
         || funding.FundingTxId != spliceTxId)
        {
            _logger.LogWarning("Our commitment_signed for splice {TxId} of channel {ChannelId} is due again but the "
                             + "splice is not in negotiation", spliceTxId, channel.ChannelId);
            return [];
        }

        if (negotiation.RemoteCurrentCommitNonce is not { } nonce)
        {
            _logger.LogWarning("Our commitment_signed for splice {TxId} of channel {ChannelId} is due again but the "
                             + "peer sent no current_commit_nonce", spliceTxId, channel.ChannelId);
            return [];
        }

        var commitmentSigned = await _statePort.SignSpliceCommitmentAsync(channel, funding, unitOfWork,
                                                                          cancellationToken, nonce);
        await unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Signed our commitment on splice {TxId} of simple taproot channel {ChannelId} again for "
                             + "its retransmission", spliceTxId, channel.ChannelId);
        return [commitmentSigned];
    }

    /// <summary>Every output the splice transaction spends, from its inputs (the shared input's is the current
    /// funding output).</summary>
    private static IReadOnlyList<SpentOutput> GetSpentOutputs(ConstructedInteractiveTx transaction) =>
        transaction.Inputs.Select(i => new SpentOutput(i.PrevTxId, i.PrevTxVout, i.Amount, i.ScriptPubKey)).ToList();

    /// <summary>A peer's public nonce parses as two compressed points (bolt-simple-taproot.md).</summary>
    private bool IsValidNonce(MusigPublicNonce nonce) =>
        _serviceProvider.GetService<IMusig2Service>() is not { } musig2
     || Taproot.TaprootChannelNonces.IsValidPublicNonce(musig2, nonce);

    #endregion

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
        // The row carries the splice transaction's whole fee (every input's value is known: the shared input is the
        // current capacity, NL-604); our share of it is the accounting feed's, at the lock. Its purpose is Splice
        // (NL-626; rows saved before it are Funding, and every rule treats both the same)
        var broadcast = new BroadcastTransactionModel(completion.SignedTransaction, BroadcastPurpose.Splice,
                                                      channel.ChannelId, GetTip(), completion.FeeratePerKw,
                                                      negotiation.Model.RbfOf,
                                                      fee: LightningMoney.Satoshis(
                                                          GetTotalFee(completion.Transaction)));
        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);

        // SPR-T1: the attempt it bumps stays pending too (the row names it in ReplacesTransactionId), so the chain
        // monitor sends every attempt again each round and the mempool keeps whichever it accepts (a conflict refusal
        // is temporary, never abandoned). Marking it Replaced here would stop sending it while the replacement may
        // never be accepted (a bitcoind whose incrementalrelayfee is above the BOLT 2 +25 sat/kw floor, an input of
        // the peer's double-spent) and could leave no attempt broadcast once the old one left the mempool. The lock of
        // any attempt abandons the others (SP-LK-03); whichever confirms is locked
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
        await StageAbandonedPurchaseAsync(channel.ChannelId, funding.FundingTxId, unitOfWork);
        await unitOfWork.SaveChangesAsync();
        _statePort.ApplyFundings(channel, next, retired);
        _logger.LogInformation("Splice funding {TxId} of channel {ChannelId} discarded after the abort",
                               funding.FundingTxId, negotiation.ChannelId);
    }

    /// <summary>
    /// Marks the stored <c>ChannelFundings</c> row of an aborted splice <c>Discarded</c> when it is still
    /// <c>Pending</c>, and its liquidity purchase replaced (NL-870), in one save of their own.
    /// </summary>
    private async Task DiscardStoredFundingAsync(ChannelModel channel, TxId fundingTxId, IUnitOfWork unitOfWork)
    {
        var rows = unitOfWork.ChannelFundingDbRepository;
        var discarded = false;
        if ((await rows.GetByChannelIdAsync(channel.ChannelId))
               .FirstOrDefault(f => f.FundingTxId == fundingTxId) is { Status: ChannelFundingStatus.Pending } stored)
        {
            await rows.UpsertAsync(channel.ChannelId, stored with { Status = ChannelFundingStatus.Discarded });
            discarded = true;
        }

        if (!await StageAbandonedPurchaseAsync(channel.ChannelId, fundingTxId, unitOfWork) && !discarded)
            return;

        await unitOfWork.SaveChangesAsync();
        if (discarded)
            _logger.LogInformation("Stored splice funding {TxId} of channel {ChannelId} discarded after the abort",
                                   fundingTxId, channel.ChannelId);
    }

    #endregion

    #region splice_locked (SP2-B-T1: lock rules; SP2-B-T2: short channel id switch; SP2-B-T3: announcement)

    /// <summary>
    /// Under the lock: the peer's <c>splice_locked</c> (BOLT 2 "Splice Completion"). SP-LK-02: a <c>splice_txid</c> that
    /// is none of our pending splices is a <c>warning</c> and close (a retransmission for the funding we already locked
    /// is ignored, as is a duplicate for a pending one). When we sent ours for the same txid, the funding is locked
    /// (SP-LK-03: its RBF siblings and ancestors discarded, the short channel id switched and, for a public channel, our
    /// <c>announcement_signatures</c> for the splice returned once it has the announcement depth); when ours named another
    /// RBF candidate (the nodes are on different forks) the message is only remembered and nothing is failed (D11).
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
        var txId = message.Payload.SpliceTxId;
        var fundings = _statePort.GetFundings(channel);
        if (fundings.Current.FundingTxId == txId)
        {
            _logger.LogDebug("splice_locked for the current funding {TxId} of channel {ChannelId}; already locked",
                             txId, channelId);
            return [];
        }

        if (fundings.Pending.All(f => f.FundingTxId != txId))
            throw new ChannelWarningException(
                $"[SP-LK-02] splice_locked for {txId}, which is no pending splice of channel {channelId}",
                channelId, "splice_locked for an unknown splice transaction")
            {
                CloseConnection = true
            };

        // Taproot gossip (BOLTs PR #1059, NL-1131): the peer's announcement nonces for the splice, kept for its
        // re-announcement once it locks
        _serviceProvider.GetService<IChannelAnnouncement2Service>()
                       ?.OnSpliceLockedNonces(channel, peerPubKey, txId, message.AnnouncementNodeNonceTlv,
                                              message.AnnouncementBitcoinNonceTlv);

        return await ReceiveSpliceLockedAsync(channel, fundings, txId, unitOfWork, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> HandlePeerFundingLockedAsync(ChannelModel channel,
        TxId fundingTxId, IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // SP-RE-04: only a pending splice whose splice_locked we have not received is processed; anything else
        // (the current funding, an unknown txid) is no splice_locked retransmission
        FundingSet fundings;
        try
        {
            fundings = _statePort.GetFundings(channel);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        if (fundings.Pending.FirstOrDefault(f => f.FundingTxId == fundingTxId) is not { SpliceLockedReceived: false })
            return [];

        _logger.LogInformation("my_current_funding_locked of channel {ChannelId} names pending splice {TxId}: "
                             + "processed as its splice_locked", channel.ChannelId, fundingTxId);
        return await ReceiveSpliceLockedAsync(channel, fundings, fundingTxId, unitOfWork, cancellationToken);
    }

    /// <summary>
    /// A pending splice transaction reached acceptable depth (D8: the channel's <c>minimum_depth</c>; the depth
    /// watcher calls it off any lock): we send <c>splice_locked</c> (SP-LK-01), after saving that we did, and lock the
    /// funding when the peer's arrived for the same txid (SP-LK-03). Idempotent: a splice we already sent ours for is
    /// left alone.
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

        // Taproot gossip (BOLTs PR #1059, NL-1131): a public taproot channel's splice_locked carries our announcement
        // nonces for the splice (made before the lock below, which may sign with them at once)
        var nonces = _serviceProvider.GetService<IChannelAnnouncement2Service>()
                                    ?.CreateSpliceLockedNonces(channel, channel.RemoteNodeId,
                                                               funding with { ShortChannelId = shortChannelId });
        var followUps = await AdvanceLockAsync(channel, fundings,
                                               funding with
                                               {
                                                   SpliceLockedSent = true,
                                                   ConfirmedHeight = height,
                                                   ShortChannelId = shortChannelId
                                               },
                                               unitOfWork, cancellationToken);

        _logger.LogInformation("Splice {TxId} of channel {ChannelId} reached its depth at {Height}; sending "
                             + "splice_locked", spliceTxId, channelId, height);
        GetPublisher()?.Publish(channel.RemoteNodeId,
                                [
                                    _messageFactory.CreateSpliceLockedMessage(channelId, spliceTxId, nonces?.Node,
                                                                              nonces?.Bitcoin),
                                    .. followUps
                                ]);
    }

    /// <summary>
    /// SP2-C-T4, before the lock (called by the depth watcher on a block disconnect and at its startup catch-up): the
    /// pending splice's confirming block was disconnected, so the splice is unconfirmed again (the chain monitor's
    /// rollback cleared its watch). The depth state the lock waits on is reset: our <c>splice_locked</c> was premature
    /// and is sent again at the new depth, with the short channel id of the splice's new block; the peer's
    /// <c>splice_locked</c> (received or not) stays remembered, so either side's depth still completes the lock
    /// (SP-LK-03). The splice transaction is still valid and rebroadcast. Idempotent.
    /// </summary>
    public async Task OnSpliceReorgedOutAsync(ChannelId channelId, TxId spliceTxId, uint forkHeight,
                                              CancellationToken cancellationToken = default)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            return;

        var fundings = _statePort.GetFundings(channel);
        if (fundings.Pending.FirstOrDefault(f => f.FundingTxId == spliceTxId) is not { } funding
         || funding.ConfirmedHeight is null)
            return;

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _logger.LogWarning("The splice {TxId} of channel {ChannelId} left the active chain (reorg at or below height "
                         + "{Height}) before it locked; it is unconfirmed again and waits for its depth",
                           spliceTxId, channelId, forkHeight);
        var next = fundings with
        {
            Pending = fundings.Pending
                              .Select(f => f.FundingTxId == spliceTxId
                                               ? f with
                                               {
                                                   SpliceLockedSent = false,
                                                   ConfirmedHeight = null,
                                                   ShortChannelId = null
                                               }
                                               : f)
                              .ToList()
        };
        await _statePort.StageFundingsAsync(channel, next, [], unitOfWork, cancellationToken);
        await unitOfWork.SaveChangesAsync();
        _statePort.ApplyFundings(channel, next, []);
    }

    /// <summary>The peer's <c>splice_locked</c> (or <c>my_current_funding_locked</c>) for the pending funding
    /// <paramref name="txId"/>: remembered, and the funding locked when ours named it too.</summary>
    private async Task<IReadOnlyList<IChannelMessage>> ReceiveSpliceLockedAsync(ChannelModel channel,
                                                                                FundingSet fundings, TxId txId,
                                                                                IUnitOfWork unitOfWork,
                                                                                CancellationToken cancellationToken)
    {
        var funding = fundings.Pending.First(f => f.FundingTxId == txId);
        if (funding.SpliceLockedReceived)
        {
            _logger.LogDebug("Duplicate splice_locked for {TxId} of channel {ChannelId}; ignored", txId,
                             channel.ChannelId);
            return [];
        }

        // SP-LK-03 / D11: ours named another RBF candidate (different forks): remember theirs and wait for the chain
        if (fundings.Pending.FirstOrDefault(f => f.SpliceLockedSent) is { } ours && ours.FundingTxId != txId)
            _logger.LogWarning("splice_locked of channel {ChannelId} names {TheirTxId} while ours named {OurTxId} "
                             + "(different forks); waiting for the chain to settle", channel.ChannelId, txId,
                               ours.FundingTxId);

        return await AdvanceLockAsync(channel, fundings, funding with { SpliceLockedReceived = true }, unitOfWork,
                                      cancellationToken);
    }

    /// <summary>
    /// Stages and saves the pending funding's new flags; when <paramref name="updated"/> is now locked both ways, the
    /// lock itself (SP-LK-03) and what follows it (<see cref="AfterLockAsync"/>). Returns the messages due after the
    /// caller's own (our <c>announcement_signatures</c> for the splice).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AdvanceLockAsync(ChannelModel channel, FundingSet fundings,
                                                                        ChannelFunding updated, IUnitOfWork unitOfWork,
                                                                        CancellationToken cancellationToken)
    {
        var withFlags = new FundingSet(fundings.Current,
                                       fundings.Pending
                                               .Select(f => f.FundingTxId == updated.FundingTxId ? updated : f)
                                               .ToList());
        FundingSet next;
        IReadOnlyList<ChannelFunding> retired = [];
        var previousShortChannelId = channel.ShortChannelId;
        if (updated is { SpliceLockedSent: true, SpliceLockedReceived: true })
        {
            // The replaced funding keeps its short channel id in its row, which rebuilds the retired map (D12)
            if (withFlags.Current.ShortChannelId is null && IsSet(previousShortChannelId))
                withFlags = withFlags with
                {
                    Current = withFlags.Current with { ShortChannelId = previousShortChannelId }
                };
            (next, retired) = _statePort.Lock(withFlags, updated.FundingTxId);
        }
        else
        {
            next = withFlags;
        }

        await _statePort.StageFundingsAsync(channel, next, retired, unitOfWork, cancellationToken);

        // The accounting feed's SpliceLocked (NL-602) rides in the lock's save; a lock happens once per funding (the
        // funding is current afterwards, never pending again). Its delta is relative to the funding it replaces
        // A liquidity purchase made with the splice (NL-850): its row becomes active (the siblings' replaced) and its fee
        // is booked in the same save, the SpliceLocked event leaving the fee out of the balance change
        if (retired.Count > 0)
        {
            var occurredAt = (_serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            var liquidityFeeMsat = await StageLiquidityAtLockAsync(unitOfWork, channel, updated, retired, occurredAt);
            await ChannelAccountingEvents.StageSpliceLockedAsync(unitOfWork, channel, updated, fundings.Current,
                                                                 occurredAt, _logger, liquidityFeeMsat);
        }

        // SP-LK-03 with RBF siblings (NL-489): the lock discards the other attempts of the splice in the same save, and
        // their transactions, which double-spend the locked one, are no longer rebroadcast
        foreach (var discarded in retired.Where(f => f.Status == ChannelFundingStatus.Discarded))
        {
            if (unitOfWork.BroadcastTransactionDbRepository is { } broadcasts)
                await broadcasts.MarkAbandonedAsync(discarded.FundingTxId);
        }

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

        // SP-G-01: the replaced funding's announcement_signatures sign a void announcement, so both halves are
        // forgotten in the lock's own save: a crash between two saves would otherwise leave the new short channel id
        // beside the old halves, and the channel would read as announced after a restart
        var announcementReset = retired.Count > 0 ? await StageAnnouncementResetAsync(channel, unitOfWork) : null;
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch
        {
            // Nothing was saved: the shared model keeps the halves the database still holds
            announcementReset?.Restore(channel);
            throw;
        }

        _statePort.ApplyFundings(channel, next, retired);
        if (fundingWatch is not null)
            _serviceProvider.GetService<IBlockchainMonitor>()?.TrackWatchedOutpoint(fundingWatch);

        if (retired.Count == 0)
            return [];

        _lastSigned.TryRemove(channel.ChannelId, out _);
        // No pending attempt is left to RBF: the next splice gets a host of its own (wave SPR review)
        _hosts.TryRemove(channel.ChannelId, out _);
        _logger.LogInformation("Splice {TxId} of channel {ChannelId} locked: {Capacity} sat", updated.FundingTxId,
                               channel.ChannelId, updated.CapacitySatoshis);
        return await AfterLockAsync(channel, previousShortChannelId, next.Current, unitOfWork);
    }

    /// <summary>
    /// After the lock's save, under the channel's lock: the short channel id switch (D12: the replaced one keeps
    /// resolving in the switch for <see cref="RetiredShortChannelId.RetentionBlocks"/> blocks; our <c>channel_update</c>
    /// follows the channel's new short channel id through the channel update service) and, for a public channel, the
    /// announcement of the splice (SP-G-01, BOLT 7): the announcement of the replaced funding is void, so both halves of
    /// <c>announcement_signatures</c> were forgotten in the lock's save, a half of the peer's that waited for our
    /// <c>splice_locked</c> is taken now, and ours is returned when the splice already has the announcement depth
    /// (otherwise the block-driven announcement round of the channel manager sends it at that depth).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AfterLockAsync(ChannelModel channel,
                                                                      ShortChannelId previousShortChannelId,
                                                                      ChannelFunding locked, IUnitOfWork unitOfWork)
    {
        // BOLT 2 option_scid_alias: an alias-only channel never accepts HTLCs by its real short channel id, so the
        // replaced real one is not retired into the map (NL-348)
        if (IsSet(previousShortChannelId) && previousShortChannelId != channel.ShortChannelId
                                          && channel.ChannelParams.UseScidAlias != FeatureSupport.Compulsory)
        {
            var retiredAt = locked.ConfirmedHeight ?? locked.ShortChannelId?.BlockHeight ?? GetTip();
            _serviceProvider.GetService<IRetiredScidMap>()
                           ?.Retire(RetiredScidMap.Create(previousShortChannelId, channel.ChannelId, retiredAt));
        }

        if (!channel.AnnounceChannel)
            return [];

        // Taproot gossip (BOLTs PR #1059, NL-1131): the old channel_announcement_2 is void; the splice's session (its
        // nonces exchanged in splice_locked) signs now when the splice already has the announcement depth, otherwise
        // the block-driven announcement round does
        if (_serviceProvider.GetService<IChannelAnnouncement2Service>() is { } announcements2
         && announcements2.IsV2Channel(channel))
        {
            try
            {
                _serviceProvider.GetService<IChannelAnnouncementService>()?.OnShortChannelIdChanged(channel.ChannelId);
                _channelMemoryRepository.UpdateChannel(channel);
                return announcements2.OnSpliceLocked(channel, channel.RemoteNodeId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not start the channel_announcement_2 of the splice of channel {ChannelId}",
                                 channel.ChannelId);
                return [];
            }
        }

        if (_serviceProvider.GetService<IChannelAnnouncementService>() is not { } announcements)
            return [];

        try
        {
            // The halves were forgotten in the lock's save (StageAnnouncementResetAsync)
            announcements.OnShortChannelIdChanged(channel.ChannelId);
            _channelMemoryRepository.UpdateChannel(channel);

            var replies = new List<IChannelMessage>();
            if (await announcements.ProcessDeferredRemoteAnnouncementSignaturesAsync(channel, channel.RemoteNodeId,
                                                                                    unitOfWork) is { } reply)
                replies.Add(reply);
            else if (await announcements.PrepareOwnAnnouncementSignaturesAsync(channel, channel.RemoteNodeId,
                                                                              unitOfWork) is { } own)
                replies.Add(own);
            await announcements.CompleteAnnouncementAsync(channel, unitOfWork);
            return replies;
        }
        catch (Exception e)
        {
            // The lock is saved; the announcement round of the next block or reconnection retries
            _logger.LogError(e, "Could not start the announcement of the splice of channel {ChannelId}",
                             channel.ChannelId);
            return [];
        }
    }

    private static bool IsSet(ShortChannelId shortChannelId) => ((byte[]?)shortChannelId) is not null;

    /// <summary>
    /// For a public channel with announcement state, forgets both halves on the shared model and stages the channel row
    /// in the lock's unit of work (the row's funding columns and short channel id stay the lock's, which
    /// <c>ChannelDbRepository.UpdateAsync</c> leaves alone once a splice lock is staged). Returns what to put back when
    /// the save fails, or null when there was nothing to forget.
    /// </summary>
    private static async Task<AnnouncementState?> StageAnnouncementResetAsync(ChannelModel channel,
                                                                             IUnitOfWork unitOfWork)
    {
        if (!channel.AnnounceChannel
         || channel is { RemoteAnnouncementSignatures: null, LocalAnnouncementSignaturesSentAt: null })
            return null;

        var previous = new AnnouncementState(channel.RemoteAnnouncementSignatures,
                                             channel.LocalAnnouncementSignaturesSentAt);
        channel.ResetAnnouncementSignatures();
        try
        {
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        }
        catch
        {
            previous.Restore(channel);
            throw;
        }

        return previous;
    }

    /// <summary>The announcement halves a failed lock save puts back on the shared model.</summary>
    private sealed record AnnouncementState(ChannelAnnouncementSignatures? Remote, DateTimeOffset? SentAt)
    {
        public void Restore(ChannelModel channel)
        {
            if (Remote is not null)
                channel.SetRemoteAnnouncementSignatures(Remote);
            if (SentAt is { } sentAt)
                channel.MarkAnnouncementSignaturesSent(sentAt);
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
        _quiescenceEvents?.QuiescenceEnded -= OnQuiescenceEnded;
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

        negotiation.Liquidity?.EndSale();
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

        // An ended RBF attempt gives the channel's host back to the negotiation it served before
        RestoreHost(negotiation);
        negotiation.Liquidity?.EndSale();
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
                                                           (int)GetInitiatorSharedWeight(negotiation.IsSimpleTaproot),
                                                           true);
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
            SpliceOutAmount = spliceOutAmount,
            IsSimpleTaproot = channel.ChannelParams.OptionSimpleTaproot
        };
    }

    /// <summary>
    /// The shared input and output (SP-TX-01, SP-TX-03) once both funding keys and contributions are known; each
    /// side's share of the input is its balance, of the output its balance plus its contribution (SP-TX-05).
    /// </summary>
    private bool TryPrepareSharedFunding(SpliceNegotiation negotiation, out string reason)
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

        // A simple taproot channel's fundings are MuSig2 P2TR outputs, its shared input a key-path spend (NL-965)
        var taproot = negotiation.IsSimpleTaproot;
        var musig2 = taproot ? _serviceProvider.GetService<IMusig2Service>() : null;
        var currentScript = SpliceFundingScripts.CreateScriptPubKey(current.LocalFundingPubKey,
                                                                    current.RemoteFundingPubKey, taproot, musig2);
        var newScript = SpliceFundingScripts.CreateScriptPubKey(model.LocalFundingPubKey, remoteKey, taproot, musig2);
        negotiation.NewFundingScript = newScript;
        negotiation.SharedFunding = new SharedFundingSpec(
            new SharedFundingInput(current.FundingTxId, current.OutputIndex,
                                   LightningMoney.Satoshis(current.CapacitySatoshis), currentScript,
                                   SpliceFundingScripts.GetSharedInputWeight(taproot), taproot),
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
            GetTotalFee(transaction));
    }

    private InteractiveTxTerms CreateTerms(SpliceNegotiation negotiation, InteractiveTxContribution contribution)
    {
        var model = negotiation.Model;
        var nodeId = _serviceProvider.GetRequiredService<ISecureKeyManager>().GetNodePubKey();
        return new InteractiveTxTerms(negotiation.ChannelId, nodeId, negotiation.PeerPubKey, negotiation.IsInitiator,
                                      model.FeeratePerKw, model.Locktime, GetDustLimitSatoshis(negotiation.ChannelId),
                                      model.LocalRequiresConfirmedInputs, model.RemoteRequiresConfirmedInputs, null,
                                      contribution);
    }

    /// <summary>
    /// The channel's negotiated dust limit for the interactive-tx <c>tx_add_output</c> check (NL-473): the larger of
    /// both sides' <c>dust_limit_satoshis</c>, since the funding output serves both commitments. 0 when the channel is
    /// no longer in memory (only Bitcoin Core's standardness floor applies then).
    /// </summary>
    private ulong GetDustLimitSatoshis(ChannelId channelId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
            ? (ulong)Math.Max(channel.ChannelParams.Local.DustLimitAmount.Satoshi,
                              channel.ChannelParams.Remote.DustLimitAmount.Satoshi)
            : 0;

    private SpliceNegotiationHost CreateHost(SpliceNegotiation negotiation)
    {
        var host = new SpliceNegotiationHost(this, negotiation);
        negotiation.Host = host;
        _hosts[negotiation.ChannelId] = host;
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
        // A taproot splice always rotates (NL-965): verification nonces are bound to the funding key, and key 0's of a
        // v1 open have a commitment 0 without a txid (NL-972)
        if (!_options.RotateFundingKey && !channel.ChannelParams.OptionSimpleTaproot)
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

    /// <summary>NL-591: a node draining for its shutdown starts no splice and no RBF of one.</summary>
    private bool IsDraining() => _serviceProvider.GetService<INodeDrainState>() is { IsDraining: true };

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
    /// fields, the shared input and the new funding output (a P2WSH output, 34-byte script; a simple taproot channel's
    /// P2TR output has a 34-byte script too, and its shared input is a key-path spend).
    /// </summary>
    private static long GetInitiatorSharedWeight(bool simpleTaproot) =>
        CollaborativeFeeCalculator.CommonFieldsWeight + SpliceFundingScripts.GetSharedInputWeight(simpleTaproot)
      + CollaborativeFeeCalculator.OutputWeight(new BitcoinScript(new byte[34]));

    /// <summary>D16: the fee a splice-out we initiate pays from our channel balance.</summary>
    private static LightningMoney GetSpliceOutFee(BitcoinScript destination, uint feeratePerKw, bool simpleTaproot) =>
        CollaborativeFeeCalculator.FeeForWeight(GetInitiatorSharedWeight(simpleTaproot)
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