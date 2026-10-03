using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Channels.Close;
using Domain.Accounting.Labels;
using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.LiquidityAds.Models;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Models;
using Interfaces;

public sealed class OpenChannelClientHandler
    : IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>
{
    private readonly IAnchorReserveService? _anchorReserveService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelManager _channelManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelFactory _channelFactory;
    private readonly IChannelLockProvider? _channelLockProvider;
    private readonly ILogger<OpenChannelClientHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IPeerManager _peerManager;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;
    private readonly UpfrontShutdownScriptSource? _upfrontShutdownScriptSource;
    private readonly IDualFundedOpenService? _dualFundedOpenService;
    private readonly ILightningSigner? _lightningSigner;

    private ChannelId _channelId = ChannelId.Zero;
    private ChannelId? _upgradedChannelId;
    private bool _v1OpenWithDualFundNegotiated;
    private IPeerService? _peerService;

    /// <summary>The default of <see cref="OpenTimeout"/>.</summary>
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.OpenChannel;

    /// <summary>
    /// How long we wait for the peer's accept_channel after open_channel (NL-392). The open then fails and its
    /// temporary channel is forgotten.
    /// </summary>
    internal TimeSpan OpenTimeout { get; set; } = DefaultOpenTimeout;

    public OpenChannelClientHandler(IBlockchainMonitor blockchainMonitor, IChannelFactory channelFactory,
                                    IChannelManager channelManager, IChannelMemoryRepository channelMemoryRepository,
                                    ILogger<OpenChannelClientHandler> logger, IMessageFactory messageFactory,
                                    IPeerManager peerManager, IUtxoMemoryRepository utxoMemoryRepository,
                                    IOptions<GossipOptions>? gossipOptions = null,
                                    IOptions<NodeOptions>? nodeOptions = null,
                                    IAnchorReserveService? anchorReserveService = null,
                                    IChannelLockProvider? channelLockProvider = null,
                                    UpfrontShutdownScriptSource? upfrontShutdownScriptSource = null,
                                    IDualFundedOpenService? dualFundedOpenService = null,
                                    ILightningSigner? lightningSigner = null)
    {
        _lightningSigner = lightningSigner;
        _dualFundedOpenService = dualFundedOpenService;
        _upfrontShutdownScriptSource = upfrontShutdownScriptSource;
        _channelLockProvider = channelLockProvider;
        _anchorReserveService = anchorReserveService;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();
        _blockchainMonitor = blockchainMonitor;
        _channelFactory = channelFactory;
        _channelManager = channelManager;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _messageFactory = messageFactory;
        _peerManager = peerManager;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    /// <inheritdoc/>
    public async Task<OpenChannelClientResponse> HandleAsync(OpenChannelClientRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NodeInfo))
            throw new ClientException(ErrorCodes.InvalidAddress, "Address cannot be empty");

        // NL-216: no new channel while the node does not follow the chain (it could not see the funding confirm)
        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("openchannel"));

        // BOLT 7 plan D12: public channels stay off on mainnet until the Docker proof of G1 passed
        if (request.IsPublic && _nodeOptions.BitcoinNetwork == BitcoinNetwork.Mainnet
                             && !_gossipOptions.AllowPublicChannelsOnMainnet)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Public channels are not enabled on mainnet yet "
                                    + "(Gossip:AllowPublicChannelsOnMainnet)");

        if (request.IsPublic && request.IsZeroConfChannel)
            throw new ClientException(ErrorCodes.InvalidOperation, "A public channel can't be zero-conf");

        if (request.IsDualFunded && request.ForceV1)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "A channel can't be opened both dual-funded (--dual-fund) and v1 (--v1)");

        // Liquidity ads (NL-850): buying inbound liquidity rides on open_channel2, so it implies a v2 open
        CheckLiquidityRequest(request);

        // Simple taproot channels (NL-877 T5): private, no liquidity purchase (NL-971)
        CheckSimpleTaprootRequest(request);

        // NL-602 A3-T1: refused before anything is sent; stored with the channel's first save
        var labels = SourceLabelsGuard.Check(request.Label, request.Tags);

        // Check if either a PeerAddressInfo or a CompactPubKey was provided
        var isPeerAddressInfo = request.NodeInfo.Contains('@') && request.NodeInfo.Contains(':');
        CompactPubKey peerId;

        peerId = isPeerAddressInfo
                     ? new PeerAddress(request.NodeInfo).PubKey
                     : new CompactPubKey(Convert.FromHexString(request.NodeInfo)); // Parse as a hex public key

        // Check if we're connected to the peer
        var peer = _peerManager.GetPeer(peerId)
                ?? await _peerManager.ConnectToPeerAsync(new PeerAddressInfo(request.NodeInfo));

        // Simple taproot channels (NL-877 T5): our advertisement, the peer's support and option_simple_close; the open
        // then goes v1 or v2 by the NL-551 rules like any other (DualFundedOpenRequest.SimpleTaproot on v2)
        if (request.IsSimpleTaproot)
            CheckSimpleTaprootPeer(peer);

        // Wave DF: a dual-funded (v2) open negotiates the funding transaction interactively, our share from the wallet.
        // NL-551: it is the default when the peer supports it (Eclair refuses a v1 open once option_dual_fund is
        // negotiated; CLN and Eclair open v2 themselves then)
        if (request.IsDualFunded || request.RequestInboundSat is not null)
            return await OpenDualFundedAsync(request, peerId, labels, ct);
        if (OpensDualFundedByDefault(request, peer))
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Opening a dual-funded channel with {PeerId}: option_dual_fund is negotiated "
                                     + "(openchannel --v1 opens v1)", peerId);
            return await OpenDualFundedAsync(request, peerId, labels, ct);
        }

        // Let's check if we have enough funds to open this channel
        var currentHeight = _blockchainMonitor.LastProcessedBlockHeight;
        if (_utxoMemoryRepository.GetConfirmedBalance(currentHeight) < request.FundingAmount)
            throw new ClientException(ErrorCodes.NotEnoughBalance, "We don't have enough balance to open this channel");

        // NL-557: a v1 open (push, zero-conf or --v1) to a peer with which option_dual_fund is negotiated; Eclair refuses
        // those, so a refusal names the way out
        _v1OpenWithDualFundNegotiated = peer.NegotiatedFeatures.DualFund != FeatureSupport.No;

        // Since we're connected, let's open the channel
        var channel =
            await _channelFactory.CreateChannelV1AsInitiatorAsync(request, peer.NegotiatedFeatures, peerId);
        channel.Label = labels.Label;
        channel.Tags = labels.CanonicalTags;

        // Save the channelId for later
        _channelId = channel.ChannelId;

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Created Temporary Channel {id} with fundingPubKey: {fundingPubKey}", channel.ChannelId,
                             channel.LocalKeySet.FundingCompactPubKey);

        // Select UTXOs and mark them as toSpend for this channel: through the reserve service the funding keeps the
        // anchors reserve (NL-379), counting this channel when it has anchors, and skips outputs our own pending
        // broadcasts spend (NL-385); both checks count the funding transaction's fee. An anchors channel counts toward
        // the reserve from the check until it is funded or fails (released in the finally below)
        if (_anchorReserveService is not null)
        {
            try
            {
                await _anchorReserveService.EnsureCanFundAsync(request.FundingAmount, channel, ct);
                try
                {
                    await _anchorReserveService.LockFundingUtxosAsync(request.FundingAmount, channel, ct);
                }
                catch (InvalidOperationException e) when (IsTooFewUtxos(e))
                {
                    // NL-393: the funding selection found too few UTXOs (a concurrent spend since the balance check)
                    throw NotEnoughBalance(e);
                }
            }
            catch (InsufficientFundsException e)
            {
                _anchorReserveService.ReleasePendingChannel(channel.ChannelId);
                throw new ClientException(ErrorCodes.NotEnoughBalance, e.Message);
            }
            catch
            {
                _anchorReserveService.ReleasePendingChannel(channel.ChannelId);
                throw;
            }
        }
        else
        {
            try
            {
                _utxoMemoryRepository.LockUtxosToSpendOnChannel(request.FundingAmount, channel.ChannelId);
            }
            catch (InvalidOperationException e) when (IsTooFewUtxos(e))
            {
                throw NotEnoughBalance(e);
            }
        }

        // Create a task completion source for the response
        var tsc = new TaskCompletionSource<OpenChannelClientResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;

        try
        {
            // Create the channel type Tlv; accept_channel must echo exactly this type
            var channelTypeTlv = new ChannelTypeTlv(channel.ChannelParams.ToChannelType());

            // NL-045: when option_upfront_shutdown_script is negotiated, a reserved wallet address becomes our upfront
            // shutdown script (after the funds are locked, so a refused open reserves nothing); the peer makes an open
            // that failed before funding_created reuse its script instead of reserving another one (NL-463)
            if (_upfrontShutdownScriptSource is not null)
                await _upfrontShutdownScriptSource.AssignIfNegotiatedAsync(channel, peer.NegotiatedFeatures, peerId);

            // Create UpfrontShutdownScriptTlv if needed
            var upfrontShutdownScriptTlv = channel.LocalUpfrontShutdownScript is not null
                                               ? new UpfrontShutdownScriptTlv(channel.LocalUpfrontShutdownScript.Value)
                                               : new UpfrontShutdownScriptTlv(Array.Empty<byte>());

            // Create the ChannelFlags (NL-341): announce_channel for a public channel, whose channel type the factory
            // built without option_scid_alias (BOLT 2 forbids the two together)
            var channelFlags = new ChannelFlags(channel.AnnounceChannel ? ChannelFlag.AnnounceChannel
                                                                        : ChannelFlag.None);

            // Create the openChannel message
            // funding_satoshis is the whole channel; the pushed part is only the peer's opening balance. A simple
            // taproot channel's carries next_local_nonce: our verification nonce for our commitment 0 (NL-877 T5)
            var openChannel1Message = channel.ChannelParams.OptionSimpleTaproot
                                          ? _messageFactory.CreateOpenChannel1Message(
                                              channel.ChannelId, request.FundingAmount,
                                              channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance,
                                              channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
                                              channel.LocalKeySet.RevocationCompactBasepoint,
                                              channel.LocalKeySet.PaymentCompactBasepoint,
                                              channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                                              channel.LocalKeySet.HtlcCompactBasepoint,
                                              channel.LocalKeySet.CurrentPerCommitmentCompactPoint, channelFlags,
                                              channelTypeTlv, upfrontShutdownScriptTlv,
                                              (_lightningSigner ?? throw new InvalidOperationException(
                                                   "No signer for the simple taproot open"))
                                             .GetLocalVerificationNonce(channel.LocalKeySet.KeyIndex, null, 0))
                                          : _messageFactory.CreateOpenChannel1Message(
                                              channel.ChannelId, request.FundingAmount,
                                              channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance,
                                              channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
                                              channel.LocalKeySet.RevocationCompactBasepoint,
                                              channel.LocalKeySet.PaymentCompactBasepoint,
                                              channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                                              channel.LocalKeySet.HtlcCompactBasepoint,
                                              channel.LocalKeySet.CurrentPerCommitmentCompactPoint, channelFlags,
                                              channelTypeTlv, upfrontShutdownScriptTlv);

            if (!peer.TryGetPeerService(out _peerService))
                throw new ClientException(ErrorCodes.InvalidOperation, "Error getting peerService from peer");

            // Subscribe to the events before sending the message
            _peerService.OnAttentionMessageReceived += AttentionMessageHandlerEnvelope;
            _peerService.OnDisconnect += PeerDisconnectionEnvelope;
            _peerService.OnExceptionRaised += ExceptionRaisedEnvelope;
            _channelMemoryRepository.OnChannelUpgraded += ChannelUpgradedHandlerEnvelope;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sending OpenChannel message to peer {peerId} for channel {channelId}",
                                       peerId,
                                       channel.ChannelId);
            // Bounded wait for accept_channel (NL-392): a peer that never answers, or a cancelled request, fails the
            // open and forgets its temporary channel below
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(OpenTimeout);
            await using var waitRegistration = waitCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                    tsc.TrySetCanceled(ct);
                else if (!tsc.Task.IsCompleted)
                {
                    timedOut = true;
                    tsc.TrySetException(new ClientException(ErrorCodes.ConnectionError,
                                                            $"Peer {peerId} did not answer open_channel within "
                                                          + $"{OpenTimeout.TotalSeconds:0} s"));
                }
            });

            // Stores the temporary channel and queues open_channel on the peer's outbox, under the channel's lock
            await _channelManager.StartOpeningChannelAsync(peerId, channel, openChannel1Message);

            return await tsc.Task;
        }
        catch (Exception e)
        {
            // The cleanup holds the temporary channel's lock (NL-392 review): an accept_channel handler that already
            // read our locked UTXOs finishes first, and once it has upgraded the channel (and moved the UTXOs to the
            // new channel id) there is nothing to forget or return
            using (_channelLockProvider is null
                       ? null
                       : await _channelLockProvider.AcquireAsync(_channelId, CancellationToken.None))
            {
                if (_upgradedChannelId is { } upgradedId)
                {
                    _logger.LogWarning("The open of {ChannelId} failed ({Reason}) after accept_channel was processed; "
                                     + "the channel goes on as {NewChannelId}", _channelId, e.Message, upgradedId);

                    // A timeout raced a late accept_channel: the open did succeed
                    if (timedOut)
                        return new OpenChannelClientResponse(upgradedId);

                    throw;
                }

                _utxoMemoryRepository.ReturnUtxosNotSpentOnChannel(_channelId);

                // NL-392: the failed open's temporary channel (already gone when accept_channel's handler failed or
                // the peer disconnected)
                if (_channelMemoryRepository.TryRemoveTemporaryChannel(peerId, _channelId))
                    _logger.LogInformation("Forgot the temporary channel {ChannelId} of the failed open", _channelId);
            }

            throw;
        }
        finally
        {
            // Funded (it counts as a channel now) or failed: the open no longer holds anchors reserve as pending
            _anchorReserveService?.ReleasePendingChannel(_channelId);

            //Unsubscribe from the events so we don't have dangling memory
            _peerService?.OnAttentionMessageReceived -= AttentionMessageHandlerEnvelope;
            _peerService?.OnDisconnect -= PeerDisconnectionEnvelope;
            _peerService?.OnExceptionRaised -= ExceptionRaisedEnvelope;
            _channelMemoryRepository.OnChannelUpgraded -= ChannelUpgradedHandlerEnvelope;
        }

        // Envelopes for the events
        void AttentionMessageHandlerEnvelope(object? _, AttentionMessageEventArgs args) =>
            HandleAttentionMessage(args, tsc);

        void PeerDisconnectionEnvelope(object? _, PeerDisconnectedEventArgs args) =>
            HandlePeerDisconnection(args, channel.RemoteNodeId, tsc);

        void ExceptionRaisedEnvelope(object? _, Exception e) =>
            HandleExceptionRaised(e, tsc);

        void ChannelUpgradedHandlerEnvelope(object? _, ChannelUpgradedEventArgs args) =>
            HandleChannelUpgraded(args, tsc);
    }

    /// <summary>
    /// <c>openchannel --dual-fund</c> (or a plain <c>openchannel</c> to a peer with <c>option_dual_fund</c>, NL-551):
    /// <c>open_channel2</c> with <see cref="OpenChannelClientRequest.FundingAmount"/>
    /// as our contribution (BOLT 2 "Channel Establishment v2"); completes once both <c>tx_signatures</c> were exchanged.
    /// </summary>
    private async Task<OpenChannelClientResponse> OpenDualFundedAsync(OpenChannelClientRequest request,
                                                                     CompactPubKey peerId, SourceLabels labels,
                                                                     CancellationToken ct)
    {
        if (_dualFundedOpenService is null)
            throw new ClientException(ErrorCodes.InvalidOperation, "Dual-funded opens are not available on this node");
        if (request.PushAmount is { IsZero: false })
            throw new ClientException(ErrorCodes.InvalidOperation, "A dual-funded open has no push amount");
        if (request.IsZeroConfChannel)
            throw new ClientException(ErrorCodes.InvalidOperation, "A dual-funded open can't be zero-conf");

        var currentHeight = _blockchainMonitor.LastProcessedBlockHeight;
        if (_utxoMemoryRepository.GetConfirmedBalance(currentHeight) < request.FundingAmount)
            throw new ClientException(ErrorCodes.NotEnoughBalance, "We don't have enough balance to open this channel");

        DualFundedOpenResult result;
        var openRequest = new DualFundedOpenRequest(peerId, request.FundingAmount,
                                                    request.FeeRatePerKw is { } feerate ? (uint)feerate.Satoshi : null,
                                                    null, request.IsPublic)
        {
            Labels = labels,
            SimpleTaproot = request.IsSimpleTaproot,
            Liquidity = request.RequestInboundSat is { } inbound
                            ? new LiquidityRequest(inbound, null, request.MaxLiquidityFeeSat)
                            : null
        };
        try
        {
            result = await _dualFundedOpenService.OpenAsync(openRequest, ct);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }

        if (result.FailureReason is not null)
            throw new ClientException(ErrorCodes.InvalidOperation, $"Dual-funded open failed: {result.FailureReason}");

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Dual-funded channel {ChannelId} opened with funding {TxId}", result.ChannelId,
                                   result.FundingTxId);

        // NL-535: the funding is signed and published now, long before it confirms; the client prints it at once so
        // the operator can bumpopen it
        uint? outputIndex = _channelMemoryRepository.TryGetChannel(result.ChannelId, out var channel)
                         && channel.FundingOutput is { TransactionId: { } txId, Index: { } index }
                         && txId == result.FundingTxId
                                ? index
                                : null;
        return new OpenChannelClientResponse(result.ChannelId)
        {
            FundingTxId = result.FundingTxId,
            FundingOutputIndex = outputIndex,
            Purchase = result.Purchase
        };
    }

    /// <summary>
    /// The liquidity ads options of <c>openchannel</c> (NL-850): <c>--request-inbound</c> needs a dual-funded open (no
    /// <c>--v1</c>, no push, no zero-conf) and an amount above 0; <c>--max-liquidity-fee</c> only with it.
    /// </summary>
    internal static void CheckLiquidityRequest(OpenChannelClientRequest request)
    {
        if (request.RequestInboundSat is not { } inbound)
        {
            if (request.MaxLiquidityFeeSat is not null)
                throw new ClientException(ErrorCodes.InvalidOperation,
                                          "--max-liquidity-fee needs --request-inbound");
            return;
        }

        if (inbound == 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The inbound liquidity to buy must be above 0");
        if (request.ForceV1)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Buying inbound liquidity (--request-inbound) needs a dual-funded open, not --v1");
        if (request.PushAmount is { IsZero: false })
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Buying inbound liquidity (--request-inbound) needs a dual-funded open, which has "
                                    + "no push amount");
        if (request.IsZeroConfChannel)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Buying inbound liquidity (--request-inbound) can't be zero-conf");
    }

    /// <summary>
    /// <c>openchannel --channel-type taproot</c> (NL-877 T5), before anything is looked up: a simple taproot channel is
    /// private (the spec forbids <c>announce_channel</c>, taproot gossip is T7) and buys no liquidity (no
    /// <c>--request-inbound</c>: liquidity ads are not wired for the taproot funding script and weight yet, NL-971). It
    /// may open v1 or dual-funded (<c>--dual-fund</c>, or v2 by default by the NL-551 rules).
    /// </summary>
    internal static void CheckSimpleTaprootRequest(OpenChannelClientRequest request)
    {
        if (!request.IsSimpleTaproot)
            return;

        if (request.IsPublic)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Simple taproot channels are private: --public can't be used with "
                                    + "--channel-type taproot");
        if (request.RequestInboundSat is not null)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Buying inbound liquidity (--request-inbound) is not supported with "
                                    + "--channel-type taproot yet (NL-971)");
    }

    /// <summary>
    /// <c>openchannel --channel-type taproot</c> once the peer is connected: our <c>Features:OptionSimpleTaproot</c>
    /// advertised (with <c>Features:AllowExperimentalFeatures</c>), the peer supporting it (bits 80/81) and
    /// <c>option_simple_close</c>.
    /// </summary>
    private void CheckSimpleTaprootPeer(PeerModel peer)
    {
        if (!_nodeOptions.Features.IsSimpleTaprootAdvertised)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Simple taproot channels are not enabled on this node: set "
                                    + "Features:OptionSimpleTaproot=Optional and Features:AllowExperimentalFeatures=true");
        if (peer.NegotiatedFeatures.OptionSimpleTaproot == FeatureSupport.No)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The peer does not support simple taproot channels (feature bits 80/81)");
        if (peer.NegotiatedFeatures.OptionSimpleClose == FeatureSupport.No)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "A simple taproot channel needs option_simple_close, which the peer did not "
                                    + "negotiate");
    }

    /// <summary>
    /// NL-551: a plain <c>openchannel</c> opens v2 when <c>option_dual_fund</c> is negotiated with the peer and nothing
    /// needs v1: no <see cref="OpenChannelClientRequest.ForceV1"/>, no push amount and no zero-conf (v2 has neither).
    /// </summary>
    private bool OpensDualFundedByDefault(OpenChannelClientRequest request, PeerModel peer) =>
        _dualFundedOpenService is not null
     && !request.ForceV1
     && request.PushAmount is not { IsZero: false }
     && !request.IsZeroConfChannel
     && peer.NegotiatedFeatures.DualFund != FeatureSupport.No;

    /// <summary>
    /// The funding selection's own failure (NL-393): the UTXO lock throws a plain <see cref="InvalidOperationException"/>
    /// when the wallet has too few spendable outputs; any other invalid operation (a shutdown's
    /// <see cref="ObjectDisposedException"/>, a subclass) keeps its own error.
    /// </summary>
    private static bool IsTooFewUtxos(InvalidOperationException e) => e.GetType() == typeof(InvalidOperationException);

    private static ClientException NotEnoughBalance(InvalidOperationException e) =>
        new(ErrorCodes.NotEnoughBalance, $"We don't have enough balance to open this channel: {e.Message}");

    private void HandleChannelUpgraded(ChannelUpgradedEventArgs args,
                                       TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.OldChannelId != _channelId)
            return;

        _upgradedChannelId = args.NewChannelId;
        tsc.TrySetResult(new OpenChannelClientResponse(args.NewChannelId));

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Channel {oldChannelId} has been upgraded to {channelId}", args.OldChannelId,
                                   args.NewChannelId);
    }

    private void HandleAttentionMessage(AttentionMessageEventArgs args,
                                        TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.ChannelId != _channelId)
            return;

        _logger.LogError(
            "Received attention message from peer {peerId} for channel {channelId}: {message}",
            args.PeerPubKey, args.ChannelId, args.Message);

        tsc.TrySetException(new ChannelErrorException(DescribeRefusal(args.Message)));
    }

    /// <summary>
    /// The client error of a refused v1 open (NL-557): the peer's text, and, when <c>option_dual_fund</c> is negotiated
    /// with the peer, that it may require a dual-funded open. Eclair (checked on 0.14.3 and its master, 2026-10-02)
    /// treats every channel with a peer that negotiated <c>option_dual_fund</c> as dual-funded and refuses
    /// <c>open_channel</c> ("custom remote channel reserve is incompatible with dual-funded channels"); BOLT 2 allows the
    /// v1 open, and CLN and LDK accept it.
    /// </summary>
    internal string DescribeRefusal(string? peerMessage)
    {
        var message = $"Error opening channel: {peerMessage}";
        if (!_v1OpenWithDualFundNegotiated)
            return message;

        return message + ". The peer negotiated option_dual_fund and may accept only a dual-funded (v2) open, as "
             + "Eclair does: open without a push amount, zero-conf or --v1 to open it dual-funded";
    }

    private void HandlePeerDisconnection(PeerDisconnectedEventArgs args, CompactPubKey peerPubKey,
                                         TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (args.PeerPubKey != peerPubKey)
            return;

        _logger.LogError("Peer disconnected without notice");
        tsc.TrySetException(new ConnectionException("Error opening channel: Peer disconnected"));
    }

    private void HandleExceptionRaised(Exception e, TaskCompletionSource<OpenChannelClientResponse> tsc)
    {
        if (e is not ChannelErrorException ce || ce.ChannelId != _channelId)
            return;

        _logger.LogError("Exception raised while opening channel: {message}", e.Message);
        tsc.TrySetException(e);
    }
}