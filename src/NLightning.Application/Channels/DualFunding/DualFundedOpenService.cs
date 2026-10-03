using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.DualFunding;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Policies;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Constants;
using Domain.Node.Events;
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
using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx;
using InteractiveTx.Interfaces;
using InteractiveTx.Models;
using Interfaces;
using LiquidityAds;

/// <summary>
/// Dual-funded (v2) channel opens (BOLT 2 "Channel Establishment v2"; splicing plan wave DF, DF1/DF2) over the
/// interactive-tx driver: <c>open_channel2</c>/<c>accept_channel2</c>, the funding negotiation (our wallet's share
/// through the driver's <c>IInteractiveTxContributor</c>), the zero-HTLC first <c>commitment_signed</c> both ways,
/// <c>tx_signatures</c> in the IT-SIG-01 order, the funding broadcast, and RBF of an unconfirmed open. From the fully
/// signed funding transaction on the channel follows the v1 path: the funding confirmation sends <c>channel_ready</c>
/// and the first <c>channel_ready</c> builds the commitment snapshot.
/// </summary>
/// <remarks>
/// <para>The channel is persisted in <see cref="ChannelState.V1FundingSigned"/> (the state the confirmation, the
/// reestablish and the startup code know) in the save that precedes our <c>commitment_signed</c>, together with the
/// interactive-tx row (BOLT 2: "MUST remember the details of this funding transaction"); before that it lives in
/// memory only. Its <see cref="ChannelModel.Version"/> is <see cref="ChannelVersion.V2"/>.</para>
/// <para>Singleton; every member that changes a negotiation runs under the channel's lock (the handlers under the one
/// <c>ChannelManager</c> holds, <see cref="OpenAsync"/>/<see cref="BumpAsync"/> take it themselves).</para>
/// <para>RBF (<see cref="DualFundingOptions.AllowRbf"/>, on by default): an RBF may change either contribution (BOLT 2,
/// NL-521: the capacity, balances and reserve follow the attempt); every fully signed attempt is stored with the peer's
/// signature of our first commitment and our share, so the channel follows whichever attempt confirms
/// (<see cref="OnFundingConfirmedAsync"/>, NL-528). Either role starts an RBF (BOLT 2 "Fee bumping": the sender "MAY be
/// either the <i>initiator</i> or the <i>accepter</i>", NL-530; the sender is the new attempt's interactive-tx initiator),
/// and a peer's RBF is followed in either role.</para>
/// </remarks>
public sealed class DualFundedOpenService : IDualFundedOpenService, IDisposable
{
    private const string OpenTimedOut = "the dual-funded open timed out";

    private readonly ConcurrentDictionary<ChannelId, DualFundNegotiation> _negotiations = new();
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly ConcurrentDictionary<ChannelId, (ChannelReadyMessage Message, FeatureOptions Features)>
        _deferredChannelReady = new();
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOpenValidator _channelOpenValidator;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly IFeeService _feeService;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<DualFundedOpenService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISha256 _sha256;
    private readonly DualFundingOptions _options;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;

    public DualFundedOpenService(IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 IChannelOpenValidator channelOpenValidator,
                                 ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                 ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                 IFeeService feeService, ILightningSigner lightningSigner,
                                 ILogger<DualFundedOpenService> logger, IMessageFactory messageFactory,
                                 IServiceProvider serviceProvider, ISha256 sha256,
                                 IOptions<NodeOptions>? nodeOptions = null,
                                 IOptions<GossipOptions>? gossipOptions = null,
                                 IOptions<DualFundingOptions>? options = null, TimeProvider? timeProvider = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOpenValidator = channelOpenValidator;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _feeService = feeService;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _serviceProvider = serviceProvider;
        _sha256 = sha256;
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _options = options?.Value ?? new DualFundingOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The accepter policy (our contribution to a peer's open), from <c>Node:DualFund</c>.</summary>
    public DualFundingOptions Options => _options;

    #region Opener

    /// <inheritdoc />
    public async Task<DualFundedOpenResult> OpenAsync(DualFundedOpenRequest request,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Without a peer manager (in-process harnesses) our own features stand for the negotiated ones
        var peerManager = _serviceProvider.GetService<IPeerManager>();
        var peer = peerManager is null
                       ? null
                       : peerManager.GetPeer(request.PeerNodeId)
                      ?? throw new InvalidOperationException($"Peer {request.PeerNodeId} is not connected");
        var features = peer?.NegotiatedFeatures ?? _nodeOptions.Features;
        if (features.DualFund == FeatureSupport.No)
            throw new InvalidOperationException($"option_dual_fund is not negotiated with {request.PeerNodeId}");
        if (GetMonitor() is { IsChainProcessingHalted: true })
            throw new InvalidOperationException("Chain processing is halted: no new channel (NL-216)");
        if (IsDraining())
            throw new InvalidOperationException(NodeDrain.Refusal("openchannel"));
        if (request.IsPublic && !_gossipOptions.ArePublicChannelsAllowed(_nodeOptions.BitcoinNetwork))
            throw new InvalidOperationException("Public channels are not enabled on this network");
        if (request.LocalFundingAmount.IsZero)
            throw new InvalidOperationException("The opener must contribute to a dual-funded open");
        if (request.LocalFundingAmount < _nodeOptions.MinimumChannelSize)
            throw new InvalidOperationException(
                $"Funding amount {request.LocalFundingAmount} is below Node:MinimumChannelSize");
        if (request.LocalFundingAmount >= Domain.Channels.Constants.ChannelConstants.LargeChannelAmount
         && features.LargeChannels == FeatureSupport.No)
            throw new InvalidOperationException("The peer doesn't support large channels");

        var fundingFeerate = request.FundingFeeratePerKw ?? await EstimateFeerateAsync(cancellationToken);
        // The commitment feerate from our estimate is at least Node:MinCommitmentFeeRatePerKw (NL-564)
        var commitmentFeerate = request.CommitmentFeeratePerKw
                             ?? _nodeOptions.GetCommitmentFeeRatePerKw(await EstimateFeerateAsync(cancellationToken));
        fundingFeerate = Math.Max(fundingFeerate, (uint)ChannelOpenValidator.MinAcceptableFeeRatePerKw.Satoshi);
        commitmentFeerate = Math.Max(commitmentFeerate,
                                     (uint)ChannelOpenValidator.MinAcceptableFeeRatePerKw.Satoshi);

        var keyIndex = _lightningSigner.CreateNewChannel(out var basepoints, out var firstPoint);
        var secondPoint = _lightningSigner.GetPerCommitmentPoint(keyIndex, 1);
        var optionAnchors = features.OptionAnchors > FeatureSupport.No;
        var useScidAlias = features.ScidAlias == FeatureSupport.No
                               ? FeatureSupport.No
                               : request.IsPublic
                                   ? FeatureSupport.Optional
                                   : FeatureSupport.Compulsory;

        // Liquidity ads (NL-850): the request goes out with open_channel2; the seller contributes at least the amount,
        // so the fee is known now (min(requested, contributed) is the requested amount) and checked before anything is
        // sent: our limit, and our share must still pay it and the first commitment's fee
        DualFundLiquidityRequest? liquidityRequest = null;
        if (request.Liquidity is { } liquidity)
        {
            var liquidityAds = GetLiquidityAds()
                            ?? throw new InvalidOperationException("Liquidity ads are not available on this node");
            var requestFunding = liquidityAds.CreateRequest(request.PeerNodeId, liquidity, fundingFeerate, true);
            var fees = LiquidityAdsRules.ComputeFees(requestFunding.Rate, fundingFeerate, requestFunding.RequestedSat,
                                                     requestFunding.RequestedSat, true);
            if ((liquidity.MaxFeeSat ?? liquidityAds.Options.MaxFeeSat) is { } maxFee && fees.TotalSat > maxFee)
                throw new InvalidOperationException(
                    $"The liquidity fee of {fees.TotalSat} sat ({fees.MiningFeeSat} mining + {fees.ServiceFeeSat} "
                  + $"service) is above the limit of {maxFee} sat");
            if (DualFundLiquidity.GetBalanceViolation(request.LocalFundingAmount,
                                                      LightningMoney.Satoshis(requestFunding.RequestedSat),
                                                      checked((long)fees.TotalMsat), true,
                                                      CommitmentFeeCalculator.FunderCost(commitmentFeerate,
                                                          features.OptionAnchors > FeatureSupport.No, 0))
                is { } violation)
                throw new InvalidOperationException($"Cannot buy {requestFunding.RequestedSat} sat: {violation}");

            liquidityRequest = new DualFundLiquidityRequest(requestFunding, liquidity.MaxFeeSat, fundingFeerate);
        }

        // Our in-flight limit is announced now and fixed for the channel's lifetime (BOLT 2): the accepter's share is
        // not known yet, so the capacity counts ours and the liquidity we buy (the seller contributes at least that);
        // a channel that can be spliced announces no cap (NL-880, NL-881)
        var inFlightCapacity = request.LocalFundingAmount
                             + LightningMoney.Satoshis(liquidityRequest?.Request.RequestedSat ?? 0UL);
        var localParams = CreateLocalParams(
            DualFundingRules.GetChannelReserve(request.LocalFundingAmount, _nodeOptions.DustLimitAmount),
            GetAnnouncedMaxHtlcValueInFlight(inFlightCapacity, features));
        var channelParams = new ChannelParams(localParams, ChannelParty.Unknown,
                                              LightningMoney.Satoshis(commitmentFeerate), _nodeOptions.MinimumDepth,
                                              optionAnchors, useScidAlias)
        {
            AnnounceChannel = request.IsPublic
        };
        var channelType = channelParams.ToChannelType().GetWireBytes() ?? [];

        var temporaryId = ChannelIdV2.DeriveTemporary(_sha256, basepoints.RevocationBasepoint);
        var localKeySet = CreateLocalKeySet(keyIndex, basepoints, firstPoint);
        var placeholder = new ChannelModel(channelParams, temporaryId, null, null, true, null, null,
                                           request.LocalFundingAmount, localKeySet, 0, 0, LightningMoney.Zero, null, 0,
                                           request.PeerNodeId, 0, ChannelState.V1Opening, ChannelVersion.V2)
        {
            // NL-602 A3-T1: carried to the channel built at accept_channel2, stored with its first save
            Label = request.Labels.Label,
            Tags = request.Labels.CanonicalTags
        };

        var negotiation = new DualFundNegotiation(temporaryId, temporaryId, request.PeerNodeId, true)
        {
            Channel = placeholder,
            LocalShare = request.LocalFundingAmount,
            FundingFeeratePerKw = fundingFeerate,
            Locktime = GetLocktime(),
            LocalRequiresConfirmedInputs = request.RequireConfirmedInputs,
            LiquidityRequest = liquidityRequest,
            Pending = new DualFundNegotiation.PendingOpen(keyIndex, basepoints, firstPoint, localParams, channelType,
                                                          commitmentFeerate, request.IsPublic, optionAnchors,
                                                          useScidAlias),
            OpenCompletion = new TaskCompletionSource<DualFundedOpenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        negotiation.Host = new DualFundHost(this, negotiation);
        if (!_negotiations.TryAdd(temporaryId, negotiation))
            throw new InvalidOperationException($"An open with temporary id {temporaryId} is already in progress");

        var message = _messageFactory.CreateOpenChannel2Message(
            temporaryId, fundingFeerate, commitmentFeerate, request.LocalFundingAmount, localParams,
            negotiation.Locktime, basepoints.FundingPubKey, basepoints.RevocationBasepoint,
            basepoints.PaymentBasepoint, basepoints.DelayedPaymentBasepoint, basepoints.HtlcBasepoint, firstPoint,
            secondPoint,
            new ChannelFlags(request.IsPublic ? ChannelFlag.AnnounceChannel : ChannelFlag.None),
            new ChannelTypeTlv(channelType), new UpfrontShutdownScriptTlv(Array.Empty<byte>()), request.RequireConfirmedInputs,
            liquidityRequest?.Request);

        // The peer's error for the open (on the temporary or the v2 id) ends it. Warnings arrive through the same event
        // and cannot be told apart: both only end an open we have not signed yet (EndFailedOpenAsync)
        var peerService = peer is not null && peer.TryGetPeerService(out var service) ? service : null;
        void OnAttention(object? _, AttentionMessageEventArgs args)
        {
            if (args.ChannelId is { } id && (id == temporaryId || id == negotiation.ChannelId))
                negotiation.OpenCompletion.TrySetResult(
                    new DualFundedOpenResult(negotiation.ChannelId, null, $"peer error: {args.Message}"));
        }

        if (peerService is not null)
            peerService.OnAttentionMessageReceived += OnAttention;
        try
        {
            var channelManager = _serviceProvider.GetRequiredService<IChannelManager>();
            await channelManager.StartOpeningChannelAsync(request.PeerNodeId, placeholder, message);
            _logger.LogInformation("Sent open_channel2 {TemporaryChannelId} to {Peer} for {Amount} at {Feerate} sat/kw",
                                   temporaryId, request.PeerNodeId, request.LocalFundingAmount, fundingFeerate);

            var result = await WaitAsync(negotiation.OpenCompletion.Task, cancellationToken);
            return result.FailureReason is null ? result : await EndFailedOpenAsync(negotiation, result);
        }
        catch (TimeoutException)
        {
            return await EndFailedOpenAsync(negotiation,
                                            new DualFundedOpenResult(negotiation.ChannelId, null, OpenTimedOut));
        }
        finally
        {
            if (peerService is not null)
                peerService.OnAttentionMessageReceived -= OnAttention;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> HandleAcceptChannel2Async(AcceptChannel2Message message,
        FeatureOptions negotiatedFeatures, CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var temporaryId = payload.ChannelId;
        if (!_negotiations.TryGetValue(temporaryId, out var negotiation) || !negotiation.IsOpener
         || negotiation.Peer != peerPubKey || negotiation.Pending is not { } pending)
            throw new ChannelErrorException($"accept_channel2 for an unknown open {temporaryId}", temporaryId,
                                            "unknown channel");

        try
        {
            // BOLT 2: channel_type MUST be set and MUST be the one from open_channel2
            if (message.ChannelTypeTlv is null
             || !message.ChannelTypeTlv.Features.GetWireBytes().AsSpan().SequenceEqual(pending.ChannelType))
                throw new ChannelErrorException("accept_channel2 did not echo our channel_type", temporaryId,
                                                "channel_type must be the one of open_channel2");

            negotiation.RemoteShare = payload.FundingAmount;
            negotiation.RemoteRequiresConfirmedInputs = message.RequireConfirmedInputsTlv is not null;

            var total = negotiation.Total;
            var reserve = DualFundingRules.GetChannelReserve(total, Max(payload.DustLimitAmount,
                                                                        pending.LocalParams.DustLimitAmount));
            var remoteParams = new ChannelParty(payload.DustLimitAmount, reserve, payload.HtlcMinimumAmount,
                                                payload.MaxAcceptedHtlcs, payload.MaxHtlcValueInFlightAmount,
                                                payload.ToSelfDelay, NonEmpty(message.UpfrontShutdownScriptTlv));
            _channelOpenValidator.PerformMandatoryChecks(new ChannelOpenMandatoryValidationParameters
            {
                ChannelTypeTlv = message.ChannelTypeTlv,
                CurrentFeeRatePerKw = LightningMoney.Satoshis(pending.CommitmentFeeratePerKw),
                NegotiatedFeatures = negotiatedFeatures,
                ToSelfDelay = payload.ToSelfDelay,
                MaxAcceptedHtlcs = payload.MaxAcceptedHtlcs,
                DustLimitAmount = payload.DustLimitAmount,
                ChannelReserveAmount = reserve
            }, out _);
            _channelOpenValidator.CheckMaxHtlcValueInFlight(total, payload.MaxHtlcValueInFlightAmount);

            // Our in-flight limit stays the one open_channel2 announced (NL-881): nothing can change it on the wire
            var localParams = CreateLocalParams(reserve, pending.LocalParams.MaxHtlcValueInFlight);
            var channelParams = new ChannelParams(localParams, remoteParams,
                                                  LightningMoney.Satoshis(pending.CommitmentFeeratePerKw),
                                                  payload.MinimumDepth, pending.OptionAnchors, pending.UseScidAlias)
            {
                AnnounceChannel = pending.IsPublic
            };
            var remoteKeySet = ChannelKeySetModel.CreateForRemote(payload.FundingCompactPubKey,
                                                                  payload.RevocationCompactBasepoint,
                                                                  payload.PaymentCompactBasepoint,
                                                                  payload.DelayedPaymentCompactBasepoint,
                                                                  payload.HtlcCompactBasepoint,
                                                                  payload.FirstPerCommitmentCompactPoint);
            var channelId = ChannelIdV2.Derive(_sha256, pending.Basepoints.RevocationBasepoint,
                                               payload.RevocationCompactBasepoint);

            // Liquidity ads (NL-850): the seller's answer to our request, checked before anything is funded (a missing
            // or invalid answer fails the open with an error, as Eclair's validateRemoteFunding does)
            if (negotiation.LiquidityRequest is { } liquidityRequest)
            {
                negotiation.AttemptLiquidity = CheckWillFund(
                    negotiation, liquidityRequest, message.ProvideFundingTlv?.WillFund,
                    GetFundingScript(negotiation.Total, pending.Basepoints.FundingPubKey,
                                     payload.FundingCompactPubKey), negotiation.LocalShare, payload.FundingAmount,
                    pending.CommitmentFeeratePerKw, pending.OptionAnchors) switch
                {
                    { Refusal: { } refusal } => throw new ChannelErrorException(
                                                    $"Refusing the liquidity of {peerPubKey}: {refusal}", temporaryId,
                                                    $"invalid liquidity ads answer: {refusal}"),
                    { Liquidity: var bought } => bought
                };
                negotiation.LiquidityRequest = null;
            }
            else if (message.ProvideFundingTlv is not null)
            {
                _logger.LogWarning("accept_channel2 of {TemporaryChannelId} carries provide_funding we did not ask for; "
                                 + "ignoring it", temporaryId);
            }

            var channel = CreateChannel(channelParams, channelId,
                                        CreateLocalKeySet(pending.KeyIndex, pending.Basepoints,
                                                          pending.FirstPerCommitmentPoint), remoteKeySet, peerPubKey,
                                        true, negotiation.LocalShare, negotiation.RemoteShare,
                                        negotiation.AttemptLiquidity?.LocalFeeMsat ?? 0);
            channel.Label = negotiation.Channel?.Label;
            channel.Tags = negotiation.Channel?.Tags;
            if (!_negotiations.TryAdd(channelId, negotiation))
                throw new ChannelErrorException($"Channel {channelId} is already being opened", temporaryId);

            _negotiations.TryRemove(temporaryId, out _);
            negotiation.ChannelId = channelId;
            negotiation.Channel = channel;
            negotiation.Pending = null;
            _channelMemoryRepository.UpgradeChannel(temporaryId, channel);

            _logger.LogInformation(
                "accept_channel2 for {TemporaryChannelId}: channel {ChannelId}, {Local} + {Remote} at {Feerate} sat/kw",
                temporaryId, channelId, negotiation.LocalShare, negotiation.RemoteShare,
                negotiation.FundingFeeratePerKw);

            var terms = CreateTerms(negotiation, true, negotiation.FundingFeeratePerKw, negotiation.Locktime,
                                    new InteractiveTxContributionRequest(
                                        channelId, InteractiveTxPurpose.DualFund, negotiation.LocalShare, [],
                                        negotiation.FundingFeeratePerKw,
                                        DualFundingRules.GetOpenerExtraWeight(GetFundingScript(channel)),
                                        negotiation.RemoteRequiresConfirmedInputs));
            try
            {
                return await GetDriver().StartAsync(terms, negotiation.Host!, cancellationToken);
            }
            catch (InsufficientFundsException e)
            {
                throw new ChannelErrorException($"Cannot fund our share: {e.Message}", channelId,
                                                "the opener cannot fund its contribution");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            negotiation.OpenCompletion?.TrySetResult(
                new DualFundedOpenResult(negotiation.ChannelId, null, $"accept_channel2 refused: {e.Message}"));
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw,
                                                CancellationToken cancellationToken = default) =>
        BumpAsync(channelId, feeratePerKw, null, cancellationToken);

    /// <summary>
    /// <see cref="BumpAsync(ChannelId, uint, CancellationToken)"/> with our <c>funding_output_contribution</c> changed
    /// to <paramref name="localContribution"/> (BOLT 2: the sender "MAY set <c>funding_output_contribution</c> to a
    /// different value", NL-521); null keeps it. Either role may bump (BOLT 2 "Fee bumping": the sender of
    /// <c>tx_init_rbf</c> "MAY be either the <i>initiator</i> or the <i>accepter</i>", NL-530): the sender becomes the
    /// interactive-tx initiator of the new attempt, adds the funding output and pays the common fields, whichever side
    /// opened the channel (the opener still pays the first commitment's fee). Our contribution: the previous attempt's
    /// inputs re-added (IT-RBF-01) with the change paying the new fee, or, when we contributed nothing before (an
    /// accepter), fresh wallet inputs that pay our share and the initiator's weight, also for a share of 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">As the other overload, or the new contribution is refused
    /// (<see cref="GetRbfShareViolation"/>) or our inputs cannot pay it.</exception>
    public Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw,
                                                LightningMoney? localContribution,
                                                CancellationToken cancellationToken = default) =>
        BumpAsync(channelId, feeratePerKw, localContribution, null, cancellationToken);

    /// <summary>
    /// <see cref="BumpAsync(ChannelId, uint, LightningMoney?, CancellationToken)"/> buying <paramref name="liquidity"/>
    /// from the peer with the new attempt (liquidity ads, NL-850): our <c>tx_init_rbf</c> carries
    /// <c>request_funding</c>, the peer's <c>tx_ack_rbf</c> must answer it (<c>tx_abort</c> otherwise) and its fee, at
    /// the new feerate, moves from our balance to the peer's. Null repeats the purchase of the attempt it replaces (its
    /// amount and rate, re-quoted at the new feerate; BOLT PR #1153 fails an RBF that drops a purchase made before),
    /// or buys nothing when that attempt bought nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">As the other overloads, the peer's rates cannot sell it, the fee is
    /// above the limit, or we sold liquidity in the attempt it replaces (only the buyer can keep its purchase in an
    /// RBF).</exception>
    public async Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw,
                                                      LightningMoney? localContribution, LiquidityRequest? liquidity,
                                                      CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<DualFundedOpenResult> completion;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            var negotiation = await GetOrLoadAsync(channelId, null, cancellationToken)
                           ?? throw new InvalidOperationException($"No dual-funded open on channel {channelId}");
            if (negotiation.Channel is not { State: ChannelState.V1FundingSigned } channel)
                throw new InvalidOperationException(
                    $"Channel {channelId} is not waiting for its funding (channel_ready sent or received)");
            if (negotiation.CompletedTxIds.Count == 0 || negotiation.LastContribution is not { } previous)
                throw new InvalidOperationException($"Channel {channelId} has no signed funding transaction to replace");

            // No attempt runs (the driver refuses a second one): shares a refused attempt took are dropped
            negotiation.RestoreShares();
            if (await GetRbfRefusalAsync(negotiation) is { } refusal)
                throw new InvalidOperationException($"Channel {channelId}: {refusal}");

            var minimum = InteractiveTxDriver.GetMinimumRbfFeeratePerKw(negotiation.LastFeeratePerKw);
            if (feeratePerKw < minimum)
                throw new InvalidOperationException(
                    $"[IT-RBF-01] {feeratePerKw} sat/kw is below the minimum {minimum} sat/kw");

            var share = localContribution ?? negotiation.LocalShare;
            if (negotiation.IsOpener && share.IsZero)
                throw new InvalidOperationException("The opener must contribute to a dual-funded open");
            if (GetRbfShareViolation(negotiation, share, negotiation.RemoteShare.Satoshi) is { } violation)
                throw new InvalidOperationException($"Channel {channelId}: {violation}");

            var liquidityRequest = CreateRbfLiquidityRequest(negotiation, channel, share, feeratePerKw, liquidity);

            var contribution = await CreateInitiatorRbfContributionAsync(negotiation, channel, previous, share,
                                                                         feeratePerKw, cancellationToken);
            var fresh = previous.Inputs.Count == 0 && contribution.ReservationId is not null ? contribution : null;
            var terms = CreateTerms(negotiation, true, feeratePerKw, GetLocktime(), contribution: contribution);
            ChangeSharesForRbf(negotiation, share, negotiation.RemoteShare);
            negotiation.AttemptLiquidity = null;
            negotiation.LiquidityRequest = liquidityRequest;
            IReadOnlyList<IChannelMessage> messages;
            try
            {
                messages = await GetDriver().RequestRbfAsync(terms, share, cancellationToken,
                                                             liquidityRequest?.Request);
            }
            catch
            {
                negotiation.RestoreShares();
                negotiation.LiquidityRequest = null;
                if (fresh is not null)
                    await ReleaseContributionAsync(fresh);
                throw;
            }

            negotiation.FreshRbfContribution = fresh;
            completion = new TaskCompletionSource<DualFundedOpenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            negotiation.BumpCompletion = completion;
            _serviceProvider.GetRequiredService<IChannelMessagePublisher>().Publish(channel.RemoteNodeId, messages);
            _logger.LogInformation("tx_init_rbf of channel {ChannelId} at {Feerate} sat/kw as the {Role}, our share "
                                 + "{Share} sat", channelId, feeratePerKw,
                                   negotiation.IsOpener ? "opener" : "accepter", share.Satoshi);
        }

        try
        {
            return await WaitAsync(completion.Task, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new DualFundedOpenResult(channelId, null, "the RBF timed out");
        }
    }

    /// <summary>
    /// The <c>request_funding</c> of our <c>tx_init_rbf</c> (NL-850): <paramref name="liquidity"/> at the peer's rate, or
    /// the purchase of the attempt it replaces again (its amount, rate and fee limit, the fee re-quoted at the new
    /// feerate), or
    /// none when that attempt bought nothing. The fee, known now (the seller contributes at least the amount), must be
    /// within the limit and leave our share able to pay it (and the opener the first commitment's fee).
    /// </summary>
    private DualFundLiquidityRequest? CreateRbfLiquidityRequest(DualFundNegotiation negotiation, ChannelModel channel,
                                                                LightningMoney share, uint feeratePerKw,
                                                                LiquidityRequest? liquidity)
    {
        var previous = negotiation.LatestSignedPurchase;
        if (liquidity is null && previous is null)
            return null;
        if (previous is { Role: LiquidityPurchaseRole.Seller })
            throw new InvalidOperationException(
                $"Channel {channel.ChannelId}: we sold liquidity in the attempt to replace; only the buyer can bump it "
              + "(an RBF must carry its request_funding again)");

        var liquidityAds = GetLiquidityAds()
                        ?? throw new InvalidOperationException("Liquidity ads are not available on this node");
        var request = liquidity is not null
                          ? liquidityAds.CreateRequest(negotiation.Peer, liquidity, feeratePerKw, true)
                          : new RequestFunding(previous!.RequestedSat, previous.Rate,
                                               LiquidityPaymentDetails.FromChannelBalance);
        // A repeated purchase keeps the buyer's own fee limit of the attempt it replaces (NL-871): its fee grows with
        // the new feerate, and a bump without a new request names no limit of its own
        var maxFee = liquidity is not null ? liquidity.MaxFeeSat : previous!.MaxFeeSat;
        var fees = LiquidityAdsRules.ComputeFees(request.Rate, feeratePerKw, request.RequestedSat,
                                                 request.RequestedSat, true);
        if ((maxFee ?? liquidityAds.Options.MaxFeeSat) is { } limit && fees.TotalSat > limit)
            throw new InvalidOperationException(
                $"The liquidity fee of {fees.TotalSat} sat at {feeratePerKw} sat/kw is above the limit of {limit} sat");

        var channelParams = channel.ChannelParams;
        if (DualFundLiquidity.GetBalanceViolation(share, LightningMoney.Satoshis(request.RequestedSat),
                                                  checked((long)fees.TotalMsat), negotiation.IsOpener,
                                                  CommitmentFeeCalculator.FunderCost(
                                                      (ulong)channelParams.FeeRateAmountPerKw.Satoshi,
                                                      channelParams.OptionAnchorOutputs, 0))
            is { } violation)
            throw new InvalidOperationException($"Cannot buy {request.RequestedSat} sat: {violation}");

        return new DualFundLiquidityRequest(request, maxFee, feeratePerKw);
    }

    /// <summary>
    /// Our contribution to an RBF attempt we start, as its interactive-tx initiator (we add the funding output and pay
    /// the common fields, BOLT 2 "Fee bumping"): the previous attempt's inputs again with the change lowered (IT-RBF-01:
    /// "MUST ensure that the new transaction double-spends all other attempts"), or, when none of our inputs is in the
    /// earlier attempts (an accepter that funded nothing; the opener's re-added inputs double-spend them), fresh wallet
    /// inputs for our share plus the initiator's weight.
    /// </summary>
    private async Task<InteractiveTxContribution> CreateInitiatorRbfContributionAsync(
        DualFundNegotiation negotiation, ChannelModel channel, InteractiveTxContribution previous,
        LightningMoney share, uint feeratePerKw, CancellationToken cancellationToken)
    {
        if (previous.Inputs.Count > 0)
            return DualFundingRules.RebuildContributionForFeerate(
                       previous, share, true, GetSharedFunding(negotiation), feeratePerKw,
                       LightningMoney.Satoshis(ChangeDustLimitSat))
                ?? throw new InvalidOperationException(
                       $"Our inputs cannot pay {share} and the initiator's fees at {feeratePerKw} sat/kw; add funds "
                     + "and try again");

        try
        {
            return await GetContributor().ContributeAsync(
                       new InteractiveTxContributionRequest(negotiation.ChannelId, InteractiveTxPurpose.DualFundRbf,
                                                            share, [], feeratePerKw,
                                                            DualFundingRules.GetOpenerExtraWeight(
                                                                GetFundingScript(channel)),
                                                            negotiation.RemoteRequiresConfirmedInputs,
                                                            FundWeightWithoutAmount: true), cancellationToken);
        }
        catch (InsufficientFundsException e)
        {
            throw new InvalidOperationException(
                $"The wallet cannot pay {share} and the initiator's fees at {feeratePerKw} sat/kw: {e.Message}", e);
        }
    }

    /// <summary>Releases a contribution's wallet reservation (logged, never thrown).</summary>
    private async Task ReleaseContributionAsync(InteractiveTxContribution contribution)
    {
        try
        {
            await GetContributor().ReleaseAsync(contribution, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not release the RBF reservation {ReservationId}", contribution.ReservationId);
        }
    }

    #endregion

    #region Accepter

    /// <summary>
    /// The contribution our accepter policy makes to a peer's <c>open_channel2</c>
    /// (<see cref="DualFundingOptions.AcceptContributionSat"/>, capped at the opener's by default).
    /// </summary>
    public LightningMoney GetAcceptContribution(OpenChannel2Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Liquidity ads (NL-850): a seller contributes exactly the requested amount (checked in AcceptAsync)
        return message.RequestFundingTlv is { Request: var request }
                   ? LightningMoney.Satoshis(request.RequestedSat)
                   : GetAcceptContribution(message.Payload.FundingAmount);
    }

    private LightningMoney GetAcceptContribution(LightningMoney openerContribution)
    {
        var wanted = LightningMoney.Satoshis(_options.AcceptContributionSat);
        return _options.MatchOpenerContribution && wanted > openerContribution ? openerContribution : wanted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> AcceptAsync(OpenChannel2Message message,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey,
                                                                  LightningMoney localContribution,
                                                                  IUnitOfWork unitOfWork,
                                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        ArgumentNullException.ThrowIfNull(localContribution);
        var temporaryId = message.Payload.ChannelId;
        if (negotiatedFeatures.DualFund == FeatureSupport.No)
            throw new ChannelErrorException("open_channel2 without option_dual_fund", temporaryId,
                                            "option_dual_fund is not negotiated");

        // Liquidity ads (NL-850): a request_funding makes us the seller of exactly the requested amount, at one of our
        // rates and within the griefing caps (D-L5); a refused request is an error for the open (Eclair's behavior)
        LiquidityAdsService.LiquiditySale? sale = null;
        if (message.RequestFundingTlv is { Request: var requestFunding })
        {
            var liquidityAds = GetLiquidityAds()
                            ?? throw new ChannelErrorException("open_channel2 asks for liquidity we do not sell",
                                                               temporaryId, "we do not sell liquidity");
            if (liquidityAds.TryStartSale(peerPubKey, requestFunding, out sale) is { } refusal)
            {
                _logger.LogInformation("Refusing the liquidity request of {Peer} in open_channel2 {TemporaryChannelId}: "
                                     + "{Reason}", peerPubKey, temporaryId, refusal);
                throw new ChannelErrorException($"Refusing the liquidity request: {refusal}", temporaryId, refusal);
            }

            localContribution = LightningMoney.Satoshis(requestFunding.RequestedSat);
        }

        try
        {
            return await AcceptCoreAsync(message, negotiatedFeatures, peerPubKey, localContribution, sale,
                                         cancellationToken);
        }
        catch
        {
            // The negotiation gives the slot back when it ends; one that never started gives it back here
            sale?.Dispose();
            throw;
        }
    }

    private async Task<IReadOnlyList<IChannelMessage>> AcceptCoreAsync(OpenChannel2Message message,
                                                                      FeatureOptions negotiatedFeatures,
                                                                      CompactPubKey peerPubKey,
                                                                      LightningMoney localContribution,
                                                                      LiquidityAdsService.LiquiditySale? sale,
                                                                      CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        var temporaryId = payload.ChannelId;

        if (GetMonitor() is { IsChainProcessingHalted: true })
            throw new ChannelErrorException("Chain processing is halted (NL-216)", temporaryId,
                                            "Not accepting channels right now, try again later");
        if (IsDraining())
            throw new ChannelErrorException(NodeDrain.Refusal("open_channel2"), temporaryId,
                                            "Not accepting channels right now, try again later");
        if (payload.ChannelFlags.AnnounceChannel && (!_gossipOptions.AcceptPublicChannels
                                                  || !_gossipOptions.ArePublicChannelsAllowed(
                                                         _nodeOptions.BitcoinNetwork)))
            throw new ChannelErrorException("Refusing a public channel", temporaryId,
                                            "We don't accept public channels");
        if (_channelMemoryRepository.TryGetTemporaryChannelState(peerPubKey, temporaryId, out _)
         || _negotiations.ContainsKey(temporaryId))
            throw new ChannelErrorException("A channel with this temporary id is already being opened", temporaryId,
                                            "This channel is already being negotiated with peer");

        var violation = DualFundingRules.CheckOpenChannel2(message.ChannelTypeTlv?.Features,
                                                           payload.FundingFeeRatePerKw,
                                                           payload.CommitmentFeeRatePerKw, payload.Locktime,
                                                           (uint)ChannelOpenValidator.MinAcceptableFeeRatePerKw.Satoshi);
        if (violation is not null)
            throw new ChannelErrorException(violation, temporaryId, violation);

        // Keys first: the reserve and the checks need both contributions and our dust limit
        var total = LightningMoney.MilliSatoshis(payload.FundingAmount.MilliSatoshi + localContribution.MilliSatoshi);
        var reserve = DualFundingRules.GetChannelReserve(total, Max(payload.DustLimitAmount,
                                                                    _nodeOptions.DustLimitAmount));
        var localParams = CreateLocalParams(reserve, GetAnnouncedMaxHtlcValueInFlight(total, negotiatedFeatures));
        var remoteParams = new ChannelParty(payload.DustLimitAmount, reserve, payload.HtlcMinimumAmount,
                                            payload.MaxAcceptedHtlcs, payload.MaxHtlcValueInFlightAmount,
                                            payload.ToSelfDelay, NonEmpty(message.UpfrontShutdownScriptTlv));

        var currentFeerate = await _feeService.GetFeeRatePerKwAsync(cancellationToken);
        _channelOpenValidator.PerformMandatoryChecks(new ChannelOpenMandatoryValidationParameters
        {
            ChannelTypeTlv = message.ChannelTypeTlv,
            CurrentFeeRatePerKw = currentFeerate,
            NegotiatedFeatures = negotiatedFeatures,
            ChainHash = payload.ChainHash,
            FundingAmount = payload.FundingAmount,
            ToSelfDelay = payload.ToSelfDelay,
            MaxAcceptedHtlcs = payload.MaxAcceptedHtlcs,
            FeeRatePerKw = LightningMoney.Satoshis(payload.CommitmentFeeRatePerKw),
            DustLimitAmount = payload.DustLimitAmount,
            ChannelReserveAmount = reserve,
            ChannelFlags = payload.ChannelFlags
        }, out var minimumDepth);
        _channelOpenValidator.CheckMaxHtlcValueInFlight(total, payload.MaxHtlcValueInFlightAmount);

        if (total >= Domain.Channels.Constants.ChannelConstants.LargeChannelAmount
         && negotiatedFeatures.LargeChannels == FeatureSupport.No)
            throw new ChannelErrorException("The channel would be a large channel", temporaryId,
                                            "We don't support large channels");

        var keyIndex = _lightningSigner.CreateNewChannel(out var basepoints, out var firstPoint);
        var secondPoint = _lightningSigner.GetPerCommitmentPoint(keyIndex, 1);
        var channelId = ChannelIdV2.Derive(_sha256, basepoints.RevocationBasepoint, payload.RevocationBasepoint);

        var optionAnchors = message.ChannelTypeTlv!.Features.IsFeatureSet(Feature.OptionAnchors, true);
        var useScidAlias = negotiatedFeatures.ScidAlias == FeatureSupport.No
                               ? FeatureSupport.No
                               : message.ChannelTypeTlv.Features.IsFeatureSet(Feature.OptionScidAlias, true)
                                   ? FeatureSupport.Compulsory
                                   : FeatureSupport.Optional;
        var channelParams = new ChannelParams(localParams, remoteParams,
                                              LightningMoney.Satoshis(payload.CommitmentFeeRatePerKw), minimumDepth,
                                              optionAnchors, useScidAlias)
        {
            AnnounceChannel = payload.ChannelFlags.AnnounceChannel
        };
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(payload.FundingPubKey, payload.RevocationBasepoint,
                                                              payload.PaymentBasepoint,
                                                              payload.DelayedPaymentBasepoint, payload.HtlcBasepoint,
                                                              payload.FirstPerCommitmentPoint);

        // The fee of a sale moves from the buyer's (the opener's) balance to ours: its share must pay it and the first
        // commitment's fee after it
        LiquidityFees? saleFees = null;
        if (sale is not null)
        {
            var request = sale.Request;
            saleFees = LiquidityAdsRules.ComputeFees(request.Rate, payload.FundingFeeRatePerKw, request.RequestedSat,
                                                     request.RequestedSat, true);
            if (DualFundLiquidity.GetBalanceViolation(localContribution, payload.FundingAmount,
                                                      -checked((long)saleFees.Value.TotalMsat), false,
                                                      CommitmentFeeCalculator.FunderCost(
                                                          payload.CommitmentFeeRatePerKw, optionAnchors, 0))
                is { } liquidityViolation)
                throw new ChannelErrorException($"Refusing the liquidity request: {liquidityViolation}", temporaryId,
                                                liquidityViolation);
        }

        var saleFeeMsat = saleFees is { } feesOfSale ? -checked((long)feesOfSale.TotalMsat) : 0;
        var negotiation = new DualFundNegotiation(channelId, temporaryId, peerPubKey, false)
        {
            LocalShare = localContribution,
            RemoteShare = payload.FundingAmount,
            FundingFeeratePerKw = payload.FundingFeeRatePerKw,
            Locktime = payload.Locktime,
            RemoteRequiresConfirmedInputs = message.RequireConfirmedInputsTlv is not null
        };
        negotiation.Host = new DualFundHost(this, negotiation);
        var channel = CreateChannel(channelParams, channelId, CreateLocalKeySet(keyIndex, basepoints, firstPoint),
                                    remoteKeySet, peerPubKey, false, localContribution, payload.FundingAmount,
                                    saleFeeMsat);
        negotiation.Channel = channel;
        if (sale is not null)
        {
            // Our will_fund signs the rate and the new funding output's script with the node key
            var willFund = GetLiquidityAds()!.CreateWillFund(sale.Request.Rate, GetFundingScript(channel));
            negotiation.AttemptLiquidity = new DualFundLiquidity(LiquidityPurchaseRole.Seller, sale.Request, willFund,
                                                                 saleFees!.Value, sale.Request.RequestedSat);
            negotiation.Sale = sale;
            _logger.LogInformation("Selling {Amount} sat of liquidity to {Peer} in open {TemporaryChannelId} for {Fee} "
                                 + "sat", sale.Request.RequestedSat, peerPubKey, temporaryId,
                                   saleFees.Value.TotalSat);
        }

        // NL-379: as for a v1 open, an anchors channel we cannot back with the anchors reserve is refused
        var anchorReserve = _serviceProvider.GetService<IAnchorReserveService>();
        if (optionAnchors && anchorReserve is not null)
        {
            try
            {
                await anchorReserve.EnsureCanAcceptAnchorsChannelAsync(channel);
                negotiation.HoldsAnchorReserve = true;
            }
            catch (AnchorReserveException e)
            {
                throw new ChannelErrorException(e.Message, temporaryId,
                                                "Not enough on-chain funds to keep the anchors reserve for this "
                                              + "channel");
            }
        }

        if (!_negotiations.TryAdd(channelId, negotiation))
        {
            ReleaseAnchorReserve(negotiation);
            throw new ChannelErrorException($"Channel {channelId} is already being opened", temporaryId);
        }

        try
        {
            await StartAccepterAsync(negotiation, cancellationToken);
        }
        catch
        {
            _negotiations.TryRemove(channelId, out _);
            ReleaseAnchorReserve(negotiation);
            throw;
        }

        // The share may have dropped to zero (the wallet could not fund it): the channel's balances follow it, and so
        // do the reserve and our in-flight limit (BOLT 2: 1% of open_channel2.funding_satoshis +
        // accept_channel2.funding_satoshis, the amount we announce, not the one we intended)
        if (negotiation.LocalShare != localContribution)
        {
            total = LightningMoney.MilliSatoshis(payload.FundingAmount.MilliSatoshi
                                               + negotiation.LocalShare.MilliSatoshi);
            reserve = DualFundingRules.GetChannelReserve(total, Max(payload.DustLimitAmount,
                                                                    _nodeOptions.DustLimitAmount));
            localParams = CreateLocalParams(reserve, GetAnnouncedMaxHtlcValueInFlight(total, negotiatedFeatures));
            remoteParams = new ChannelParty(payload.DustLimitAmount, reserve, payload.HtlcMinimumAmount,
                                            payload.MaxAcceptedHtlcs, payload.MaxHtlcValueInFlightAmount,
                                            payload.ToSelfDelay, NonEmpty(message.UpfrontShutdownScriptTlv));
            channelParams = new ChannelParams(localParams, remoteParams,
                                              LightningMoney.Satoshis(payload.CommitmentFeeRatePerKw), minimumDepth,
                                              optionAnchors, useScidAlias)
            {
                AnnounceChannel = payload.ChannelFlags.AnnounceChannel
            };
            channel = CreateChannel(channelParams, channelId, CreateLocalKeySet(keyIndex, basepoints, firstPoint),
                                    remoteKeySet, peerPubKey, false, negotiation.LocalShare, payload.FundingAmount,
                                    saleFeeMsat);
            negotiation.Channel = channel;
        }

        _channelMemoryRepository.AddChannel(channel);
        ScheduleAccepterTimeout(negotiation);
        _logger.LogInformation(
            "Accepting open_channel2 {TemporaryChannelId} from {Peer} as channel {ChannelId}: {Remote} + our {Local}",
            temporaryId, peerPubKey, channelId, negotiation.RemoteShare, negotiation.LocalShare);

        return
        [
            _messageFactory.CreateAcceptChannel2Message(temporaryId, negotiation.LocalShare, localParams,
                                                        minimumDepth, basepoints.FundingPubKey,
                                                        basepoints.RevocationBasepoint, basepoints.PaymentBasepoint,
                                                        basepoints.DelayedPaymentBasepoint,
                                                        basepoints.HtlcBasepoint, firstPoint, secondPoint,
                                                        message.ChannelTypeTlv, new UpfrontShutdownScriptTlv(Array.Empty<byte>()),
                                                        willFund: negotiation.AttemptLiquidity?.WillFund)
        ];
    }

    /// <summary>
    /// The accepter's negotiation must reach our <c>commitment_signed</c> within
    /// <see cref="DualFundingOptions.OpenTimeout"/>: a peer that stays connected and silent after
    /// <c>accept_channel2</c> would otherwise hold the anchors reserve and the channel in memory for as long as it likes.
    /// On expiry the negotiation is aborted with our <c>tx_abort</c> (the reservation released) and the open forgotten;
    /// an open that reached our <c>commitment_signed</c> is left to the driver (stored, resumed on reconnection).
    /// </summary>
    private void ScheduleAccepterTimeout(DualFundNegotiation negotiation)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var stopping = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.OpenTimeout, _timeProvider, stopping);
                using (await _channelLockProvider.AcquireAsync(negotiation.ChannelId, stopping))
                {
                    if (!_negotiations.TryGetValue(negotiation.ChannelId, out var current) || current != negotiation
                     || negotiation.Channel is not { State: ChannelState.V1Opening })
                        return;

                    _logger.LogInformation("The dual-funded open {ChannelId} of {Peer} did not reach commitment_signed "
                                         + "in {Timeout}; aborting it", negotiation.ChannelId, negotiation.Peer,
                                           _options.OpenTimeout);
                    if (await AbortNegotiationLockedAsync(negotiation, OpenTimedOut))
                        await ForgetUnfundedLockedAsync(negotiation);
                }
            }
            catch (OperationCanceledException)
            {
                // The service is stopping
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The open timeout of channel {ChannelId} failed", negotiation.ChannelId);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Starts the driver as non-initiator with our share; when the wallet cannot fund it (or confirmed inputs are
    /// required and we have none) the open goes on without our contribution (BOLT 2 recommends it).
    /// </summary>
    private async Task StartAccepterAsync(DualFundNegotiation negotiation, CancellationToken cancellationToken)
    {
        var driver = GetDriver();
        if (!negotiation.LocalShare.IsZero)
        {
            try
            {
                var request = new InteractiveTxContributionRequest(negotiation.ChannelId, InteractiveTxPurpose.DualFund,
                                                                   negotiation.LocalShare, [],
                                                                   negotiation.FundingFeeratePerKw, 0,
                                                                   negotiation.RemoteRequiresConfirmedInputs);
                await driver.StartAsync(CreateTerms(negotiation, false, negotiation.FundingFeeratePerKw,
                                                    negotiation.Locktime, request), negotiation.Host!,
                                        cancellationToken);
                return;
            }
            catch (InsufficientFundsException e) when (negotiation.AttemptLiquidity is not null)
            {
                // A sale contributes exactly what the buyer asked for or nothing at all (NL-850)
                _logger.LogWarning("Cannot fund the {Amount} of liquidity sold in the open of {ChannelId}: {Reason}",
                                   negotiation.LocalShare, negotiation.ChannelId, e.Message);
                throw new ChannelErrorException($"Cannot fund the requested liquidity: {e.Message}",
                                                negotiation.TemporaryChannelId,
                                                "cannot fund the requested liquidity right now");
            }
            catch (InsufficientFundsException e)
            {
                _logger.LogWarning("Cannot contribute {Amount} to the open of {ChannelId} ({Reason}); accepting it "
                                 + "without our funds", negotiation.LocalShare, negotiation.ChannelId, e.Message);
                negotiation.LocalShare = LightningMoney.Zero;
            }
        }

        await driver.StartAsync(CreateTerms(negotiation, false, negotiation.FundingFeeratePerKw, negotiation.Locktime),
                                negotiation.Host!, cancellationToken);
    }

    #endregion

    #region Host callbacks (under the channel's lock)

    internal SharedFundingSpec GetSharedFunding(DualFundNegotiation negotiation)
    {
        var channel = negotiation.Channel ?? throw new InvalidOperationException("The channel is not known yet");
        return new SharedFundingSpec(null, GetFundingScript(channel), negotiation.Total, LightningMoney.Zero,
                                     LightningMoney.Zero, negotiation.LocalShare, negotiation.RemoteShare);
    }

    /// <summary>
    /// The commitment step: the funding outpoint of the constructed transaction, our signature of the peer's first
    /// commitment (zero HTLCs), and the channel staged in <see cref="ChannelState.V1FundingSigned"/> with it.
    /// </summary>
    internal async Task<IReadOnlyList<IChannelMessage>> CreateCommitmentSignedAsync(
        DualFundNegotiation negotiation, InteractiveTxSessionModel session, IUnitOfWork unitOfWork)
    {
        var channel = negotiation.Channel ?? throw new InvalidOperationException("The channel is not known yet");
        var transaction = session.ConstructedTx
                       ?? throw new InvalidOperationException("The negotiated transaction is not constructed");
        var index = transaction.SharedOutputIndex
                 ?? throw new InvalidOperationException("The negotiated transaction has no funding output");
        var output = transaction.Outputs[(int)index];
        if (output.Amount.MilliSatoshi != negotiation.Total.MilliSatoshi
         || output.ScriptPubKey != GetFundingScript(channel))
            throw new InvalidOperationException("The funding output is not the channel's");

        var isFirstAttempt = channel.State == ChannelState.V1Opening;
        if (!isFirstAttempt && negotiation.LastSignedFunding is null
                            && channel.FundingOutput is
                            { TransactionId: { } signedTxId, Index: { } signedIndex } signedFunding)
        {
            // An RBF attempt: the last fully signed attempt stays restorable until this one is signed (OnAbortedAsync)
            negotiation.LastSignedFunding = new DualFundNegotiation.SignedFunding(
                signedTxId, signedIndex, signedFunding.Amount, channel.LocalBalance, channel.RemoteBalance,
                channel.ChannelParams, channel.LastSentSignature, channel.LastReceivedSignature);
        }

        CompactSignature signature;
        if (isFirstAttempt)
        {
            channel.FundingOutput!.TransactionId = transaction.TxId;
            channel.FundingOutput.Index = checked((ushort)index);
            RegisterWithSigner(channel);
            signature = _lightningSigner.SignChannelTransaction(channel.ChannelId,
                                                                BuildCommitment(channel, CommitmentSide.Remote));
        }
        else
        {
            // An RBF attempt: its outpoint, and its capacity, balances and reserve when a contribution changed
            // (NL-521); the signer signs it as a pending funding of the channel (the BIP 143 sighash commits to the
            // funding amount, so the first attempt's registration cannot sign it)
            ApplyAttemptFunding(negotiation, channel, transaction.TxId, checked((ushort)index));
            RegisterRbfFunding(channel, session);
            signature = _lightningSigner.SignChannelTransaction(channel.ChannelId, transaction.TxId,
                                                                BuildCommitment(channel, CommitmentSide.Remote));
        }

        channel.UpdateLastSentSignature(signature);

        if (isFirstAttempt)
        {
            channel.FundingCreatedAtBlockHeight = GetMonitor()?.LastProcessedBlockHeight ?? 0;
            channel.UpdateState(ChannelState.V1FundingSigned);
            await unitOfWork.ChannelDbRepository.AddAsync(channel);
        }
        else
        {
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        }

        // NL-138: the channel is registered (accept_channel2 added it), so the open's progress — for the first attempt
        // the V1Opening -> V1FundingSigned move the client's open subscription waits for — must go through
        // UpdateChannel for OnChannelUpdated to fire (the save itself follows in the driver's staged round)
        _channelMemoryRepository.UpdateChannel(channel);

        // The funding is watched from our commitment_signed on: once our tx_signatures go out the peer can broadcast
        // it, whether or not its own tx_signatures ever reach us (BOLT 2: "MUST remember the channel")
        await StageFundingWatchesAsync(negotiation, channel, transaction.TxId, index, unitOfWork);

        // Liquidity ads (NL-850): the attempt's purchase is stored with it (its fee moved the balances just signed, and
        // a restart or the confirmation of this attempt rebuilds them from it)
        if (negotiation.AttemptLiquidity is { } liquidity)
            RecordPurchase(negotiation, channel, transaction.TxId, liquidity, unitOfWork);

        negotiation.PendingTxId = transaction.TxId;
        negotiation.CommitmentSignedReceived = false;
        negotiation.LastContribution = session.LocalContribution;
        negotiation.LastFeeratePerKw = session.FeeratePerKw;

        _logger.LogInformation("Signed the first commitment of channel {ChannelId} for funding {TxId}",
                               channel.ChannelId, transaction.TxId);
        return
        [
            _messageFactory.CreateCommitmentSignedMessage(channel.ChannelId, signature, [], transaction.TxId)
        ];
    }

    /// <summary>
    /// Both <c>tx_signatures</c> were exchanged: the funding transaction, its watches and (for an RBF) the replaced
    /// attempts' rows are staged in the driver's save; the publish follows once that save is done.
    /// </summary>
    internal async Task<IReadOnlyList<IChannelMessage>> OnCompletedAsync(DualFundNegotiation negotiation,
                                                                         InteractiveTxCompletion completion,
                                                                         IUnitOfWork unitOfWork)
    {
        var channel = negotiation.Channel!;
        var txId = completion.Transaction.TxId;

        // A sale's negotiation is over: its slot goes back (D-L5)
        negotiation.EndSale();
        negotiation.AttemptLiquidity = null;
        if (channel.State != ChannelState.V1FundingSigned)
        {
            // Another attempt confirmed while this one was being signed (we had sent our tx_signatures, so it could
            // not be aborted, IT-ABT-01): it double-spends the confirmed one and never confirms (NL-528)
            _logger.LogWarning("RBF attempt {TxId} of channel {ChannelId} completed after its funding {Funding} "
                             + "confirmed; not publishing it", txId, channel.ChannelId,
                               channel.FundingOutput?.TransactionId);
            negotiation.PendingTxId = null;
            negotiation.LastSignedFunding = null;
            negotiation.SharesBeforeRbf = null;
            negotiation.Complete(new DualFundedOpenResult(channel.ChannelId, null,
                                                          "another attempt of the open confirmed first"));
            return [];
        }

        var index = completion.Transaction.SharedOutputIndex!.Value;
        var height = GetMonitor()?.LastProcessedBlockHeight ?? 0;

        // The row carries the funding transaction's whole fee (both sides' inputs are known, NL-604); our share of it is
        // the accounting feed's, at the confirmation
        var broadcast = new BroadcastTransactionModel(completion.SignedTransaction, BroadcastPurpose.Funding,
                                                      channel.ChannelId, height,
                                                      fee: LightningMoney.Satoshis(
                                                          Splicing.SpliceService.GetTotalFee(completion.Transaction)));
        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);

        // The watches were stored with our commitment_signed; a negotiation stored by an older build has none
        var (fundingWatch, outpointWatch) =
            await AddMissingFundingWatchesAsync(channel, txId, index, unitOfWork);

        // An RBF replaced the earlier attempts: they are no longer sent after every block
        foreach (var replaced in negotiation.CompletedTxIds)
            await unitOfWork.BroadcastTransactionDbRepository.MarkReplacedAsync(replaced);

        // The signer signs for the new attempt from now on, as the channel does (its capacity may differ, NL-521)
        if (negotiation.CompletedTxIds.Count > 0)
            LockRbfFunding(channel, txId);

        negotiation.CompletedTxIds.Add(txId);
        negotiation.PendingTxId = null;
        negotiation.LastSignedFunding = null;
        negotiation.SharesBeforeRbf = null;
        negotiation.FreshRbfContribution = null;
        TrackAfterSave(negotiation, txId, () => PublishAsync(negotiation, txId, fundingWatch, outpointWatch,
                                                              broadcast));
        return [];
    }

    /// <summary>
    /// The negotiation ended with <c>tx_abort</c> or a disconnection before our <c>tx_signatures</c>. A first attempt
    /// forgets the channel (a stored one is persisted <see cref="ChannelState.Stale"/>); an RBF attempt leaves the
    /// signed ones as they are.
    /// </summary>
    internal async Task OnAbortedAsync(DualFundNegotiation negotiation, string reason)
    {
        var abandonedTxId = negotiation.PendingTxId;
        negotiation.PendingTxId = null;
        negotiation.EndSale();
        negotiation.LiquidityRequest = null;
        negotiation.AttemptLiquidity = null;
        if (negotiation.FreshRbfContribution is { } fresh)
        {
            // Our tx_init_rbf ended (before or after its attempt existed): the reservation it took goes back; a stored,
            // signed or completed negotiation that holds it keeps it (the contributor's IT-ABT-01 guard)
            negotiation.FreshRbfContribution = null;
            await ReleaseContributionAsync(fresh);
        }

        if (negotiation.CompletedTxIds.Count > 0)
        {
            negotiation.RestoreShares();
            await RestoreLastSignedFundingAsync(negotiation, reason);
            if (abandonedTxId is { } txId)
                await ReplaceAbandonedPurchaseAsync(negotiation, txId);
            negotiation.BumpCompletion?.TrySetResult(new DualFundedOpenResult(negotiation.ChannelId, null, reason));
            negotiation.BumpCompletion = null;
            return;
        }

        _logger.LogInformation("Dual-funded open of {ChannelId} ended before it was signed: {Reason}",
                               negotiation.ChannelId, reason);
        await ForgetUnfundedLockedAsync(negotiation);
        negotiation.OpenCompletion?.TrySetResult(new DualFundedOpenResult(negotiation.ChannelId, null, reason));
    }

    /// <summary>
    /// The purchase stored with an RBF attempt that ended before both <c>tx_signatures</c> (liquidity ads, NL-867):
    /// nothing can confirm the attempt any more, so its row, still Pending, is replaced at once (no seller lease guard
    /// or griefing-cap slot held for it until the funding's depth), in a save of its own. Logged, never thrown.
    /// </summary>
    private async Task ReplaceAbandonedPurchaseAsync(DualFundNegotiation negotiation, TxId abandonedTxId)
    {
        if (!negotiation.Purchases.TryGetValue(abandonedTxId, out var purchase)
         || purchase is not { Id: > 0, Status: LiquidityPurchaseStatus.Pending })
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            purchase.MarkReplaced();
            unitOfWork.LiquidityPurchaseDbRepository.Update(purchase);
            await unitOfWork.SaveChangesAsync();
            negotiation.Purchases.Remove(abandonedTxId);
            _logger.LogInformation("Liquidity {Role} of channel {ChannelId} in the abandoned RBF attempt {TxId} replaced",
                                   purchase.Role, negotiation.ChannelId, abandonedTxId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not replace the liquidity purchase of the abandoned RBF attempt {TxId} of "
                              + "channel {ChannelId}", abandonedTxId, negotiation.ChannelId);
        }
    }

    /// <summary>
    /// An RBF attempt ended before both <c>tx_signatures</c> (<c>tx_abort</c>, a refused <c>commitment_signed</c>, a
    /// disconnection): the channel goes back to the last fully signed attempt's outpoint and signatures, persisted.
    /// </summary>
    private async Task RestoreLastSignedFundingAsync(DualFundNegotiation negotiation, string reason)
    {
        if (negotiation.LastSignedFunding is not { } signed
         || negotiation.Channel is not { FundingOutput: { } funding } channel)
            return;

        negotiation.LastSignedFunding = null;
        channel.ReplaceUnconfirmedFunding(
            new FundingOutputInfo(signed.Capacity, funding.LocalFundingPubKey, funding.RemoteFundingPubKey,
                                  signed.TransactionId, signed.Index), signed.LocalBalance, signed.RemoteBalance,
            signed.ChannelParams);

        // Without a restart the signer's current funding is still the signed attempt (the unsigned one was only a
        // pending funding); after one it is the attempt the channel row carried (NL-528)
        MoveSignerToFunding(channel, null);
        channel.UpdateLastSentSignature(signed.LastSentSignature
                                     ?? _lightningSigner.SignChannelTransaction(
                                            channel.ChannelId, BuildCommitment(channel, CommitmentSide.Remote)));
        if (signed.LastReceivedSignature is not null)
            channel.UpdateLastReceivedSignature(signed.LastReceivedSignature);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not persist the funding {TxId} of channel {ChannelId} back after the RBF "
                              + "attempt ended", signed.TransactionId, channel.ChannelId);
        }

        _channelMemoryRepository.UpdateChannel(channel);
        _logger.LogInformation("RBF attempt of channel {ChannelId} ended ({Reason}); back on funding {TxId}",
                               channel.ChannelId, reason, signed.TransactionId);
    }

    /// <summary>
    /// Why an RBF of the open is refused now, or null: RBF not allowed (<see cref="DualFundingOptions.AllowRbf"/>),
    /// channel_ready sent or received (BOLT 2: the sender "MUST NOT have sent or received a channel_ready message", the
    /// recipient "MUST fail the negotiation" then; a peer's channel_ready we keep until our confirmation counts,
    /// NL-528), or an attempt already confirmed (BOLT 2: "If the previous transaction confirms in the middle of an RBF
    /// attempt, the attempt MUST be abandoned"). A public channel may be bumped: its announcement is built once the
    /// funding confirmed, from the attempt the channel follows (NL-528).
    /// </summary>
    private async Task<string?> GetRbfRefusalAsync(DualFundNegotiation negotiation)
    {
        if (!_options.AllowRbf)
            return "RBF of a dual-funded open is not enabled";
        if (negotiation.Channel is not { State: ChannelState.V1FundingSigned }
         || _deferredChannelReady.ContainsKey(negotiation.ChannelId))
            return "channel_ready was already sent or received";

        return await GetConfirmedAttemptAsync(negotiation, null) is { } confirmed
                   ? $"the funding transaction {confirmed} already has a confirmation"
                   : null;
    }

    /// <summary>
    /// A fully signed attempt of the open that has a confirmation (its funding watch has a first-seen height), or null.
    /// From then on no other attempt can confirm: they all double-spend it (IT-RBF-01).
    /// </summary>
    private async Task<TxId?> GetConfirmedAttemptAsync(DualFundNegotiation negotiation, IUnitOfWork? unitOfWork)
    {
        if (negotiation.CompletedTxIds.Count == 0)
            return null;

        using var scope = unitOfWork is null ? _serviceProvider.CreateScope() : null;
        var watches = (unitOfWork ?? scope!.ServiceProvider.GetRequiredService<IUnitOfWork>())
           .WatchedTransactionDbRepository;
        foreach (var txId in negotiation.CompletedTxIds.ToList())
        {
            if (await watches.GetByTransactionIdAsync(txId) is { FirstSeenAtHeight: not null })
                return txId;
        }

        return null;
    }

    /// <summary>The reason an RBF attempt is abandoned once an earlier attempt confirmed (NL-867).</summary>
    private static string EarlierAttemptConfirmed(TxId confirmed) => $"an earlier attempt {confirmed} confirmed";

    /// <summary>
    /// The host's check before our <c>tx_signatures</c> (NL-867): an RBF attempt whose earlier attempt confirmed can
    /// never confirm, so it is abandoned (<c>tx_abort</c>) instead of signed. Null for a first attempt, or when no
    /// earlier attempt has a confirmation.
    /// </summary>
    internal async Task<string?> GetTxSignaturesRefusalAsync(DualFundNegotiation negotiation) =>
        await GetConfirmedAttemptAsync(negotiation, null) is { } confirmed ? EarlierAttemptConfirmed(confirmed) : null;

    /// <summary>
    /// A block was processed (called by <c>ChannelManager</c> for every block, NL-867): every open with an RBF attempt
    /// running (negotiating, or our <c>tx_init_rbf</c> waiting for its answer) whose earlier attempt now has a
    /// confirmation abandons it at once (BOLT 2: "If the previous transaction confirms in the middle of an RBF attempt,
    /// the attempt MUST be abandoned"), each under its channel's lock and off the caller's thread, not only at the
    /// funding depth (<see cref="OnFundingConfirmedAsync"/>): our <c>tx_abort</c> goes out unless our
    /// <c>tx_signatures</c> did (IT-ABT-01), the reservation and the sale slot go back, the attempt's purchase is
    /// replaced and a waiting <c>bumpopen</c> returns.
    /// </summary>
    public void ScheduleConfirmedAttemptRound()
    {
        if (Volatile.Read(ref _disposed) != 0 || _serviceProvider.GetService<IInteractiveTxDriver>() is not { } driver)
            return;

        foreach (var negotiation in _negotiations.Values.Distinct().ToList())
        {
            if (negotiation.CompletedTxIds.Count == 0
             || driver.GetInfo(negotiation.ChannelId) is not { } info
             || info is { SessionId: null, RbfRequested: false })
                continue;

            RunAfterLock(negotiation.ChannelId, async () =>
            {
                if (_negotiations.TryGetValue(negotiation.ChannelId, out var current) && current == negotiation)
                    _serviceProvider.GetService<IChannelMessagePublisher>()
                                   ?.Publish(negotiation.Peer, await AbandonRbfAttemptLockedAsync(negotiation, null));
            });
        }
    }

    /// <summary>
    /// <c>channel_reestablish</c> of a dual-funded open (NL-867; called by the reestablish handler under the channel's
    /// lock, before it plans): an RBF attempt that is not signed and whose earlier attempt confirmed is abandoned now,
    /// so the peer's <c>next_funding</c> for it is answered with our <c>tx_abort</c> (returned, to go out after our
    /// <c>channel_reestablish</c>) and never with its <c>commitment_signed</c> again. Empty when nothing was abandoned.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> AbandonRbfAttemptIfConfirmedAsync(ChannelModel channel,
                                                                                       IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned }
         || await GetOrLoadAsync(channel.ChannelId, unitOfWork, CancellationToken.None) is not { } negotiation)
            return [];

        return await AbandonRbfAttemptLockedAsync(negotiation, unitOfWork);
    }

    /// <summary>
    /// Under the channel's lock: the running RBF attempt (or our unanswered <c>tx_init_rbf</c>) is abandoned when an
    /// earlier attempt confirmed; returns our <c>tx_abort</c> (to publish), empty when nothing was abandoned. After our
    /// <c>tx_signatures</c> nothing is (IT-ABT-01): the attempt double-spends the confirmed one and never confirms.
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> AbandonRbfAttemptLockedAsync(DualFundNegotiation negotiation,
                                                                                 IUnitOfWork? unitOfWork)
    {
        var driver = _serviceProvider.GetService<IInteractiveTxDriver>();
        if (driver?.GetInfo(negotiation.ChannelId) is not { } info || info is { SessionId: null, RbfRequested: false }
         || info.State is InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed
         || await GetConfirmedAttemptAsync(negotiation, unitOfWork) is not { } confirmed)
            return [];

        var reason = EarlierAttemptConfirmed(confirmed);
        _logger.LogInformation("Abandoning the RBF attempt of channel {ChannelId}: {Reason}", negotiation.ChannelId,
                               reason);
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var messages = await driver.AbortAsync(negotiation.ChannelId, reason,
                                                   unitOfWork ?? scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            negotiation.RestoreShares();
            return messages;
        }
        catch (InvalidOperationException e)
        {
            // Our tx_signatures went out meanwhile (IT-ABT-01)
            _logger.LogWarning(e, "Could not abandon the RBF attempt of channel {ChannelId}", negotiation.ChannelId);
            return [];
        }
    }

    /// <summary>
    /// A peer's <c>tx_init_rbf</c> for the open (the driver checked the IT-RBF-01 feerate floor), unless
    /// <see cref="GetRbfRefusalAsync"/> refuses it. BOLT 2: the opener "MAY set <c>funding_output_contribution</c> to a
    /// different value" (NL-521): its new contribution is checked (<see cref="GetRbfShareViolation"/>) and the attempt's
    /// funding output, balances and reserve follow it. Our share: the previous one with our inputs re-added and our
    /// change lowered for the new feerate; when we did not contribute before but our policy wants to
    /// (<see cref="DualFundingOptions.AcceptContributionSat"/>, e.g. the wallet was empty at the open), a fresh
    /// contribution, or still none when the wallet cannot fund it.
    /// </summary>
    internal async Task<InteractiveTxRbfDecision> DecideRbfAsync(DualFundNegotiation negotiation,
                                                                 TxInitRbfMessage message)
    {
        // NL-592: a node draining for its shutdown accepts no RBF of a dual-funded open either (BOLT 2: MAY answer
        // tx_abort for any reason); our own bumpopen is refused at the IPC router
        if (IsDraining())
            return InteractiveTxRbfDecision.Reject(NodeDrain.Refusal("tx_init_rbf"));

        if (await GetRbfRefusalAsync(negotiation) is { } refusal)
            return InteractiveTxRbfDecision.Reject(refusal);

        // No attempt runs (the driver checked it): shares a refused attempt took are dropped
        negotiation.RestoreShares();
        negotiation.AttemptLiquidity = null;
        negotiation.LiquidityRequest = null;

        // funding_output_contribution is an s64 in satoshis (SP1-A); a dual-funded open's share is never negative
        var theirs = message.FundingOutputContributionTlv?.Satoshis ?? 0L;
        var feerate = message.Payload.Feerate;

        // Liquidity ads (NL-850): a request_funding makes us the seller of this attempt (re-validated and re-signed for
        // every attempt); BOLT PR #1153: an RBF of an attempt with a purchase that drops request_funding MUST fail
        if (message.RequestFundingTlv is { Request: var requestFunding })
            return await DecideLiquidityRbfAsync(negotiation, message, requestFunding, theirs);
        if (negotiation.LatestSignedPurchase is not null)
            return InteractiveTxRbfDecision.Reject(
                "InvalidRbfMissingLiquidityPurchase: the attempt to replace bought liquidity and tx_init_rbf does not "
              + "request it again");

        var localShare = negotiation.LocalShare;
        if (localShare.IsZero && theirs >= 0)
            localShare = GetAcceptContribution(LightningMoney.Satoshis(theirs));
        if (GetRbfShareViolation(negotiation, localShare, theirs) is { } violation)
            return InteractiveTxRbfDecision.Reject(violation);

        var contribution = InteractiveTxContribution.Empty;
        if (!negotiation.LocalShare.IsZero)
        {
            if (negotiation.LastContribution is not { } previous)
                return InteractiveTxRbfDecision.Reject("our previous contribution is unknown");

            var rebuilt = DualFundingRules.RebuildContributionForFeerate(
                previous, negotiation.LocalShare, false, GetSharedFunding(negotiation), feerate,
                LightningMoney.Satoshis(ChangeDustLimitSat));
            if (rebuilt is null)
                return InteractiveTxRbfDecision.Reject($"our inputs cannot pay {feerate} sat/kw");
            contribution = rebuilt;
        }
        else if (!localShare.IsZero)
        {
            // Nothing of ours is in the earlier attempts, so nothing must be double-spent: a new contribution
            try
            {
                contribution = await GetContributor().ContributeAsync(
                                   new InteractiveTxContributionRequest(negotiation.ChannelId,
                                                                        InteractiveTxPurpose.DualFundRbf, localShare,
                                                                        [], feerate, 0,
                                                                        message.RequireConfirmedInputsTlv is not null));
            }
            catch (InsufficientFundsException e)
            {
                _logger.LogWarning("Cannot contribute {Amount} to the RBF of {ChannelId} ({Reason}); accepting it "
                                 + "without our funds", localShare, negotiation.ChannelId, e.Message);
                localShare = LightningMoney.Zero;
                if (GetRbfShareViolation(negotiation, localShare, theirs) is { } withoutUs)
                    return InteractiveTxRbfDecision.Reject(withoutUs);
            }
        }

        ChangeSharesForRbf(negotiation, localShare, LightningMoney.Satoshis(theirs));
        var terms = CreateTerms(negotiation, false, feerate, message.Payload.Locktime, contribution: contribution);
        _logger.LogInformation("Accepting the RBF of channel {ChannelId} at {Feerate} sat/kw: {Remote} + our {Local}",
                               negotiation.ChannelId, feerate, negotiation.RemoteShare, negotiation.LocalShare);
        return InteractiveTxRbfDecision.Accept(terms, negotiation.LocalShare);
    }

    /// <summary>
    /// A peer's <c>tx_init_rbf</c> that buys liquidity from us (NL-850): checked like the open's request (our rates, the
    /// griefing caps, the buyer's share paying the fee and, as opener, the first commitment's fee), then our share is
    /// exactly the requested amount, from the inputs of our earlier contribution (IT-RBF-01) or, when we contributed
    /// nothing before, from fresh wallet inputs; a sale the wallet cannot fund is refused (<c>tx_abort</c>), never
    /// accepted without our funds. Our <c>will_fund</c> is signed again over the funding script.
    /// </summary>
    private async Task<InteractiveTxRbfDecision> DecideLiquidityRbfAsync(DualFundNegotiation negotiation,
                                                                         TxInitRbfMessage message,
                                                                         RequestFunding request, long theirs)
    {
        if (GetLiquidityAds() is not { } liquidityAds)
            return InteractiveTxRbfDecision.Reject("we do not sell liquidity");
        if (liquidityAds.TryStartSale(negotiation.Peer, request, out var sale) is { } refusal)
        {
            _logger.LogInformation("Refusing the liquidity request of {Peer} in the RBF of {ChannelId}: {Reason}",
                                   negotiation.Peer, negotiation.ChannelId, refusal);
            return InteractiveTxRbfDecision.Reject($"liquidity ads: {refusal}");
        }

        try
        {
            var channel = negotiation.Channel!;
            var feerate = message.Payload.Feerate;
            var localShare = LightningMoney.Satoshis(request.RequestedSat);
            var remoteShare = LightningMoney.Satoshis(Math.Max(theirs, 0));
            if (GetRbfShareViolation(negotiation, localShare, theirs) is { } violation)
                return InteractiveTxRbfDecision.Reject(violation);

            var fees = LiquidityAdsRules.ComputeFees(request.Rate, feerate, request.RequestedSat, request.RequestedSat,
                                                     true);
            var channelParams = channel.ChannelParams;
            if (DualFundLiquidity.GetBalanceViolation(localShare, remoteShare, -checked((long)fees.TotalMsat),
                                                      negotiation.IsOpener,
                                                      CommitmentFeeCalculator.FunderCost(
                                                          (ulong)channelParams.FeeRateAmountPerKw.Satoshi,
                                                          channelParams.OptionAnchorOutputs, 0))
                is { } balanceViolation)
                return InteractiveTxRbfDecision.Reject($"liquidity ads: {balanceViolation}");

            InteractiveTxContribution contribution;
            if (!negotiation.LocalShare.IsZero)
            {
                if (negotiation.LastContribution is not { } previous)
                    return InteractiveTxRbfDecision.Reject("our previous contribution is unknown");

                // Our earlier inputs again (the new attempt must double-spend the earlier ones), now paying the amount
                // sold at the new feerate
                var rebuilt = DualFundingRules.RebuildContributionForFeerate(
                    previous, localShare, false, GetSharedFunding(negotiation), feerate,
                    LightningMoney.Satoshis(ChangeDustLimitSat));
                if (rebuilt is null)
                    return InteractiveTxRbfDecision.Reject(
                        $"liquidity ads: our inputs cannot fund {request.RequestedSat} sat at {feerate} sat/kw");
                contribution = rebuilt;
            }
            else
            {
                try
                {
                    contribution = await GetContributor().ContributeAsync(
                                       new InteractiveTxContributionRequest(negotiation.ChannelId,
                                                                            InteractiveTxPurpose.DualFundRbf,
                                                                            localShare, [], feerate, 0,
                                                                            message.RequireConfirmedInputsTlv
                                                                                is not null));
                }
                catch (InsufficientFundsException e)
                {
                    return InteractiveTxRbfDecision.Reject(
                        $"liquidity ads: cannot fund the requested {request.RequestedSat} sat: {e.Message}");
                }
            }

            ChangeSharesForRbf(negotiation, localShare, remoteShare);
            var willFund = liquidityAds.CreateWillFund(request.Rate, GetFundingScript(channel));
            negotiation.AttemptLiquidity = new DualFundLiquidity(LiquidityPurchaseRole.Seller, request, willFund, fees,
                                                                 request.RequestedSat);
            negotiation.EndSale();
            negotiation.Sale = sale;
            sale = null;
            var terms = CreateTerms(negotiation, false, feerate, message.Payload.Locktime, contribution: contribution);
            _logger.LogInformation("Selling {Amount} sat of liquidity to {Peer} in the RBF of {ChannelId} at {Feerate} "
                                 + "sat/kw for {Fee} sat", request.RequestedSat, negotiation.Peer,
                                   negotiation.ChannelId, feerate, fees.TotalSat);
            return InteractiveTxRbfDecision.Accept(terms, negotiation.LocalShare, willFund);
        }
        finally
        {
            sale?.Dispose();
        }
    }

    /// <summary>
    /// The peer's <c>tx_ack_rbf</c> to our <c>tx_init_rbf</c> (driver callback, under the channel's lock, before the
    /// attempt is created): BOLT 2 lets the accepter change its <c>funding_output_contribution</c> (NL-521), so the
    /// attempt's funding output is built from the new value, checked by <see cref="GetRbfShareViolation"/>. Returns the
    /// <c>tx_abort</c> reason when it is refused, else null.
    /// </summary>
    internal string? OnRbfAcknowledged(DualFundNegotiation negotiation, TxAckRbfMessage message)
    {
        var theirs = message.FundingOutputContributionTlv?.Satoshis ?? 0L;
        if (GetRbfShareViolation(negotiation, negotiation.LocalShare, theirs) is { } violation)
        {
            _logger.LogWarning("Refusing the tx_ack_rbf of channel {ChannelId}: {Reason}", negotiation.ChannelId,
                               violation);
            return violation;
        }

        // Liquidity ads (NL-850): the seller's answer to our request_funding, checked as at the open; a missing or
        // invalid one is our tx_abort
        negotiation.AttemptLiquidity = null;
        if (negotiation.LiquidityRequest is { } liquidityRequest)
        {
            negotiation.LiquidityRequest = null;
            var channel = negotiation.Channel!;
            var check = CheckWillFund(negotiation, liquidityRequest, message.ProvideFundingTlv?.WillFund,
                                      GetFundingScript(channel), negotiation.LocalShare,
                                      LightningMoney.Satoshis(theirs),
                                      (uint)channel.ChannelParams.FeeRateAmountPerKw.Satoshi,
                                      channel.ChannelParams.OptionAnchorOutputs);
            if (check.Refusal is { } refusal)
            {
                _logger.LogWarning("Refusing the tx_ack_rbf of channel {ChannelId}: {Reason}", negotiation.ChannelId,
                                   refusal);
                return refusal;
            }

            negotiation.AttemptLiquidity = check.Liquidity;
        }
        else if (message.ProvideFundingTlv is not null)
        {
            _logger.LogWarning("tx_ack_rbf of channel {ChannelId} carries provide_funding we did not ask for; ignoring "
                             + "it", negotiation.ChannelId);
        }

        ChangeSharesForRbf(negotiation, negotiation.LocalShare, LightningMoney.Satoshis(theirs));
        return null;
    }

    /// <summary>
    /// Why an RBF attempt with <paramref name="localShare"/> and the peer's <paramref name="remoteSatoshis"/> is
    /// refused, or null (NL-521): a negative contribution (an s64 on the wire, but a v2 open's share is never negative),
    /// more than all bitcoin, a funding output at or below the dust limit, an opener's share that cannot pay the first
    /// commitment's fee (and anchors) at the channel's feerate (BOLT 2 open_channel), or a large channel without
    /// <c>option_support_large_channel</c>.
    /// </summary>
    private string? GetRbfShareViolation(DualFundNegotiation negotiation, LightningMoney localShare,
                                         long remoteSatoshis)
    {
        if (remoteSatoshis < 0)
            return $"funding_output_contribution {remoteSatoshis} sat is negative: a dual-funded open's contribution "
                 + "cannot be";
        if (remoteSatoshis > MaxMoneySatoshis)
            return $"funding_output_contribution {remoteSatoshis} sat is more than all bitcoin";

        var channel = negotiation.Channel ?? throw new InvalidOperationException("The channel is not known yet");
        var remoteShare = LightningMoney.Satoshis(remoteSatoshis);
        var total = LightningMoney.MilliSatoshis(localShare.MilliSatoshi + remoteShare.MilliSatoshi);
        var channelParams = channel.ChannelParams;
        var dustLimit = Max(channelParams.Local.DustLimitAmount, channelParams.Remote.DustLimitAmount);
        if (total <= dustLimit)
            return $"the funding output of {total} would be at or below the dust limit {dustLimit}";

        var openerShare = negotiation.IsOpener ? localShare : remoteShare;
        var fee = CommitmentFeeCalculator.FunderCost((ulong)channelParams.FeeRateAmountPerKw.Satoshi,
                                                     channelParams.OptionAnchorOutputs, 0);
        if (openerShare < fee)
            return $"the opener's contribution {openerShare} cannot pay the first commitment's fee {fee}";

        if (total >= Domain.Channels.Constants.ChannelConstants.LargeChannelAmount
         && GetNegotiatedFeatures(negotiation.Peer).LargeChannels == FeatureSupport.No)
            return $"a funding output of {total} is a large channel, which is not negotiated";

        return null;
    }

    private void ChangeSharesForRbf(DualFundNegotiation negotiation, LightningMoney localShare,
                                    LightningMoney remoteShare)
    {
        if (localShare == negotiation.LocalShare && remoteShare == negotiation.RemoteShare)
            return;

        _logger.LogInformation("RBF of channel {ChannelId} changes the contributions: ours {OldLocal} -> {Local}, the "
                             + "peer's {OldRemote} -> {Remote}", negotiation.ChannelId, negotiation.LocalShare,
                               localShare, negotiation.RemoteShare, remoteShare);
        negotiation.ChangeShares(localShare, remoteShare);
    }

    #endregion

    #region commitment_signed and restart

    /// <summary>
    /// The peer's <c>commitment_signed</c> for the first commitment of a dual-funded open (routed here by
    /// <c>ChannelManager</c> under the channel's lock): verified against our first commitment, stored with the channel
    /// in the driver's save, then our <c>tx_signatures</c> follow in the IT-SIG-01 order. Null when the channel has no
    /// dual-funded negotiation waiting for it (normal operation handles the message).
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>?> TryHandleCommitmentSignedAsync(
        CommitmentSignedMessage message, CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channelId = message.Payload.ChannelId;
        var negotiation = await GetOrLoadAsync(channelId, unitOfWork, cancellationToken);
        if (negotiation?.Channel is not { State: ChannelState.V1FundingSigned } channel
         || negotiation.Peer != peerPubKey)
            return null;

        if (negotiation.PendingTxId is not { } pendingTxId)
        {
            // A retransmission for an attempt that is already fully signed
            _logger.LogDebug("Ignoring commitment_signed on channel {ChannelId}: no attempt waits for one",
                             channelId);
            return [];
        }

        if (negotiation.CommitmentSignedReceived)
        {
            _logger.LogDebug("Ignoring a repeated commitment_signed on channel {ChannelId}", channelId);
            return [];
        }

        var driver = GetDriver();
        var violation = DualFundingRules.CheckFirstCommitmentSigned(message.Payload.HtlcSignatures.Count());
        if (violation is null && message.FundingTxIdTlv is { } fundingTxIdTlv
                              && !fundingTxIdTlv.FundingTxId.Equals(pendingTxId))
            violation = $"commitment_signed for funding {fundingTxIdTlv.FundingTxId}, not {pendingTxId}";

        if (violation is null)
        {
            var localCommitment = BuildCommitment(channel, CommitmentSide.Local);
            try
            {
                // An RBF attempt is a pending funding of the signer until it completes (its capacity may differ)
                if (negotiation.CompletedTxIds.Count > 0)
                    _lightningSigner.ValidateSignature(channelId, pendingTxId, message.Payload.Signature,
                                                       localCommitment);
                else
                    _lightningSigner.ValidateSignature(channelId, message.Payload.Signature, localCommitment);
            }
            catch (SignerException e)
            {
                violation = $"invalid commitment signature: {e.Message}";
            }
        }

        if (violation is not null)
        {
            _logger.LogWarning("commitment_signed of the dual-funded open {ChannelId} refused: {Reason}", channelId,
                               violation);
            return await driver.AbortAsync(channelId, violation, unitOfWork, cancellationToken);
        }

        var previousSignature = channel.LastReceivedSignature;
        channel.UpdateLastReceivedSignature(message.Payload.Signature);
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        try
        {
            // Stored with the attempt (NL-528): whichever signed attempt confirms can still be closed unilaterally
            var replies = await driver.OnCommitmentSignedReceivedAsync(channelId, unitOfWork, cancellationToken,
                                                                       message.Payload.Signature);

            // The driver abandons an attempt it cannot sign for (NL-867): nothing is pending then
            negotiation.CommitmentSignedReceived = negotiation.PendingTxId is not null;
            return replies;
        }
        catch
        {
            if (previousSignature is not null)
                channel.UpdateLastReceivedSignature(previousSignature);
            throw;
        }
    }

    /// <summary>
    /// The channel's dual-funded negotiation, rebuilt after a restart from the channel (a <see cref="ChannelVersion.V2"/>
    /// channel still in <see cref="ChannelState.V1FundingSigned"/>) and its stored interactive-tx rows, which are
    /// resumed in the driver. Null when the channel has none. Call it under the channel's lock.
    /// </summary>
    internal async Task<DualFundNegotiation?> GetOrLoadAsync(ChannelId channelId, IUnitOfWork? unitOfWork,
                                                             CancellationToken cancellationToken)
    {
        if (_negotiations.TryGetValue(channelId, out var known))
            return known;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned })
            return null;

        IReadOnlyList<InteractiveTxSessionModel> sessions;
        IReadOnlyList<LiquidityPurchaseModel> purchases;
        if (unitOfWork is not null)
        {
            sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
            purchases = await GetOpenPurchasesAsync(unitOfWork, channelId);
        }
        else
        {
            using var scope = _serviceProvider.CreateScope();
            var scoped = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            sessions = await scoped.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
            purchases = await GetOpenPurchasesAsync(scoped, channelId);
        }

        var stored = sessions.Where(s => s.State != InteractiveTxSessionState.Aborted && s.ConstructedTx is not null)
                             .OrderBy(s => s.CreatedAt)
                             .ToList();
        if (stored.Count == 0)
            return null;

        var latest = stored[^1];

        // The channel row's balances are its funding attempt's shares with that attempt's liquidity fee moved (NL-850)
        var purchasesByTxId = purchases.ToDictionary(p => p.FundingTxId);
        var (localShare, remoteShare) = DualFundLiquidity.RemoveFee(
            channel.LocalBalance, channel.RemoteBalance,
            DualFundLiquidity.GetLocalFeeMsat(channel.FundingOutput?.TransactionId is { } fundingTxId
                                                  ? purchasesByTxId.GetValueOrDefault(fundingTxId)
                                                  : null));
        var negotiation = new DualFundNegotiation(channelId, channelId, channel.RemoteNodeId, channel.IsInitiator)
        {
            Channel = channel,
            LocalShare = localShare,
            RemoteShare = remoteShare,
            FundingFeeratePerKw = latest.FeeratePerKw,
            Locktime = latest.Locktime,
            LastContribution = latest.LocalContribution,
            LastFeeratePerKw = latest.FeeratePerKw
        };
        foreach (var purchase in purchasesByTxId)
            negotiation.Purchases[purchase.Key] = purchase.Value;
        negotiation.Host = new DualFundHost(this, negotiation);
        foreach (var signed in stored.Where(s => s.State == InteractiveTxSessionState.Signed))
            negotiation.CompletedTxIds.Add(signed.ConstructedTx!.TxId);
        if (latest.State != InteractiveTxSessionState.Signed)
        {
            negotiation.PendingTxId = latest.ConstructedTx!.TxId;
            negotiation.CommitmentSignedReceived = latest.CommitmentSignedReceived;

            // A restart during an RBF attempt (NL-528): the channel row carries the unsigned attempt since our
            // commitment_signed; the last fully signed one is rebuilt from its row, so an attempt that ends unsigned
            // puts the channel back on it (RestoreLastSignedFundingAsync), as without the restart
            if (stored.LastOrDefault(x => x.State == InteractiveTxSessionState.Signed) is { } signedSession
             && TryGetSignedAttempt(signedSession) is { } signed
             && channel.FundingOutput?.TransactionId != signed.TxId)
            {
                var (signedLocal, signedRemote) = DualFundLiquidity.ApplyFee(
                    signed.LocalShare, signed.RemoteShare, negotiation.GetLocalLiquidityFeeMsat(signed.TxId));
                negotiation.LastSignedFunding = new DualFundNegotiation.SignedFunding(
                    signed.TxId, signed.Index, signed.Capacity, signedLocal, signedRemote,
                    WithCapacity(channel.ChannelParams, signed.Capacity), null, signed.TheirSignature);
                negotiation.SharesBeforeRbf = (signed.LocalShare, signed.RemoteShare);
            }
        }

        var driver = GetDriver();
        if (driver.GetInfo(channelId) is null)
        {
            var terms = CreateTerms(negotiation, latest.IsInitiator, latest.FeeratePerKw, latest.Locktime,
                                    contribution: latest.LocalContribution);
            await driver.ResumeAsync(latest, terms, negotiation.Host, cancellationToken,
                                     stored.Where(s => s.SessionId != latest.SessionId).ToList());
        }

        _negotiations[channelId] = negotiation;
        _logger.LogInformation("Resumed the dual-funded open of channel {ChannelId} (funding {TxId})", channelId,
                               latest.ConstructedTx!.TxId);
        return negotiation;
    }

    /// <summary>
    /// A funding transaction of the channel's dual-funded open reached its depth (called by <c>ChannelManager</c> under
    /// the channel's lock, before the confirmation is applied; NL-528). BOLT 2: "If the previous transaction confirms
    /// in the middle of an RBF attempt, the attempt MUST be abandoned", so an attempt in progress is aborted (unless our
    /// <c>tx_signatures</c> went out, IT-ABT-01: it then double-spends the confirmed one and never confirms). Any fully
    /// signed attempt may be the one that confirms, not only the latest: the channel then moves to it (its outpoint,
    /// capacity, balances, reserve and in-flight limit, the peer's signature of our first commitment stored with its
    /// negotiation, the signer's current funding, its broadcast row pending and the other attempts' rows replaced),
    /// persisted in its own save. False (logged) when <paramref name="confirmedTxId"/> is no signed attempt we can
    /// follow; the confirmation must then be ignored.
    /// </summary>
    public async Task<bool> OnFundingConfirmedAsync(ChannelModel channel, TxId confirmedTxId, IUnitOfWork unitOfWork,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned })
            return channel.FundingOutput?.TransactionId == confirmedTxId;

        var negotiation = await GetOrLoadAsync(channel.ChannelId, unitOfWork, cancellationToken);
        if (negotiation is not null)
        {
            // Nothing is started once an attempt has a confirmation (GetRbfRefusalAsync); one that was already running
            // is abandoned now, which also puts the channel back on the last signed attempt (OnAbortedAsync)
            await AbortNegotiationLockedAsync(negotiation, $"funding {confirmedTxId} confirmed");
            negotiation.RestoreShares();
        }

        if (channel.FundingOutput?.TransactionId == confirmedTxId)
            return true;

        var sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channel.ChannelId);
        var (confirmedSession, confirmedAttempt) = sessions.Where(x => x.ConstructedTx?.TxId == confirmedTxId)
                                                           .Select(x => (x, TryGetSignedAttempt(x)))
                                                           .LastOrDefault(x => x.Item2 is not null);
        if (confirmedAttempt is not { } attempt)
        {
            _logger.LogCritical("Funding {TxId} of the dual-funded channel {ChannelId} confirmed, but it is not a fully "
                              + "signed attempt with the peer's commitment signature stored; the channel stays on "
                              + "{Funding}", confirmedTxId, channel.ChannelId, channel.FundingOutput?.TransactionId);
            return false;
        }

        var previous = channel.FundingOutput!.TransactionId;
        var funding = channel.FundingOutput;

        // The confirmed attempt's liquidity fee moves its balances as it did when it was signed (NL-850)
        var purchase = negotiation?.Purchases.GetValueOrDefault(confirmedTxId)
                    ?? (await GetOpenPurchasesAsync(unitOfWork, channel.ChannelId))
                      .FirstOrDefault(p => p.FundingTxId == confirmedTxId);
        var (localBalance, remoteBalance) = DualFundLiquidity.ApplyFee(attempt.LocalShare, attempt.RemoteShare,
                                                                       DualFundLiquidity.GetLocalFeeMsat(purchase));
        channel.ReplaceUnconfirmedFunding(
            new FundingOutputInfo(attempt.Capacity, funding.LocalFundingPubKey, funding.RemoteFundingPubKey, attempt.TxId,
                                  attempt.Index), localBalance, remoteBalance,
            WithCapacity(channel.ChannelParams, attempt.Capacity));
        MoveSignerToFunding(channel, confirmedSession);
        channel.UpdateLastReceivedSignature(attempt.TheirSignature);
        channel.UpdateLastSentSignature(_lightningSigner.SignChannelTransaction(
                                            channel.ChannelId, BuildCommitment(channel, CommitmentSide.Remote)));

        // The confirmed attempt is the channel's funding broadcast again; every other attempt now double-spends it
        foreach (var other in sessions.Where(x => x.State == InteractiveTxSessionState.Signed
                                              && x.ConstructedTx is not null
                                              && x.ConstructedTx.TxId != confirmedTxId))
            await unitOfWork.BroadcastTransactionDbRepository.MarkReplacedAsync(other.ConstructedTx!.TxId);
        await unitOfWork.BroadcastTransactionDbRepository.MarkPendingAsync(confirmedTxId);
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();

        if (negotiation is not null)
        {
            negotiation.LocalShare = attempt.LocalShare;
            negotiation.RemoteShare = attempt.RemoteShare;
            negotiation.LastSignedFunding = null;
            negotiation.SharesBeforeRbf = null;
        }

        _channelMemoryRepository.UpdateChannel(channel);
        _logger.LogWarning("Dual-funded channel {ChannelId}: its earlier attempt {TxId} confirmed instead of {Previous}; "
                         + "the channel follows it (capacity {Capacity}, our share {Local})", channel.ChannelId,
                           confirmedTxId, previous, attempt.Capacity, attempt.LocalShare);
        return true;
    }

    /// <summary>
    /// The peer's <c>channel_ready</c> of a dual-funded channel that is still waiting for its funding and has several
    /// fully signed attempts (NL-528; called under the channel's lock): it says the peer saw one of them reach its depth
    /// but not which, and the channel's first commitment state is built on the funding we are on. It is kept until our
    /// own confirmation tells which attempt confirmed (<see cref="TakeDeferredChannelReady"/>); after a restart the peer
    /// sends it again with <c>channel_reestablish</c>. False when the channel has one attempt at most (nothing to defer).
    /// </summary>
    public async Task<bool> TryDeferChannelReadyAsync(ChannelReadyMessage message, FeatureOptions negotiatedFeatures,
                                                      IUnitOfWork unitOfWork,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        var channelId = message.Payload.ChannelId;
        if (await GetOrLoadAsync(channelId, unitOfWork, cancellationToken) is not { } negotiation)
            return false;

        // BOLT 2: "If an RBF negotiation is in progress when a channel_ready message is exchanged, the negotiation must
        // be abandoned" (which also puts the channel back on the last signed attempt)
        await AbortNegotiationLockedAsync(negotiation, "channel_ready received");
        negotiation.RestoreShares();
        if (negotiation.CompletedTxIds.Count < 2)
            return false;

        _deferredChannelReady[channelId] = (message, negotiatedFeatures);
        _logger.LogInformation("channel_ready of the dual-funded channel {ChannelId} arrived before our confirmation of "
                             + "one of its RBF attempts; applying it once we see which one confirmed", channelId);
        return true;
    }

    /// <summary>
    /// The <c>channel_ready</c> <see cref="TryDeferChannelReadyAsync"/> kept for the channel, with the features
    /// negotiated when it arrived, removed; null when none was kept.
    /// </summary>
    public (ChannelReadyMessage Message, FeatureOptions Features)? TakeDeferredChannelReady(ChannelId channelId) =>
        _deferredChannelReady.TryRemove(channelId, out var deferred) ? deferred : null;

    /// <summary>
    /// The fully signed funding transactions of a dual-funded open that are stored with the peer's signature of our
    /// first commitment (NL-528), oldest first: each one may be the one that confirms.
    /// </summary>
    public static IReadOnlyList<TxId> GetFollowableFundingTxIds(IEnumerable<InteractiveTxSessionModel> sessions) =>
        sessions.Where(x => x.Purpose is InteractiveTxPurpose.DualFund or InteractiveTxPurpose.DualFundRbf)
                .Select(TryGetSignedAttempt)
                .OfType<SignedAttempt>()
                .Select(a => a.TxId)
                .Distinct()
                .ToList();

    /// <summary>
    /// A fully signed attempt as its stored negotiation describes it: outpoint, capacity, both shares and the peer's
    /// signature of our first commitment; null when the row lacks any of them (not signed, or stored before
    /// migration <c>AddDualFundAttempts</c>).
    /// </summary>
    private static SignedAttempt? TryGetSignedAttempt(InteractiveTxSessionModel session)
    {
        if (session is not
            {
                State: InteractiveTxSessionState.Signed, LocalFundingSatoshis: { } localSatoshis,
                TheirCommitmentSignature: { } signature, ConstructedTx.SharedOutputIndex: { } index
            })
            return null;

        var capacity = session.ConstructedTx.Outputs[(int)index].Amount;
        var local = LightningMoney.Satoshis(localSatoshis);
        if (localSatoshis < 0 || local > capacity)
            return null;

        return new SignedAttempt(session.ConstructedTx.TxId, checked((ushort)index), capacity, local,
                                 LightningMoney.MilliSatoshis(capacity.MilliSatoshi - local.MilliSatoshi), signature);
    }

    /// <summary>
    /// The liquidity purchases of the channel's open and its RBF attempts (NL-850); none when the unit of work has no
    /// purchase table (a build or test double without it).
    /// </summary>
    private static async Task<IReadOnlyList<LiquidityPurchaseModel>> GetOpenPurchasesAsync(IUnitOfWork unitOfWork,
                                                                                          ChannelId channelId)
    {
        try
        {
            if (unitOfWork.LiquidityPurchaseDbRepository is not { } repository)
                return [];

            var purchases = await repository.GetByChannelIdAsync(channelId) ?? [];
            return purchases.Where(p => p.Kind is LiquidityPurchaseKind.ChannelOpen or LiquidityPurchaseKind.OpenRbf)
                            .ToList();
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>A fully signed attempt of the open (see <see cref="TryGetSignedAttempt"/>).</summary>
    private sealed record SignedAttempt(
        TxId TxId,
        ushort Index,
        LightningMoney Capacity,
        LightningMoney LocalShare,
        LightningMoney RemoteShare,
        CompactSignature TheirSignature);

    /// <summary>
    /// Our <c>commitment_signed</c> for the pending attempt again (BOLT 2 <c>next_funding</c> retransmission; RFC 6979
    /// makes it the same signature).
    /// </summary>
    internal CommitmentSignedMessage? CreateCommitmentSignedRetransmission(DualFundNegotiation negotiation)
    {
        if (negotiation is not { Channel: { } channel, PendingTxId: { } txId })
            return null;

        var remoteCommitment = BuildCommitment(channel, CommitmentSide.Remote);
        var signature = negotiation.CompletedTxIds.Count > 0
                            ? _lightningSigner.SignChannelTransaction(channel.ChannelId, txId, remoteCommitment)
                            : _lightningSigner.SignChannelTransaction(channel.ChannelId, remoteCommitment);
        return _messageFactory.CreateCommitmentSignedMessage(channel.ChannelId, signature, [], txId);
    }

    /// <summary>Forgets a channel's negotiation (memory only; the channel closed or was forgotten).</summary>
    internal bool Forget(ChannelId channelId) => _negotiations.TryRemove(channelId, out _);

    /// <summary>Whether a dual-funded open is known for <paramref name="channelId"/> (tests and diagnostics).</summary>
    public bool IsOpening(ChannelId channelId) => _negotiations.ContainsKey(channelId);

    /// <summary>The fully signed funding transactions of the channel's open, oldest first (tests and diagnostics).</summary>
    public IReadOnlyList<TxId> GetSignedFundingTxIds(ChannelId channelId) =>
        _negotiations.TryGetValue(channelId, out var negotiation) ? negotiation.CompletedTxIds.ToList() : [];

    /// <summary>Stops the accepter timeouts (idempotent: the container disposes the service under both registrations).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>Waits for the background work (the publish after a save) to finish (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (!_pending.IsEmpty)
            await Task.WhenAll(_pending.Keys.ToList());
    }

    #endregion

    #region Helpers

    private const long ChangeDustLimitSat = 546;

    /// <summary>21 million bitcoin in satoshis (a contribution above it is not an amount).</summary>
    private const long MaxMoneySatoshis = 2_100_000_000_000_000;

    private IInteractiveTxDriver GetDriver() =>
        _serviceProvider.GetService<IInteractiveTxDriver>()
     ?? throw new InvalidOperationException("No interactive-tx driver is registered");

    private IBlockchainMonitor? GetMonitor() => _serviceProvider.GetService<IBlockchainMonitor>();

    /// <summary>NL-591: a node draining for its shutdown opens nothing new.</summary>
    private bool IsDraining() => _serviceProvider.GetService<INodeDrainState>() is { IsDraining: true };

    private uint GetLocktime() => GetMonitor()?.LastProcessedBlockHeight ?? 0;

    private async Task<uint> EstimateFeerateAsync(CancellationToken cancellationToken) =>
        (uint)(await _feeService.GetFeeRatePerKwAsync(cancellationToken)).Satoshi;

    private InteractiveTxTerms CreateTerms(DualFundNegotiation negotiation, bool isInitiator, uint feeratePerKw,
                                           uint locktime, InteractiveTxContributionRequest? request = null,
                                           InteractiveTxContribution? contribution = null) =>
        new(negotiation.ChannelId, _lightningSigner.GetNodePublicKey(), negotiation.Peer, isInitiator, feeratePerKw,
            locktime, GetDustLimitSatoshis(negotiation), negotiation.LocalRequiresConfirmedInputs,
            negotiation.RemoteRequiresConfirmedInputs, request, contribution);

    /// <summary>
    /// The channel's negotiated dust limit for the interactive-tx <c>tx_add_output</c> check (NL-473): the larger of
    /// both sides' <c>dust_limit_satoshis</c>, since the funding output serves both commitments. Our own configured dust
    /// limit while the channel is not parametrized yet (the opener's placeholder), 0 for none.
    /// </summary>
    private ulong GetDustLimitSatoshis(DualFundNegotiation negotiation) =>
        negotiation.Channel is not { } channel
            ? (ulong)_nodeOptions.DustLimitAmount.Satoshi
            : (ulong)Math.Max(channel.ChannelParams.Local.DustLimitAmount.Satoshi,
                              channel.ChannelParams.Remote.DustLimitAmount.Satoshi);

    /// <summary>
    /// The values we announce. v2 has no reserve field: the reserve is fixed from both contributions
    /// (<see cref="DualFundingRules.GetChannelReserve"/>), set once they are known.
    /// </summary>
    private ChannelParty CreateLocalParams(LightningMoney reserve, LightningMoney maxHtlcValueInFlight) =>
        new(_nodeOptions.DustLimitAmount, reserve, _nodeOptions.HtlcMinimumAmount, _nodeOptions.MaxAcceptedHtlcs,
            maxHtlcValueInFlight, _nodeOptions.ToSelfDelay);

    /// <summary>
    /// The <c>max_htlc_value_in_flight_msat</c> we announce for a channel of <paramref name="capacity"/> (at the open):
    /// fixed for the channel's lifetime, so no cap when <c>option_splice</c> was negotiated (NL-880,
    /// <see cref="MaxHtlcValueInFlightRules"/>).
    /// </summary>
    private LightningMoney GetAnnouncedMaxHtlcValueInFlight(LightningMoney capacity, FeatureOptions features) =>
        MaxHtlcValueInFlightRules.GetAnnounced(_nodeOptions, capacity, features.OptionSplice > FeatureSupport.No);

    private static ChannelKeySetModel CreateLocalKeySet(uint keyIndex, ChannelBasepoints basepoints,
                                                        CompactPubKey firstPoint) =>
        new(keyIndex, basepoints.FundingPubKey, basepoints.RevocationBasepoint, basepoints.PaymentBasepoint,
            basepoints.DelayedPaymentBasepoint, basepoints.HtlcBasepoint, firstPoint);

    /// <summary>
    /// The channel of a dual-funded open: the funding output of both shares, and the first commitment's balances once
    /// a liquidity fee of <paramref name="localLiquidityFeeMsat"/> (msat, + we buy, − we sell; NL-850) moved from the
    /// buyer to the seller.
    /// </summary>
    private ChannelModel CreateChannel(ChannelParams channelParams, ChannelId channelId, ChannelKeySetModel localKeySet,
                                       ChannelKeySetModel remoteKeySet, CompactPubKey peer, bool isOpener,
                                       LightningMoney localShare, LightningMoney remoteShare,
                                       long localLiquidityFeeMsat = 0)
    {
        // BOLT 3: the obscuring factor is SHA256(opener's payment_basepoint || accepter's payment_basepoint)
        var commitmentNumber = isOpener
                                   ? new CommitmentNumber(localKeySet.PaymentCompactBasepoint,
                                                          remoteKeySet.PaymentCompactBasepoint, _sha256)
                                   : new CommitmentNumber(remoteKeySet.PaymentCompactBasepoint,
                                                          localKeySet.PaymentCompactBasepoint, _sha256);
        var total = LightningMoney.MilliSatoshis(localShare.MilliSatoshi + remoteShare.MilliSatoshi);
        var fundingOutput = new FundingOutputInfo(total, localKeySet.FundingCompactPubKey,
                                                  remoteKeySet.FundingCompactPubKey);
        var (localBalance, remoteBalance) = DualFundLiquidity.ApplyFee(localShare, remoteShare, localLiquidityFeeMsat);
        return new ChannelModel(channelParams, channelId, commitmentNumber, fundingOutput, isOpener, null, null,
                                localBalance, localKeySet, 0, 0, remoteBalance, remoteKeySet, 0, peer, 0,
                                ChannelState.V1Opening, ChannelVersion.V2);
    }

    /// <summary>The channel's P2WSH 2-of-2 funding script (BOLT 3).</summary>
    internal static BitcoinScript GetFundingScript(ChannelModel channel)
    {
        var funding = channel.FundingOutput ?? throw new InvalidOperationException("The channel has no funding output");
        return GetFundingScript(funding.Amount, funding.LocalFundingPubKey, funding.RemoteFundingPubKey);
    }

    /// <summary>The P2WSH 2-of-2 funding script of two funding keys (BOLT 3; the amount does not change it).</summary>
    private static BitcoinScript GetFundingScript(LightningMoney amount, CompactPubKey localFundingPubKey,
                                                  CompactPubKey remoteFundingPubKey)
    {
        var output = new FundingOutput(amount, new PubKey(localFundingPubKey), new PubKey(remoteFundingPubKey));
        return new BitcoinScript(output.ToTxOut().ScriptPubKey.ToBytes());
    }

    private LiquidityAdsService? GetLiquidityAds() => _serviceProvider.GetService<LiquidityAdsService>();

    /// <summary>
    /// The buyer's check of the seller's <c>provide_funding</c> (liquidity ads, NL-850; Eclair
    /// <c>validateRemoteFunding</c> and our fee limit), then of the balances it leaves: our share must pay the fee and
    /// the opener's balance the first commitment's fee. The purchase, or why it is refused.
    /// </summary>
    private LiquidityCheck CheckWillFund(DualFundNegotiation negotiation, DualFundLiquidityRequest request,
                                         WillFund? willFund, BitcoinScript fundingScript, LightningMoney localShare,
                                         LightningMoney sellerContribution, uint commitmentFeeratePerKw,
                                         bool optionAnchors)
    {
        if (GetLiquidityAds() is not { } liquidityAds)
            return new LiquidityCheck("liquidity ads are not available on this node", null);

        var contributedSat = (ulong)sellerContribution.Satoshi;
        var refusal = liquidityAds.ValidateWillFund(negotiation.Peer, request.Request, willFund, (byte[])fundingScript,
                                                    contributedSat, request.FundingFeeratePerKw, true,
                                                    request.MaxFeeSat, out var fees);
        if (refusal != LiquidityAdsRefusal.None)
            return new LiquidityCheck($"liquidity ads: {refusal}", null);

        var liquidity = new DualFundLiquidity(LiquidityPurchaseRole.Buyer, request.Request, willFund!, fees,
                                              contributedSat, request.MaxFeeSat);
        if (DualFundLiquidity.GetBalanceViolation(localShare, sellerContribution, liquidity.LocalFeeMsat,
                                                  negotiation.IsOpener,
                                                  CommitmentFeeCalculator.FunderCost(commitmentFeeratePerKw,
                                                                                     optionAnchors, 0))
            is { } violation)
            return new LiquidityCheck($"liquidity ads: {violation}", null);

        _logger.LogInformation("Buying {Amount} sat of inbound liquidity from {Seller} on channel {ChannelId} for "
                             + "{Fee} sat ({Mining} mining + {Service} service)", request.Request.RequestedSat,
                               negotiation.Peer, negotiation.ChannelId, fees.TotalSat, fees.MiningFeeSat,
                               fees.ServiceFeeSat);
        return new LiquidityCheck(null, liquidity);
    }

    /// <summary>The outcome of <see cref="CheckWillFund"/>: the purchase, or why it is refused.</summary>
    private sealed record LiquidityCheck(string? Refusal, DualFundLiquidity? Liquidity);

    /// <summary>Registers the channel with the signer for the first attempt.</summary>
    private void RegisterWithSigner(ChannelModel channel) =>
        _lightningSigner.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());

    /// <summary>
    /// An RBF attempt's funding for the signer (NL-521): a pending funding of the channel with the same funding keys
    /// and the attempt's capacity, signed for through the per-funding API until it completes
    /// (<see cref="LockRbfFunding"/>). The first attempt's registration cannot sign it: the BIP 143 sighash commits to
    /// the funding amount, which an RBF changes when a contribution does. Registering the same attempt again (a
    /// retransmission) is a no-op.
    /// </summary>
    private void RegisterRbfFunding(ChannelModel channel, InteractiveTxSessionModel session)
    {
        var funding = channel.FundingOutput!;
        _lightningSigner.RegisterFunding(channel.ChannelId,
                                         new ChannelFunding(funding.TransactionId!.Value, funding.Index!.Value,
                                                            (ulong)funding.Amount.Satoshi,
                                                            funding.LocalFundingPubKey, funding.RemoteFundingPubKey,
                                                            channel.LocalFundingKeyIndex, 0, 0,
                                                            ChannelFundingKind.Initial, ChannelFundingStatus.Pending,
                                                            session.FeeratePerKw, session.Locktime));
    }

    /// <summary>
    /// The signer signs for the channel's funding output from now on (NL-528): a no-op when it is the signer's current
    /// funding already, a lock when it is a registered attempt (pending, or an earlier one an RBF replaced), else it is
    /// registered first (after a restart the signer knows only the funding the channel row carried).
    /// </summary>
    private void MoveSignerToFunding(ChannelModel channel, InteractiveTxSessionModel? session)
    {
        var funding = channel.FundingOutput!;
        var txId = funding.TransactionId!.Value;
        try
        {
            _lightningSigner.LockFunding(channel.ChannelId, txId);
            return;
        }
        catch (SignerException)
        {
            // Not registered with this signer: registered below
        }

        _lightningSigner.RegisterFunding(channel.ChannelId,
                                         new ChannelFunding(txId, funding.Index!.Value, (ulong)funding.Amount.Satoshi,
                                                            funding.LocalFundingPubKey, funding.RemoteFundingPubKey,
                                                            channel.LocalFundingKeyIndex, 0, 0,
                                                            ChannelFundingKind.Initial, ChannelFundingStatus.Pending,
                                                            session?.FeeratePerKw, session?.Locktime));
        _lightningSigner.LockFunding(channel.ChannelId, txId);
    }

    /// <summary>
    /// A completed RBF attempt is the channel's funding for the signer too (its capacity may differ from the first
    /// attempt's, NL-521): the earlier attempts are kept as replaced fundings, whose keys stay known. Already current
    /// (a registration from the database after a restart) is a no-op.
    /// </summary>
    private void LockRbfFunding(ChannelModel channel, TxId txId)
    {
        try
        {
            _lightningSigner.LockFunding(channel.ChannelId, txId);
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "The signer did not take funding {TxId} of channel {ChannelId} as current; it "
                                + "loads the channel from the database at the next start", txId, channel.ChannelId);
        }
    }

    /// <summary>
    /// The channel's funding outpoint, capacity, balances and capacity-bound parameters for an RBF attempt: a changed
    /// contribution moves the capacity, both balances (a v2 open has no push), the reserve (BOLT 2: 1% of the funding
    /// both sides contribute, at least the dust limit, on both sides) and our in-flight limit (a share of the capacity,
    /// as at the open). The peer's in-flight limit is what it announced.
    /// </summary>
    private void ApplyAttemptFunding(DualFundNegotiation negotiation, ChannelModel channel, TxId txId, ushort index)
    {
        var funding = channel.FundingOutput!;
        var total = negotiation.Total;
        var channelParams = channel.ChannelParams;
        if (total != funding.Amount)
        {
            channelParams = WithCapacity(channelParams, total);
            _logger.LogInformation("RBF attempt {TxId} of channel {ChannelId}: capacity {Old} -> {New}, reserve {Reserve}",
                                   txId, channel.ChannelId, funding.Amount, total,
                                   channelParams.Local.ChannelReserveAmount);
        }

        // A liquidity purchase made with this attempt moves its fee from the buyer's balance to the seller's (NL-850)
        var (localBalance, remoteBalance) = DualFundLiquidity.ApplyFee(negotiation.LocalShare, negotiation.RemoteShare,
                                                                       negotiation.AttemptLiquidity?.LocalFeeMsat ?? 0);
        channel.ReplaceUnconfirmedFunding(new FundingOutputInfo(total, funding.LocalFundingPubKey,
                                                                funding.RemoteFundingPubKey, txId, index),
                                          localBalance, remoteBalance, channelParams);
    }

    /// <summary>
    /// Stages the attempt's liquidity purchase (NL-850) in the commitment step's save, as
    /// <see cref="LiquidityPurchaseKind.ChannelOpen"/> for the first attempt and
    /// <see cref="LiquidityPurchaseKind.OpenRbf"/> for an RBF attempt.
    /// </summary>
    private void RecordPurchase(DualFundNegotiation negotiation, ChannelModel channel, TxId txId,
                                DualFundLiquidity liquidity, IUnitOfWork unitOfWork)
    {
        var liquidityAds = GetLiquidityAds()
                        ?? throw new InvalidOperationException("Liquidity ads are not available on this node");
        var purchase = liquidityAds.CreatePurchase(channel.ChannelId, txId, liquidity.Role,
                                                   negotiation.CompletedTxIds.Count == 0
                                                       ? LiquidityPurchaseKind.ChannelOpen
                                                       : LiquidityPurchaseKind.OpenRbf,
                                                   liquidity.Request, liquidity.ContributedSat, liquidity.Fees,
                                                   liquidity.WillFund, negotiation.Peer, liquidity.MaxFeeSat);
        unitOfWork.LiquidityPurchaseDbRepository.Add(purchase);
        negotiation.Purchases[txId] = purchase;
        _logger.LogInformation("Liquidity {Role} of {Amount} sat recorded with funding {TxId} of channel {ChannelId}: "
                             + "fee {Fee} sat", liquidity.Role, liquidity.Request.RequestedSat, txId,
                               channel.ChannelId, liquidity.Fees.TotalSat);
    }

    /// <summary>
    /// <paramref name="channelParams"/> for a funding of <paramref name="total"/>: the reserve (BOLT 2: 1% of the
    /// funding both sides contribute, at least the dust limit, on both sides) follows it; both in-flight limits are
    /// what each side announced at the open, which no RBF attempt changes on the wire (NL-881).
    /// </summary>
    private static ChannelParams WithCapacity(ChannelParams channelParams, LightningMoney total)
    {
        var reserve = DualFundingRules.GetChannelReserve(total, Max(channelParams.Local.DustLimitAmount,
                                                                    channelParams.Remote.DustLimitAmount));
        var local = channelParams.Local;
        var remote = channelParams.Remote;
        return channelParams
              .WithLocal(new ChannelParty(local.DustLimitAmount, reserve, local.HtlcMinimumAmount,
                                          local.MaxAcceptedHtlcs, local.MaxHtlcValueInFlight, local.ToSelfDelay,
                                          local.UpfrontShutdownScript))
              .WithRemote(new ChannelParty(remote.DustLimitAmount, reserve, remote.HtlcMinimumAmount,
                                           remote.MaxAcceptedHtlcs, remote.MaxHtlcValueInFlight, remote.ToSelfDelay,
                                           remote.UpfrontShutdownScript));
    }

    private SignedTransaction BuildCommitment(ChannelModel channel, CommitmentSide side) =>
        _commitmentTransactionBuilder.Build(
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
                channel, side, side == CommitmentSide.Local ? channel.LocalCommitmentNumber
                                                            : channel.RemoteCommitmentNumber));

    private IInteractiveTxContributor GetContributor() =>
        _serviceProvider.GetService<IInteractiveTxContributor>()
     ?? throw new InvalidOperationException("No interactive-tx contributor is registered");

    /// <summary>The features negotiated with <paramref name="peer"/> (our own without a peer manager).</summary>
    private FeatureOptions GetNegotiatedFeatures(CompactPubKey peer) =>
        _serviceProvider.GetService<IPeerManager>()?.GetPeer(peer)?.NegotiatedFeatures ?? _nodeOptions.Features;

    private static BitcoinScript? NonEmpty(UpfrontShutdownScriptTlv? tlv)
    {
        if (tlv is not { Value.Length: > 0 })
            return null;

        return new BitcoinScript(tlv.Value);
    }

    private static LightningMoney Max(LightningMoney a, LightningMoney b) => a > b ? a : b;

    /// <summary>
    /// The open deadline of <see cref="OpenAsync"/>/<see cref="BumpAsync"/> (BOLT 2: the initiator gives up on a
    /// stalled negotiation) on the service's <see cref="TimeProvider"/>, like the accepter's watchdog, so a test can
    /// fire it deterministically (NL-512).
    /// </summary>
    private async Task<DualFundedOpenResult> WaitAsync(Task<DualFundedOpenResult> task,
                                                       CancellationToken cancellationToken) =>
        await task.WaitAsync(_options.OpenTimeout, _timeProvider, cancellationToken);

    private void ReleaseAnchorReserve(DualFundNegotiation negotiation)
    {
        if (!negotiation.HoldsAnchorReserve)
            return;

        negotiation.HoldsAnchorReserve = false;
        _serviceProvider.GetService<IAnchorReserveService>()?.ReleasePendingChannel(negotiation.ChannelId);
    }

    /// <summary>
    /// A failed open (the peer's error or warning, or the timeout): forgotten while the peer cannot hold a signed
    /// funding transaction, kept once our <c>tx_signatures</c> went out (the peer may broadcast it; BOLT 2 "MUST
    /// remember the channel"), with its funding watched since our <c>commitment_signed</c>.
    /// </summary>
    private async Task<DualFundedOpenResult> EndFailedOpenAsync(DualFundNegotiation negotiation,
                                                                DualFundedOpenResult result)
    {
        if (await ForgetUnfundedAsync(negotiation, result.FailureReason!))
            return result;

        return result with
        {
            FailureReason = $"{result.FailureReason} (the channel is kept: our tx_signatures were sent, so the peer can "
                          + "broadcast the funding transaction)"
        };
    }

    /// <summary>
    /// Forgets an open that never got a signed funding transaction, taking the channel's lock: a negotiation in
    /// progress (also after our <c>commitment_signed</c>) is aborted through the driver first (<c>tx_abort</c>, which
    /// releases our reservation). False, and nothing changed, once any attempt is fully signed or our
    /// <c>tx_signatures</c> were sent (IT-ABT-01: the peer holds our witnesses).
    /// </summary>
    private async Task<bool> ForgetUnfundedAsync(DualFundNegotiation negotiation, string reason)
    {
        var channelId = negotiation.ChannelId;
        using (await _channelLockProvider.AcquireAsync(channelId))
        {
            if (!CanForget(negotiation))
            {
                _logger.LogWarning("Dual-funded open of {ChannelId} failed ({Reason}) after our tx_signatures; the "
                                 + "channel is kept and its funding watched", channelId, reason);
                return false;
            }

            if (!await AbortNegotiationLockedAsync(negotiation, reason))
                return false;

            await ForgetUnfundedLockedAsync(negotiation);
            return true;
        }
    }

    /// <summary>
    /// Aborts the channel's negotiation in progress, or withdraws our <c>tx_init_rbf</c> that waits for its answer
    /// (NL-867), with our <c>tx_abort</c> (published), under the channel's lock. False when the driver refused because
    /// our <c>tx_signatures</c> went out (IT-ABT-01).
    /// </summary>
    private async Task<bool> AbortNegotiationLockedAsync(DualFundNegotiation negotiation, string reason)
    {
        var driver = _serviceProvider.GetService<IInteractiveTxDriver>();
        if (driver is null || (!driver.IsNegotiating(negotiation.ChannelId)
                            && driver.GetInfo(negotiation.ChannelId) is not { RbfRequested: true }))
            return true;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var messages = await driver.AbortAsync(negotiation.ChannelId, reason,
                                                   scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            _serviceProvider.GetService<IChannelMessagePublisher>()?.Publish(negotiation.Peer, messages);
            return true;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogWarning(e, "Could not abort the dual-funded open of {ChannelId}; the channel is kept",
                               negotiation.ChannelId);
            return false;
        }
    }

    /// <summary>Whether no attempt of the open is fully signed and our <c>tx_signatures</c> were not sent.</summary>
    private bool CanForget(DualFundNegotiation negotiation)
    {
        if (negotiation.CompletedTxIds.Count > 0)
            return false;

        var state = _serviceProvider.GetService<IInteractiveTxDriver>()?.GetInfo(negotiation.ChannelId)?.State;
        return state is not (InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed);
    }

    private async Task ForgetUnfundedLockedAsync(DualFundNegotiation negotiation)
    {
        if (!CanForget(negotiation))
            return;

        _negotiations.TryRemove(negotiation.ChannelId, out _);
        _negotiations.TryRemove(negotiation.TemporaryChannelId, out _);
        ReleaseAnchorReserve(negotiation);
        negotiation.EndSale();
        _channelMemoryRepository.TryRemoveTemporaryChannel(negotiation.Peer, negotiation.TemporaryChannelId);

        if (negotiation.Channel is not { } channel || channel.ChannelId != negotiation.ChannelId)
            return;

        if (channel.State == ChannelState.V1FundingSigned)
        {
            // Stored with our commitment_signed, never signed: nothing can confirm, so the channel is forgotten
            channel.UpdateState(ChannelState.Stale);
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.ChannelDbRepository.UpdateAsync(channel);

                // A purchase stored with the abandoned attempt never takes effect (NL-850)
                var stored = negotiation.Purchases.Values.Where(p => p is
                {
                    Id: > 0, Status: LiquidityPurchaseStatus.Pending
                });
                foreach (var purchase in stored)
                {
                    purchase.MarkClosed(GetMonitor()?.LastProcessedBlockHeight ?? 0);
                    unitOfWork.LiquidityPurchaseDbRepository.Update(purchase);
                }

                await unitOfWork.SaveChangesAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not persist the abandoned dual-funded channel {ChannelId} Stale",
                                 channel.ChannelId);
            }
        }

        if (channel.State is ChannelState.V1Opening or ChannelState.Stale)
            _channelMemoryRepository.TryRemoveChannel(channel.ChannelId);
    }

    /// <summary>
    /// Runs <paramref name="afterSave"/> once the caller released the channel's lock (the driver saved and the replies
    /// were raised by then).
    /// </summary>
    private void TrackAfterSave(DualFundNegotiation negotiation, TxId txId, Func<Task> afterSave)
    {
        var channelId = negotiation.ChannelId;
        var task = Task.Run(async () =>
        {
            try
            {
                using (await _channelLockProvider.AcquireAsync(channelId))
                {
                    // Only a completion the driver kept (its save succeeded) is published
                    var info = _serviceProvider.GetService<IInteractiveTxDriver>()?.GetInfo(channelId);
                    if (info is null || info.CompletedAttempts.All(a => !a.TxId.Equals(txId)))
                    {
                        _logger.LogWarning("The funding {TxId} of channel {ChannelId} was not saved; not publishing it",
                                           txId, channelId);
                        negotiation.CompletedTxIds.Remove(txId);
                        return;
                    }

                    await afterSave();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "After-save work of channel {ChannelId} failed", channelId);
            }
        });
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Stages the funding transaction's watch and its funding output's watch with our <c>commitment_signed</c> (the
    /// driver's save), and tracks them in the chain monitor once that save is done.
    /// </summary>
    private async Task StageFundingWatchesAsync(DualFundNegotiation negotiation, ChannelModel channel, TxId txId,
                                                uint index, IUnitOfWork unitOfWork)
    {
        var (fundingWatch, outpointWatch) = await AddMissingFundingWatchesAsync(channel, txId, index, unitOfWork);
        if (fundingWatch is null && outpointWatch is null)
            return;

        RunAfterLock(negotiation.ChannelId, async () =>
        {
            // Only watches the driver's save stored are tracked
            using var scope = _serviceProvider.CreateScope();
            var stored = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await stored.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId) is null
             || GetMonitor() is not { } monitor)
                return;

            if (fundingWatch is not null)
                monitor.TrackWatchedTransaction(fundingWatch);
            if (outpointWatch is not null)
                monitor.TrackWatchedOutpoint(outpointWatch);
        });
    }

    /// <summary>
    /// Stages the funding watch and the funding output watch of <paramref name="txId"/> unless stored already; returns
    /// the staged ones (null for a stored one).
    /// </summary>
    private static async Task<(WatchedTransactionModel? FundingWatch, WatchedOutpointModel? OutpointWatch)>
        AddMissingFundingWatchesAsync(ChannelModel channel, TxId txId, uint index, IUnitOfWork unitOfWork)
    {
        WatchedTransactionModel? fundingWatch = null;
        if (await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId) is null)
        {
            fundingWatch = new WatchedTransactionModel(channel.ChannelId, txId, channel.ChannelParams.MinimumDepth);
            unitOfWork.WatchedTransactionDbRepository.Add(fundingWatch);
        }

        WatchedOutpointModel? outpointWatch = null;
        if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(txId, index) is null)
        {
            outpointWatch = new WatchedOutpointModel(txId, index, channel.ChannelId,
                                                     WatchedOutpointPurpose.FundingOutput);
            unitOfWork.WatchedOutpointDbRepository.Add(outpointWatch);
        }

        return (fundingWatch, outpointWatch);
    }

    /// <summary>Runs <paramref name="work"/> under the channel's lock once the caller released it (background).</summary>
    private void RunAfterLock(ChannelId channelId, Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                using (await _channelLockProvider.AcquireAsync(channelId))
                    await work();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "After-save work of channel {ChannelId} failed", channelId);
            }
        });
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task PublishAsync(DualFundNegotiation negotiation, TxId txId, WatchedTransactionModel? fundingWatch,
                                    WatchedOutpointModel? outpointWatch, BroadcastTransactionModel broadcast)
    {
        var channel = negotiation.Channel!;
        ReleaseAnchorReserve(negotiation);
        if (GetMonitor() is { } monitor)
        {
            if (fundingWatch is not null)
                monitor.TrackWatchedTransaction(fundingWatch);
            if (outpointWatch is not null)
                monitor.TrackWatchedOutpoint(outpointWatch);
            if (!await monitor.PublishAsync(broadcast))
                _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} was not accepted yet; it is "
                                 + "sent again after every block", txId, channel.ChannelId);
        }

        _channelMemoryRepository.UpdateChannel(channel);
        _logger.LogInformation("Dual-funded channel {ChannelId} signed its funding transaction {TxId}",
                               channel.ChannelId, txId);
        negotiation.Complete(new DualFundedOpenResult(channel.ChannelId, txId)
        {
            Purchase = negotiation.Purchases.GetValueOrDefault(txId)
        });
    }

    #endregion
}