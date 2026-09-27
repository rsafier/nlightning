using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
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
using Domain.Channels.Validators;
using Domain.Channels.Validators.Parameters;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
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
/// <para>Known limits (ledger follow-ups): an RBF keeps both contributions (a different
/// <c>funding_output_contribution</c> is refused), and the channel keeps the signatures of the latest attempt only,
/// so a replaced attempt that confirms instead leaves the channel on the wrong outpoint (the per-funding commitments
/// come with splicing's <c>FundingSet</c>).</para>
/// </remarks>
public sealed class DualFundedOpenService : IDualFundedOpenService
{
    private const string OpenTimedOut = "the dual-funded open timed out";

    private readonly ConcurrentDictionary<ChannelId, DualFundNegotiation> _negotiations = new();
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
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
                                 IOptions<DualFundingOptions>? options = null)
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
        var commitmentFeerate = request.CommitmentFeeratePerKw ?? await EstimateFeerateAsync(cancellationToken);
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
        var localParams = CreateLocalParams(
            DualFundingRules.GetChannelReserve(request.LocalFundingAmount, _nodeOptions.DustLimitAmount),
            request.LocalFundingAmount);
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
                                           request.PeerNodeId, 0, ChannelState.V1Opening, ChannelVersion.V2);

        var negotiation = new DualFundNegotiation(temporaryId, temporaryId, request.PeerNodeId, true)
        {
            Channel = placeholder,
            LocalShare = request.LocalFundingAmount,
            FundingFeeratePerKw = fundingFeerate,
            Locktime = GetLocktime(),
            LocalRequiresConfirmedInputs = request.RequireConfirmedInputs,
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
            new ChannelTypeTlv(channelType), new UpfrontShutdownScriptTlv(Array.Empty<byte>()), request.RequireConfirmedInputs);

        // The peer's error for the open (on the temporary or the v2 id) ends it
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
            if (result.FailureReason is not null)
                await ForgetUnfundedAsync(negotiation, result.FailureReason);
            return result;
        }
        catch (TimeoutException)
        {
            await ForgetUnfundedAsync(negotiation, OpenTimedOut);
            return new DualFundedOpenResult(negotiation.ChannelId, null, OpenTimedOut);
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

            var localParams = CreateLocalParams(reserve, total);
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
            var channel = CreateChannel(channelParams, channelId,
                                        CreateLocalKeySet(pending.KeyIndex, pending.Basepoints,
                                                          pending.FirstPerCommitmentPoint), remoteKeySet, peerPubKey,
                                        true, negotiation.LocalShare, negotiation.RemoteShare);
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
    public async Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw,
                                                      CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<DualFundedOpenResult> completion;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            var negotiation = await GetOrLoadAsync(channelId, null, cancellationToken)
                           ?? throw new InvalidOperationException($"No dual-funded open on channel {channelId}");
            if (!negotiation.IsOpener)
                throw new InvalidOperationException($"We are not the opener of channel {channelId}");
            if (negotiation.Channel is not { State: ChannelState.V1FundingSigned } channel)
                throw new InvalidOperationException(
                    $"Channel {channelId} is not waiting for its funding (channel_ready sent or received)");
            if (negotiation.CompletedTxIds.Count == 0 || negotiation.LastContribution is not { } previous)
                throw new InvalidOperationException($"Channel {channelId} has no signed funding transaction to replace");

            var minimum = InteractiveTxDriver.GetMinimumRbfFeeratePerKw(negotiation.LastFeeratePerKw);
            if (feeratePerKw < minimum)
                throw new InvalidOperationException(
                    $"[IT-RBF-01] {feeratePerKw} sat/kw is below the minimum {minimum} sat/kw");

            var contribution = negotiation.LocalShare.IsZero
                                   ? InteractiveTxContribution.Empty
                                   : DualFundingRules.RebuildContributionForFeerate(
                                         previous, negotiation.LocalShare, true, GetSharedFunding(negotiation),
                                         feeratePerKw, LightningMoney.Satoshis(ChangeDustLimitSat))
                                  ?? throw new InvalidOperationException(
                                         $"Our inputs cannot pay {feeratePerKw} sat/kw; add funds and try again");

            var terms = CreateTerms(negotiation, true, feeratePerKw, GetLocktime(), contribution: contribution);
            var messages = await GetDriver().RequestRbfAsync(terms, negotiation.LocalShare, cancellationToken);
            completion = new TaskCompletionSource<DualFundedOpenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            negotiation.BumpCompletion = completion;
            _serviceProvider.GetRequiredService<IChannelMessagePublisher>().Publish(channel.RemoteNodeId, messages);
            _logger.LogInformation("tx_init_rbf of channel {ChannelId} at {Feerate} sat/kw", channelId,
                                   feeratePerKw);
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

    #endregion

    #region Accepter

    /// <summary>
    /// The contribution our accepter policy makes to a peer's <c>open_channel2</c>
    /// (<see cref="DualFundingOptions.AcceptContributionSat"/>, capped at the opener's by default).
    /// </summary>
    public LightningMoney GetAcceptContribution(OpenChannel2Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var wanted = LightningMoney.Satoshis(_options.AcceptContributionSat);
        return _options.MatchOpenerContribution && wanted > message.Payload.FundingAmount
                   ? message.Payload.FundingAmount
                   : wanted;
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
        var payload = message.Payload;
        var temporaryId = payload.ChannelId;

        if (negotiatedFeatures.DualFund == FeatureSupport.No)
            throw new ChannelErrorException("open_channel2 without option_dual_fund", temporaryId,
                                            "option_dual_fund is not negotiated");
        if (GetMonitor() is { IsChainProcessingHalted: true })
            throw new ChannelErrorException("Chain processing is halted (NL-216)", temporaryId,
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
        var localParams = CreateLocalParams(reserve, total);
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
                                    remoteKeySet, peerPubKey, false, localContribution, payload.FundingAmount);
        negotiation.Channel = channel;

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

        // The share may have dropped to zero (the wallet could not fund it): the channel's balances follow it
        if (negotiation.LocalShare != localContribution)
        {
            channel = CreateChannel(channelParams, channelId, CreateLocalKeySet(keyIndex, basepoints, firstPoint),
                                    remoteKeySet, peerPubKey, false, negotiation.LocalShare, payload.FundingAmount);
            negotiation.Channel = channel;
        }

        _channelMemoryRepository.AddChannel(channel);
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
                                                        message.ChannelTypeTlv, new UpfrontShutdownScriptTlv(Array.Empty<byte>()))
        ];
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
        channel.FundingOutput!.TransactionId = transaction.TxId;
        channel.FundingOutput.Index = checked((ushort)index);
        RegisterWithSigner(channel, isFirstAttempt);

        var remoteCommitment =
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote,
                                                                                channel.RemoteCommitmentNumber);
        var signature = _lightningSigner.SignChannelTransaction(
            channel.ChannelId, _commitmentTransactionBuilder.Build(remoteCommitment));
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
        var index = completion.Transaction.SharedOutputIndex!.Value;
        var height = GetMonitor()?.LastProcessedBlockHeight ?? 0;

        var fundingWatch = new WatchedTransactionModel(channel.ChannelId, txId, channel.ChannelParams.MinimumDepth);
        var broadcast = new BroadcastTransactionModel(completion.SignedTransaction, BroadcastPurpose.Funding,
                                                      channel.ChannelId, height);
        var outpointWatch = new WatchedOutpointModel(txId, index, channel.ChannelId,
                                                     WatchedOutpointPurpose.FundingOutput);
        unitOfWork.WatchedTransactionDbRepository.Add(fundingWatch);
        unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);
        unitOfWork.WatchedOutpointDbRepository.Add(outpointWatch);

        // An RBF replaced the earlier attempts: they are no longer sent after every block
        foreach (var replaced in negotiation.CompletedTxIds)
            await unitOfWork.BroadcastTransactionDbRepository.MarkReplacedAsync(replaced);

        negotiation.CompletedTxIds.Add(txId);
        negotiation.PendingTxId = null;
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
        negotiation.PendingTxId = null;
        if (negotiation.CompletedTxIds.Count > 0)
        {
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
    /// A peer's <c>tx_init_rbf</c> for the open (the driver checked the IT-RBF-01 feerate floor): accepted with the
    /// same shares, our inputs re-added and our change lowered for the new feerate.
    /// </summary>
    internal InteractiveTxRbfDecision DecideRbf(DualFundNegotiation negotiation, TxInitRbfMessage message)
    {
        if (negotiation.Channel is not { State: ChannelState.V1FundingSigned })
            return InteractiveTxRbfDecision.Reject("channel_ready was already sent or received");

        var theirs = message.FundingOutputContributionTlv?.Amount ?? LightningMoney.Zero;
        if (theirs.MilliSatoshi != negotiation.RemoteShare.MilliSatoshi)
            return InteractiveTxRbfDecision.Reject(
                $"changing the funding contribution ({negotiation.RemoteShare} -> {theirs}) is not supported");

        var contribution = InteractiveTxContribution.Empty;
        if (!negotiation.LocalShare.IsZero)
        {
            if (negotiation.LastContribution is not { } previous)
                return InteractiveTxRbfDecision.Reject("our previous contribution is unknown");

            var rebuilt = DualFundingRules.RebuildContributionForFeerate(
                previous, negotiation.LocalShare, false, GetSharedFunding(negotiation), message.Payload.Feerate,
                LightningMoney.Satoshis(ChangeDustLimitSat));
            if (rebuilt is null)
                return InteractiveTxRbfDecision.Reject($"our inputs cannot pay {message.Payload.Feerate} sat/kw");
            contribution = rebuilt;
        }

        var terms = CreateTerms(negotiation, false, message.Payload.Feerate, message.Payload.Locktime,
                                contribution: contribution);
        _logger.LogInformation("Accepting the RBF of channel {ChannelId} at {Feerate} sat/kw", negotiation.ChannelId,
                               message.Payload.Feerate);
        return InteractiveTxRbfDecision.Accept(terms, negotiation.LocalShare);
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
            var localCommitment =
                _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                                    channel.LocalCommitmentNumber);
            try
            {
                _lightningSigner.ValidateSignature(channelId, message.Payload.Signature,
                                                   _commitmentTransactionBuilder.Build(localCommitment));
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
            var replies = await driver.OnCommitmentSignedReceivedAsync(channelId, unitOfWork, cancellationToken);
            negotiation.CommitmentSignedReceived = true;
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
        if (unitOfWork is not null)
        {
            sessions = await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
        }
        else
        {
            using var scope = _serviceProvider.CreateScope();
            sessions = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                                  .InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId);
        }

        var stored = sessions.Where(s => s.State != InteractiveTxSessionState.Aborted && s.ConstructedTx is not null)
                             .OrderBy(s => s.CreatedAt)
                             .ToList();
        if (stored.Count == 0)
            return null;

        var latest = stored[^1];
        var negotiation = new DualFundNegotiation(channelId, channelId, channel.RemoteNodeId, channel.IsInitiator)
        {
            Channel = channel,
            LocalShare = LightningMoney.MilliSatoshis(channel.LocalBalance.MilliSatoshi),
            RemoteShare = LightningMoney.MilliSatoshis(channel.RemoteBalance.MilliSatoshi),
            FundingFeeratePerKw = latest.FeeratePerKw,
            Locktime = latest.Locktime,
            LastContribution = latest.LocalContribution,
            LastFeeratePerKw = latest.FeeratePerKw
        };
        negotiation.Host = new DualFundHost(this, negotiation);
        foreach (var signed in stored.Where(s => s.State == InteractiveTxSessionState.Signed))
            negotiation.CompletedTxIds.Add(signed.ConstructedTx!.TxId);
        if (latest.State != InteractiveTxSessionState.Signed)
        {
            negotiation.PendingTxId = latest.ConstructedTx!.TxId;
            negotiation.CommitmentSignedReceived = latest.CommitmentSignedReceived;
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
    /// Our <c>commitment_signed</c> for the pending attempt again (BOLT 2 <c>next_funding</c> retransmission; RFC 6979
    /// makes it the same signature).
    /// </summary>
    internal CommitmentSignedMessage? CreateCommitmentSignedRetransmission(DualFundNegotiation negotiation)
    {
        if (negotiation is not { Channel: { } channel, PendingTxId: { } txId })
            return null;

        var remoteCommitment =
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote,
                                                                                channel.RemoteCommitmentNumber);
        var signature = _lightningSigner.SignChannelTransaction(channel.ChannelId,
                                                                _commitmentTransactionBuilder.Build(remoteCommitment));
        return _messageFactory.CreateCommitmentSignedMessage(channel.ChannelId, signature, [], txId);
    }

    /// <summary>Forgets a channel's negotiation (memory only; the channel closed or was forgotten).</summary>
    internal bool Forget(ChannelId channelId) => _negotiations.TryRemove(channelId, out _);

    /// <summary>Whether a dual-funded open is known for <paramref name="channelId"/> (tests and diagnostics).</summary>
    public bool IsOpening(ChannelId channelId) => _negotiations.ContainsKey(channelId);

    /// <summary>The fully signed funding transactions of the channel's open, oldest first (tests and diagnostics).</summary>
    public IReadOnlyList<TxId> GetSignedFundingTxIds(ChannelId channelId) =>
        _negotiations.TryGetValue(channelId, out var negotiation) ? negotiation.CompletedTxIds.ToList() : [];

    /// <summary>Waits for the background work (the publish after a save) to finish (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (!_pending.IsEmpty)
            await Task.WhenAll(_pending.Keys.ToList());
    }

    #endregion

    #region Helpers

    private const long ChangeDustLimitSat = 546;

    private IInteractiveTxDriver GetDriver() =>
        _serviceProvider.GetService<IInteractiveTxDriver>()
     ?? throw new InvalidOperationException("No interactive-tx driver is registered");

    private IBlockchainMonitor? GetMonitor() => _serviceProvider.GetService<IBlockchainMonitor>();

    private uint GetLocktime() => GetMonitor()?.LastProcessedBlockHeight ?? 0;

    private async Task<uint> EstimateFeerateAsync(CancellationToken cancellationToken) =>
        (uint)(await _feeService.GetFeeRatePerKwAsync(cancellationToken)).Satoshi;

    private InteractiveTxTerms CreateTerms(DualFundNegotiation negotiation, bool isInitiator, uint feeratePerKw,
                                           uint locktime, InteractiveTxContributionRequest? request = null,
                                           InteractiveTxContribution? contribution = null) =>
        new(negotiation.ChannelId, _lightningSigner.GetNodePublicKey(), negotiation.Peer, isInitiator, feeratePerKw,
            locktime, negotiation.LocalRequiresConfirmedInputs, negotiation.RemoteRequiresConfirmedInputs, request,
            contribution);

    /// <summary>
    /// The values we announce. v2 has no reserve field: the reserve is fixed from both contributions
    /// (<see cref="DualFundingRules.GetChannelReserve"/>), set once they are known.
    /// </summary>
    private ChannelParty CreateLocalParams(LightningMoney reserve, LightningMoney inFlightBase) =>
        new(_nodeOptions.DustLimitAmount, reserve, _nodeOptions.HtlcMinimumAmount, _nodeOptions.MaxAcceptedHtlcs,
            LightningMoney.Satoshis(_nodeOptions.AllowUpToPercentageOfChannelFundsInFlight * inFlightBase.Satoshi
                                  / 100M),
            _nodeOptions.ToSelfDelay);

    private static ChannelKeySetModel CreateLocalKeySet(uint keyIndex, ChannelBasepoints basepoints,
                                                        CompactPubKey firstPoint) =>
        new(keyIndex, basepoints.FundingPubKey, basepoints.RevocationBasepoint, basepoints.PaymentBasepoint,
            basepoints.DelayedPaymentBasepoint, basepoints.HtlcBasepoint, firstPoint);

    private ChannelModel CreateChannel(ChannelParams channelParams, ChannelId channelId, ChannelKeySetModel localKeySet,
                                       ChannelKeySetModel remoteKeySet, CompactPubKey peer, bool isOpener,
                                       LightningMoney localShare, LightningMoney remoteShare)
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
        return new ChannelModel(channelParams, channelId, commitmentNumber, fundingOutput, isOpener, null, null,
                                LightningMoney.MilliSatoshis(localShare.MilliSatoshi), localKeySet, 0, 0,
                                LightningMoney.MilliSatoshis(remoteShare.MilliSatoshi), remoteKeySet, 0, peer, 0,
                                ChannelState.V1Opening, ChannelVersion.V2);
    }

    /// <summary>The channel's P2WSH 2-of-2 funding script (BOLT 3).</summary>
    internal static BitcoinScript GetFundingScript(ChannelModel channel)
    {
        var funding = channel.FundingOutput ?? throw new InvalidOperationException("The channel has no funding output");
        var output = new FundingOutput(funding.Amount, new PubKey(funding.LocalFundingPubKey),
                                       new PubKey(funding.RemoteFundingPubKey));
        return new BitcoinScript(output.ToTxOut().ScriptPubKey.ToBytes());
    }

    /// <summary>
    /// Registers the channel with the signer for the first attempt. An RBF attempt keeps the first registration: the
    /// commitment signature only commits to the funding amount, which an RBF does not change (the signer refuses
    /// another outpoint; its per-funding API comes with splicing).
    /// </summary>
    private void RegisterWithSigner(ChannelModel channel, bool isFirstAttempt)
    {
        try
        {
            _lightningSigner.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        }
        catch (SignerException e) when (!isFirstAttempt)
        {
            _logger.LogDebug(e, "Signer keeps the first funding outpoint of channel {ChannelId} for the RBF attempt",
                             channel.ChannelId);
        }
    }

    private static BitcoinScript? NonEmpty(UpfrontShutdownScriptTlv? tlv)
    {
        if (tlv is not { Value.Length: > 0 })
            return null;

        return new BitcoinScript(tlv.Value);
    }

    private static LightningMoney Max(LightningMoney a, LightningMoney b) => a > b ? a : b;

    private async Task<DualFundedOpenResult> WaitAsync(Task<DualFundedOpenResult> task,
                                                       CancellationToken cancellationToken) =>
        await task.WaitAsync(_options.OpenTimeout, cancellationToken);

    private void ReleaseAnchorReserve(DualFundNegotiation negotiation)
    {
        if (!negotiation.HoldsAnchorReserve)
            return;

        negotiation.HoldsAnchorReserve = false;
        _serviceProvider.GetService<IAnchorReserveService>()?.ReleasePendingChannel(negotiation.ChannelId);
    }

    /// <summary>Forgets an open that never got a signed funding transaction, taking the channel's lock.</summary>
    private async Task ForgetUnfundedAsync(DualFundNegotiation negotiation, string reason)
    {
        var channelId = negotiation.ChannelId;
        using (await _channelLockProvider.AcquireAsync(channelId))
        {
            if (negotiation.CompletedTxIds.Count > 0)
                return;

            var driver = _serviceProvider.GetService<IInteractiveTxDriver>();
            if (driver?.IsNegotiating(channelId) == true && negotiation.Channel is { State: ChannelState.V1Opening })
            {
                using var scope = _serviceProvider.CreateScope();
                var messages = await driver.AbortAsync(channelId, reason,
                                                       scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
                _serviceProvider.GetService<IChannelMessagePublisher>()?.Publish(negotiation.Peer, messages);
            }

            await ForgetUnfundedLockedAsync(negotiation);
        }
    }

    private async Task ForgetUnfundedLockedAsync(DualFundNegotiation negotiation)
    {
        if (negotiation.CompletedTxIds.Count > 0)
            return;

        _negotiations.TryRemove(negotiation.ChannelId, out _);
        _negotiations.TryRemove(negotiation.TemporaryChannelId, out _);
        ReleaseAnchorReserve(negotiation);
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

    private async Task PublishAsync(DualFundNegotiation negotiation, TxId txId, WatchedTransactionModel fundingWatch,
                                    WatchedOutpointModel outpointWatch, BroadcastTransactionModel broadcast)
    {
        var channel = negotiation.Channel!;
        ReleaseAnchorReserve(negotiation);
        if (GetMonitor() is { } monitor)
        {
            monitor.TrackWatchedTransaction(fundingWatch);
            monitor.TrackWatchedOutpoint(outpointWatch);
            if (!await monitor.PublishAsync(broadcast))
                _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} was not accepted yet; it is "
                                 + "sent again after every block", txId, channel.ChannelId);
        }

        _channelMemoryRepository.UpdateChannel(channel);
        _logger.LogInformation("Dual-funded channel {ChannelId} signed its funding transaction {TxId}",
                               channel.ChannelId, txId);
        negotiation.Complete(new DualFundedOpenResult(channel.ChannelId, txId));
    }

    #endregion
}