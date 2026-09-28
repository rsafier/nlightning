using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Channels.Close;
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
using Domain.Node.Events;
using Domain.Node.Interfaces;
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

    private ChannelId _channelId = ChannelId.Zero;
    private ChannelId? _upgradedChannelId;
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
                                    IDualFundedOpenService? dualFundedOpenService = null)
    {
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

        // Check if either a PeerAddressInfo or a CompactPubKey was provided
        var isPeerAddressInfo = request.NodeInfo.Contains('@') && request.NodeInfo.Contains(':');
        CompactPubKey peerId;

        peerId = isPeerAddressInfo
                     ? new PeerAddress(request.NodeInfo).PubKey
                     : new CompactPubKey(Convert.FromHexString(request.NodeInfo)); // Parse as a hex public key

        // Check if we're connected to the peer
        var peer = _peerManager.GetPeer(peerId)
                ?? await _peerManager.ConnectToPeerAsync(new PeerAddressInfo(request.NodeInfo));

        // Wave DF: a dual-funded (v2) open negotiates the funding transaction interactively, our share from the wallet
        if (request.IsDualFunded)
            return await OpenDualFundedAsync(request, peerId, ct);

        // Let's check if we have enough funds to open this channel
        var currentHeight = _blockchainMonitor.LastProcessedBlockHeight;
        if (_utxoMemoryRepository.GetConfirmedBalance(currentHeight) < request.FundingAmount)
            throw new ClientException(ErrorCodes.NotEnoughBalance, "We don't have enough balance to open this channel");

        // Since we're connected, let's open the channel
        var channel =
            await _channelFactory.CreateChannelV1AsInitiatorAsync(request, peer.NegotiatedFeatures, peerId);

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
            // shutdown script (after the funds are locked, so a refused open reserves nothing)
            if (_upfrontShutdownScriptSource is not null)
                await _upfrontShutdownScriptSource.AssignIfNegotiatedAsync(channel, peer.NegotiatedFeatures);

            // Create UpfrontShutdownScriptTlv if needed
            var upfrontShutdownScriptTlv = channel.LocalUpfrontShutdownScript is not null
                                               ? new UpfrontShutdownScriptTlv(channel.LocalUpfrontShutdownScript.Value)
                                               : new UpfrontShutdownScriptTlv(Array.Empty<byte>());

            // Create the ChannelFlags (NL-341): announce_channel for a public channel, whose channel type the factory
            // built without option_scid_alias (BOLT 2 forbids the two together)
            var channelFlags = new ChannelFlags(channel.AnnounceChannel ? ChannelFlag.AnnounceChannel
                                                                        : ChannelFlag.None);

            // Create the openChannel message
            // funding_satoshis is the whole channel; the pushed part is only the peer's opening balance
            var openChannel1Message = _messageFactory.CreateOpenChannel1Message(
                channel.ChannelId, request.FundingAmount, channel.LocalKeySet.FundingCompactPubKey,
                channel.RemoteBalance, channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
                channel.LocalKeySet.RevocationCompactBasepoint,
                channel.LocalKeySet.PaymentCompactBasepoint, channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                channel.LocalKeySet.HtlcCompactBasepoint, channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
                channelFlags, channelTypeTlv, upfrontShutdownScriptTlv);

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
    /// <c>openchannel --dual-fund</c>: <c>open_channel2</c> with <see cref="OpenChannelClientRequest.FundingAmount"/>
    /// as our contribution (BOLT 2 "Channel Establishment v2"); completes once both <c>tx_signatures</c> were exchanged.
    /// </summary>
    private async Task<OpenChannelClientResponse> OpenDualFundedAsync(OpenChannelClientRequest request,
                                                                     CompactPubKey peerId, CancellationToken ct)
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
        try
        {
            result = await _dualFundedOpenService.OpenAsync(
                         new DualFundedOpenRequest(peerId, request.FundingAmount,
                                                   request.FeeRatePerKw is { } feerate ? (uint)feerate.Satoshi : null,
                                                   null, request.IsPublic), ct);
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
            FundingOutputIndex = outputIndex
        };
    }

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

        tsc.TrySetException(new ChannelErrorException($"Error opening channel: {args.Message}"));
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