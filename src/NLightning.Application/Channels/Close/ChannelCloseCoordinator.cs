using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoinTransaction = NBitcoin.Transaction;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Services;
using Simple;

/// <summary>
/// The mutual close of one channel (BOLT 2 "Channel Close", legacy <c>closing_signed</c>; BOLT2 plan N10-T3):
/// <c>shutdown</c> in both directions, the move to <see cref="ChannelState.Negotiating"/> once no HTLC or update is
/// left, the fee negotiation (<see cref="LegacyClosingNegotiator"/>), and the agreed closing transaction, which is
/// persisted (<see cref="ChannelState.Closing"/>) before it is sent or broadcast.
/// </summary>
/// <remarks>
/// Scoped (one per message or IPC call); every method runs under the channel's lock and returns the messages to send
/// to the peer in wire order. Every state change is saved before the message that reveals it goes out (invariant I1).
/// </remarks>
public sealed class ChannelCloseCoordinator
{
    /// <summary>BOLT 3's feerate floor, used as the lowest closing fee we propose or accept.</summary>
    public const uint MinFeeratePerKw = 253;

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IClosingTransactionBuilder _closingTransactionBuilder;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IFeeService _feeService;
    private readonly ClosingFeeEstimator _feeEstimator;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelCloseCoordinator> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly ChannelCloseOptions _options;
    private readonly ClosingNegotiationRegistry _registry;
    private readonly ShutdownScriptProvider _shutdownScriptProvider;
    private readonly SimpleCloseCoordinator _simpleClose;
    private readonly ClosingTimeoutMonitor? _timeouts;
    private readonly ChannelStateTransitionService _transitions;
    private readonly IUnitOfWork _unitOfWork;

    public ChannelCloseCoordinator(IClosingTransactionBuilder closingTransactionBuilder,
                                   IChannelMemoryRepository channelMemoryRepository, IFeeService feeService,
                                   ILightningSigner lightningSigner, ILogger<ChannelCloseCoordinator> logger,
                                   IMessageFactory messageFactory, IOptions<ChannelCloseOptions> options,
                                   ClosingNegotiationRegistry registry, ShutdownScriptProvider shutdownScriptProvider,
                                   ChannelStateTransitionService transitions, IUnitOfWork unitOfWork,
                                   IBlockchainMonitor? blockchainMonitor = null,
                                   ClosingTimeoutMonitor? timeouts = null,
                                   ClosingFeeEstimator? feeEstimator = null,
                                   SimpleCloseCoordinator? simpleClose = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _timeouts = timeouts;
        _closingTransactionBuilder = closingTransactionBuilder;
        _channelMemoryRepository = channelMemoryRepository;
        _feeService = feeService;
        _feeEstimator = feeEstimator
                     ?? new ClosingFeeEstimator(feeService, options, NullLogger<ClosingFeeEstimator>.Instance);
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _options = options.Value;
        _registry = registry;
        _shutdownScriptProvider = shutdownScriptProvider;
        _transitions = transitions;
        _unitOfWork = unitOfWork;
        _simpleClose = simpleClose
                    ?? new SimpleCloseCoordinator(closingTransactionBuilder, channelMemoryRepository, feeService,
                                                  lightningSigner, NullLogger<SimpleCloseCoordinator>.Instance,
                                                  options, registry, unitOfWork, blockchainMonitor, _feeEstimator);
    }

    /// <summary>True for the states in which the close still needs the peer (shutdown sent or received).</summary>
    public static bool IsNegotiationPhase(ChannelState state) =>
        state is ChannelState.ShuttingDown or ChannelState.Negotiating;

    #region Our shutdown

    /// <summary>
    /// Starts the close (IPC): signs our pending updates first (B2-SHUT-S03: no <c>shutdown</c> while updates are
    /// pending on the peer's commitment), then persists and returns our <c>shutdown</c>. A channel whose
    /// <c>shutdown</c> was already sent returns nothing (B2-SHUT-S04: only once).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not open, failed, lost data, or pending updates wait for a
    /// <c>revoke_and_ack</c> before they can be signed.</exception>
    public async Task<List<IChannelMessage>> InitiateAsync(ChannelModel channel, ChannelCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);
        _registry.Get(channel.ChannelId).Request = request;

        if (channel.LocalShutdownScript is not null)
            return [];

        // B2-SHUT-S01: shutdown only after funding_created/funding_signed; we also wait for channel_ready (N10 scope)
        if (channel.State != ChannelState.Open)
            throw new InvalidOperationException(
                $"Channel {channel.ChannelId} is {Enum.GetName(channel.State)}; only an open channel can be closed");
        if (channel.DataLossDetected)
            throw new InvalidOperationException($"Channel {channel.ChannelId} lost data; it can't be closed mutually");

        var replies = new List<IChannelMessage>();
        if (channel.Commitments is { HasPendingChangesForRemote: true } commitments)
        {
            if (!commitments.CanSendCommit)
                throw new InvalidOperationException(
                    $"Channel {channel.ChannelId} has updates waiting for the peer's revoke_and_ack; try again");

            if (await _transitions.SignIfPendingAsync(channel) is { } commitmentSigned)
                replies.Add(commitmentSigned);
        }

        replies.Add(await SendShutdownAsync(channel));
        return replies;
    }

    /// <summary>
    /// Our <c>shutdown</c> again, for the <c>channel_reestablish</c> of a new connection (B2-RE-28), or null when we
    /// never sent one.
    /// </summary>
    public ShutdownMessage? CreateShutdownResend(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.LocalShutdownScript is not { } script)
            return null;

        _registry.Get(channel.ChannelId).ShutdownSentOnConnection = true;
        return _messageFactory.CreateShutdownMessage(channel.ChannelId, script);
    }

    private async Task<ShutdownMessage> SendShutdownAsync(ChannelModel channel)
    {
        var script = await _shutdownScriptProvider.GetLocalScriptAsync(channel);
        channel.SetLocalShutdownScript(script);
        if (channel.State < ChannelState.ShuttingDown)
            channel.UpdateState(ChannelState.ShuttingDown);
        await PersistAsync(channel);
        WatchFundingSpend(channel);

        _registry.Get(channel.ChannelId).ShutdownSentOnConnection = true;
        _logger.LogInformation("Sending shutdown for channel {ChannelId} to {Script}", channel.ChannelId, script);
        return _messageFactory.CreateShutdownMessage(channel.ChannelId, script);
    }

    #endregion

    #region Peer's shutdown

    /// <summary>
    /// The peer's <c>shutdown</c> (B2-SHUT-R01..R06): checks the script (form, upfront script), persists it and
    /// <see cref="ChannelState.ShuttingDown"/>, and replies with ours once none of our updates is unsigned (signing
    /// them first when possible, B2-SHUT-R04). The same <c>shutdown</c> again (after a reconnection) is accepted.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveShutdownAsync(ChannelModel channel,
                                                                           ShutdownMessage message,
                                                                           FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        var channelId = channel.ChannelId;
        var script = message.Payload.ScriptPubkey;
        var simpleClose = SimpleCloseCoordinator.IsNegotiated(negotiatedFeatures);
        _registry.Get(channelId).SimpleClose = simpleClose;

        if (channel.State is < ChannelState.Open or ChannelState.Closing or ChannelState.Closed)
        {
            if (channel.State is ChannelState.Closing or ChannelState.Closed)
            {
                // option_simple_close: a closing channel still signs the peer's new closing_complete (RBF) and may
                // send its own, once both shutdowns went over this connection
                if (simpleClose && channel.State == ChannelState.Closing)
                    _registry.Get(channelId).ShutdownReceivedOnConnection = true;
                _logger.LogInformation("Ignoring shutdown for channel {ChannelId}: its closing transaction is out",
                                       channelId);
                return [];
            }

            // BOLT 2 allows closing before channel_ready (B2-SHUT-R03, MAY); not supported yet
            throw new ChannelWarningException(
                $"shutdown on channel {channelId} in state {Enum.GetName(channel.State)} is not supported", channelId,
                "shutdown before channel_ready is not supported, message ignored");
        }

        // B2-SHUT-R02
        var anySegwit = negotiatedFeatures.BeyondSegwitShutdown > FeatureSupport.No;
        if (!ShutdownScriptValidator.IsValid((byte[])script, anySegwit, simpleClose))
            throw new ChannelWarningException($"[B2-SHUT-R02] shutdown script {script} is not allowed", channelId,
                                              "shutdown scriptpubkey is not a valid form");

        // B2-SHUT-R05: the upfront shutdown script we received binds the peer
        if (negotiatedFeatures.UpfrontShutdownScript > FeatureSupport.No
         && channel.RemoteUpfrontShutdownScript is { Length: > 0 } upfront && upfront != script)
            throw new ChannelWarningException(
                $"[B2-SHUT-R05] shutdown script {script} differs from the upfront script {upfront}", channelId,
                "shutdown scriptpubkey differs from upfront_shutdown_script")
            {
                CloseConnection = true
            };

        // option_simple_close lets the peer change its script (closing_complete, then its shutdown after a
        // reconnection); the legacy close does not
        var changedScript = channel.RemoteShutdownScript is { } previous && previous != script;
        if (changedScript && !simpleClose)
            throw new ChannelWarningException(
                $"shutdown script {script} differs from the one received before ({previous})", channelId,
                "shutdown scriptpubkey changed")
            {
                CloseConnection = true
            };

        var entry = _registry.Get(channelId);
        entry.ShutdownReceivedOnConnection = true;
        // The negotiation that follows needs a fee estimate: fetch it now, without waiting under the lock
        _ = _feeEstimator.StartFetchIfDue();
        if (changedScript)
        {
            channel.ReplaceRemoteShutdownScript(script);
            await PersistAsync(channel);
            _logger.LogInformation("Peer {Peer} changed the shutdown script of channel {ChannelId} to {Script}",
                                   channel.RemoteNodeId, channelId, script);
        }
        else if (channel.RemoteShutdownScript is null)
        {
            channel.SetRemoteShutdownScript(script);
            if (channel.State < ChannelState.ShuttingDown)
                channel.UpdateState(ChannelState.ShuttingDown);
            await PersistAsync(channel);
            WatchFundingSpend(channel);
            _logger.LogInformation("Peer {Peer} sent shutdown for channel {ChannelId} to {Script}",
                                   channel.RemoteNodeId, channelId, script);
        }

        if (channel.LocalShutdownScript is not null)
            return [];

        // B2-SHUT-R04: reply once no update of ours is unsigned; sign them first when we can
        var replies = new List<IChannelMessage>();
        if (channel.Commitments is { HasPendingChangesForRemote: true } commitments)
        {
            if (!commitments.CanSendCommit)
            {
                _logger.LogInformation(
                    "Deferring our shutdown for channel {ChannelId} until our updates are signed", channelId);
                return [];
            }

            if (await _transitions.SignIfPendingAsync(channel) is { } commitmentSigned)
                replies.Add(commitmentSigned);
        }

        replies.Add(await SendShutdownAsync(channel));
        return replies;
    }

    #endregion

    #region Negotiation

    /// <summary>
    /// Moves the close on after a message or an IPC call (under the channel's lock): our deferred <c>shutdown</c> once
    /// our updates are signed, <see cref="ChannelState.Negotiating"/> once both <c>shutdown</c>s are known and nothing
    /// is left in either commitment, and, as the funder, the opening <c>closing_signed</c> once both <c>shutdown</c>s
    /// were exchanged on the current connection (B2-CLS-01; a reconnection restarts it, B2-RE-29).
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> AdvanceAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var messages = new List<IChannelMessage>();

        if (channel is { State: ChannelState.ShuttingDown, RemoteShutdownScript: not null, LocalShutdownScript: null }
         && channel.Commitments is not { HasPendingChangesForRemote: true })
            messages.Add(await SendShutdownAsync(channel));

        await MoveToNegotiatingIfClearedAsync(channel);

        // option_simple_close (N11): each side sends its own closing_complete; no closing_signed
        if (_registry.Get(channel.ChannelId).SimpleClose)
        {
            if (await _simpleClose.ProposeIfDueAsync(channel) is { } closingComplete)
                messages.Add(closingComplete);
            return messages;
        }

        if (channel is { State: ChannelState.Negotiating, IsInitiator: true })
        {
            var entry = _registry.Get(channel.ChannelId);
            if (entry is { ShutdownSentOnConnection: true, ShutdownReceivedOnConnection: true, Negotiation: null })
            {
                await ResolveEstimateAsync(entry);
                var context = BuildContext(channel, entry);
                var (decision, next) = LegacyClosingNegotiator.Open(context.InitialState, context.IdealFeeSat);
                messages.Add(CreateClosingSigned(channel, context, decision.FeeSat, decision.FeeRange));
                entry.Negotiation = next;
                _timeouts?.ArmReply(channel.ChannelId);
                _logger.LogInformation(
                    "Proposing a closing fee of {Fee} sat for channel {ChannelId} (range {Range}, estimate {Ideal})",
                    decision.FeeSat, channel.ChannelId, decision.FeeRange?.ToString() ?? "none",
                    context.IdealFeeSat);
            }
        }

        return messages;
    }

    /// <summary>
    /// The peer's <c>closing_signed</c> (B2-CLS-R01..R10): the signature must be valid for one variant of the closing
    /// transaction, no output may be below its script's dust threshold, then the negotiator decides. On agreement the
    /// fully signed transaction is persisted with <see cref="ChannelState.Closing"/> before our echo is sent and the
    /// transaction broadcast and watched.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveClosingSignedAsync(ChannelModel channel,
                                                                                ClosingSignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var channelId = channel.ChannelId;

        // option_simple_close replaces closing_signed (BOLT 2: the legacy negotiation applies only without it)
        if (_registry.Get(channelId).SimpleClose)
            throw new ChannelWarningException(
                $"closing_signed on channel {channelId}, which closes with option_simple_close", channelId,
                "closing_signed while option_simple_close is negotiated, message ignored");

        if (channel.State is ChannelState.Closing or ChannelState.Closed)
        {
            // The peer restarted the negotiation after a reconnection (our final closing_signed may have been lost):
            // answer with the agreed fee and our signature of the stored transaction
            if (channel.State == ChannelState.Closing
             && CreateAgreedClosingSigned(channel, (ulong)message.Payload.FeeAmount.Satoshi) is { } agreed)
            {
                _logger.LogInformation(
                    "closing_signed for closing channel {ChannelId}: answering with the agreed fee of {Fee} sat",
                    channelId, (ulong)agreed.Payload.FeeAmount.Satoshi);
                return [agreed];
            }

            _logger.LogInformation("Ignoring closing_signed for channel {ChannelId}: the closing transaction is out",
                                   channelId);
            return [];
        }

        await MoveToNegotiatingIfClearedAsync(channel);
        if (channel.State != ChannelState.Negotiating)
            throw new ChannelWarningException(
                $"closing_signed on channel {channelId} in state {Enum.GetName(channel.State)}", channelId,
                "closing_signed before both shutdowns and with updates pending")
            {
                CloseConnection = true
            };

        var entry = _registry.Get(channelId);
        entry.ReplyDueAt = null; // any closing_signed answers ours (B2-CLS-03)
        await ResolveEstimateAsync(entry);
        var context = BuildContext(channel, entry);
        var feeSat = (ulong)message.Payload.FeeAmount.Satoshi;
        if (feeSat > context.MaxFeeSat)
            throw new ChannelWarningException(
                $"closing_signed fee {feeSat} sat is above the funder's balance of {context.MaxFeeSat} sat", channelId,
                "fee_satoshis above the funder's balance")
            {
                CloseConnection = true
            };

        ClosingFeeRange? theirRange = null;
        if (message.FeeRangeTlv is { } rangeTlv)
        {
            var (min, max) = ((ulong)rangeTlv.MinFeeAmount.Satoshi, (ulong)rangeTlv.MaxFeeAmount.Satoshi);
            if (min > max)
                throw new ChannelWarningException($"fee_range [{min}, {max}] is inverted", channelId,
                                                  "fee_range min_fee_satoshis above max_fee_satoshis")
                {
                    CloseConnection = true
                };
            theirRange = new ClosingFeeRange(min, max);
        }

        // B2-CLS-R01: the signature must be valid for a variant of the closing transaction
        var peerSignature = message.Payload.Signature;
        var signed = FindSignedVariant(channel, context, feeSat, peerSignature)
                  ?? throw new ChannelWarningException(
                         $"[B2-CLS-R01] closing_signed signature for {feeSat} sat is valid for no closing transaction",
                         channelId, "invalid closing_signed signature")
                  {
                      CloseConnection = true
                  };

        // B2-CLS-R10: an output below its script's dust threshold would not relay
        foreach (var output in signed.Model.Outputs)
        {
            var threshold = ShutdownScriptValidator.GetDustThresholdSat((byte[])output.ScriptPubKey);
            if ((ulong)output.Amount.Satoshi < threshold)
            {
                ClearDeadlines(entry);
                throw new ChannelFailedException(
                    channelId,
                    $"[B2-CLS-R10] closing output of {output.Amount.Satoshi} sat is below the {threshold} sat dust threshold of its script",
                    "closing transaction output below dust")
                {
                    RequirementId = "B2-CLS-R10"
                };
            }
        }

        var state = entry.Negotiation ?? context.InitialState;
        var (decision, next) = LegacyClosingNegotiator.Receive(state, feeSat, theirRange, context.IdealFeeSat);
        entry.Negotiation = next;
        _logger.LogInformation(
            "closing_signed for channel {ChannelId}: {Fee} sat (range {Range}) -> {Decision} {DecisionFee} sat [{Requirement}]",
            channelId, feeSat, theirRange?.ToString() ?? "none", decision.Kind, decision.FeeSat,
            decision.RequirementId);

        // B2-CLS-R04: a range that doesn't overlap ours starts the wait for a satisfying one; any other outcome ends it
        if (decision is { Kind: ClosingDecisionKind.Warn, RequirementId: "B2-CLS-R04" })
            _timeouts?.ArmFeeRange(channelId);
        else if (decision.Kind != ClosingDecisionKind.Warn)
            entry.FeeRangeDueAt = null;

        switch (decision.Kind)
        {
            case ClosingDecisionKind.Warn:
                throw new ChannelWarningException($"[{decision.RequirementId}] {decision.Reason}", channelId,
                                                  decision.Reason)
                {
                    CloseConnection = decision.CloseConnection
                };
            case ClosingDecisionKind.Fail:
                ClearDeadlines(entry);
                throw new ChannelFailedException(channelId, $"[{decision.RequirementId}] {decision.Reason}",
                                                 $"closing negotiation failed: {decision.Reason}")
                {
                    RequirementId = decision.RequirementId
                };
            case ClosingDecisionKind.Propose:
                var proposal = CreateClosingSigned(channel, context, decision.FeeSat, decision.FeeRange);
                _timeouts?.ArmReply(channelId);
                return [proposal];
            default:
                return await FinalizeAsync(channel, context, signed, peerSignature, decision);
        }
    }

    /// <summary>
    /// Both sides signed the same closing transaction: sign it, persist it with <see cref="ChannelState.Closing"/>
    /// (before anything reveals it), complete the IPC waiters, broadcast and watch it, and return our echo if one is
    /// due.
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> FinalizeAsync(ChannelModel channel, CloseContext context,
                                                                     SignedVariant signed,
                                                                     CompactSignature peerSignature,
                                                                     ClosingDecision decision)
    {
        var ourSignature = _lightningSigner.SignChannelTransaction(channel.ChannelId, signed.Transaction);
        var closingTransaction = _closingTransactionBuilder.AddWitness(signed.Transaction, context.Funding,
                                                                       ourSignature, peerSignature);

        channel.SetClosingTransaction(closingTransaction);
        channel.UpdateState(ChannelState.Closing);

        // The watch is saved in the same save as Closing, so no crash leaves a Closing channel without it
        var watch = StageClosingWatch(channel, closingTransaction.TxId);
        await PersistAsync(channel);
        if (watch is not null)
            _blockchainMonitor?.TrackWatchedTransaction(watch);
        var entry = _registry.Get(channel.ChannelId);
        ClearDeadlines(entry);
        entry.CompleteWaiters(closingTransaction.TxId);
        _logger.LogInformation("Channel {ChannelId} agreed on closing transaction {TxId} with a fee of {Fee} sat",
                               channel.ChannelId, closingTransaction.TxId, decision.FeeSat);

        IReadOnlyList<IChannelMessage> replies = decision.Reply
                                                     ? [CreateClosingSigned(channel.ChannelId, decision.FeeSat,
                                                                            decision.FeeRange, ourSignature)]
                                                     : [];

        await BroadcastAsync(channel, closingTransaction);
        return replies;
    }

    /// <summary>
    /// The watch of the closing transaction, staged on this unit of work (saved with <see cref="ChannelState.Closing"/>),
    /// or null without a blockchain monitor.
    /// </summary>
    private WatchedTransactionModel? StageClosingWatch(ChannelModel channel, TxId closingTxId)
    {
        if (_blockchainMonitor is null)
            return null;

        var watch = new WatchedTransactionModel(channel.ChannelId, closingTxId, _options.ConfirmationDepth);
        _unitOfWork.WatchedTransactionDbRepository.Add(watch);
        return watch;
    }

    /// <summary>
    /// Publishes the closing transaction (its watch was saved with Closing). A failed broadcast is logged: the peer
    /// broadcasts the same transaction, and it is stored for a rebroadcast at startup.
    /// </summary>
    private async Task BroadcastAsync(ChannelModel channel, SignedTransaction closingTransaction)
    {
        if (_blockchainMonitor is null)
            return;

        try
        {
            await _blockchainMonitor.PublishTransactionAsync(closingTransaction);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Broadcasting closing transaction {TxId} of channel {ChannelId} failed",
                               closingTransaction.TxId, channel.ChannelId);
        }
    }

    private async Task MoveToNegotiatingIfClearedAsync(ChannelModel channel)
    {
        if (channel is not
            { State: ChannelState.ShuttingDown, LocalShutdownScript: not null, RemoteShutdownScript: not null }
         || channel.Commitments is { IsCleared: false })
            return;

        channel.UpdateState(ChannelState.Negotiating);
        await PersistAsync(channel);
        _logger.LogInformation("Channel {ChannelId} has no HTLC left; negotiating the closing fee", channel.ChannelId);
    }

    #endregion

    #region option_simple_close

    /// <summary>
    /// The peer's <c>closing_complete</c> (BOLT 2 <c>option_simple_close</c>, N11): moves a cleared ShuttingDown
    /// channel on to Negotiating, then <see cref="SimpleCloseCoordinator.ReceiveClosingCompleteAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveClosingCompleteAsync(ChannelModel channel,
        ClosingCompleteMessage message, FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        _registry.Get(channel.ChannelId).SimpleClose = SimpleCloseCoordinator.IsNegotiated(negotiatedFeatures);
        await MoveToNegotiatingIfClearedAsync(channel);
        return await _simpleClose.ReceiveClosingCompleteAsync(channel, message, negotiatedFeatures);
    }

    /// <summary>The peer's <c>closing_sig</c>: <see cref="SimpleCloseCoordinator.ReceiveClosingSigAsync"/>.</summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveClosingSigAsync(ChannelModel channel,
                                                                            ClosingSigMessage message,
                                                                            FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        _registry.Get(channel.ChannelId).SimpleClose = SimpleCloseCoordinator.IsNegotiated(negotiatedFeatures);
        return await _simpleClose.ReceiveClosingSigAsync(channel, message, negotiatedFeatures);
    }

    /// <summary>
    /// A new <c>closing_complete</c> of ours at <paramref name="feeratePerKw"/> (RBF, IPC): see
    /// <see cref="SimpleCloseCoordinator.BumpAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> BumpSimpleCloseAsync(ChannelModel channel, uint feeratePerKw)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await MoveToNegotiatingIfClearedAsync(channel);
        return [await _simpleClose.BumpAsync(channel, feeratePerKw)];
    }

    #endregion

    #region Transactions

    /// <summary>What every closing transaction of this channel is built from.</summary>
    private sealed record CloseContext(FundingOutputInfo Funding, ulong LocalBalanceMsat, ulong RemoteBalanceMsat,
                                       bool IsFunder, BitcoinScript LocalScript, BitcoinScript RemoteScript,
                                       ulong LocalDustLimitSat, ulong RemoteDustLimitSat, ulong IdealFeeSat,
                                       ulong MaxFeeSat, ClosingNegotiation InitialState);

    private sealed record SignedVariant(ClosingTransactionModel Model, SignedTransaction Transaction);

    private CloseContext BuildContext(ChannelModel channel, ClosingNegotiationRegistry.Entry entry)
    {
        var funding = channel.FundingOutput
                   ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding output");
        var localScript = channel.LocalShutdownScript
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no shutdown script");
        var remoteScript = channel.RemoteShutdownScript
                        ?? throw new InvalidOperationException(
                               $"Channel {channel.ChannelId} has no shutdown script from the peer");

        var localMsat = channel.Commitments?.LocalBalanceMsat ?? channel.LocalBalance.MilliSatoshi;
        var remoteMsat = channel.Commitments?.RemoteBalanceMsat ?? channel.RemoteBalance.MilliSatoshi;
        var isFunder = channel.IsInitiator;
        var maxFee = LegacyClosingTransactionFactory.MaxFeeSat(localMsat, remoteMsat, isFunder);

        var weight = ClosingFeeCalculator.EstimateWeight(localScript.Length, remoteScript.Length);
        var feerate = Math.Max(entry.Request?.FeeRatePerKw
                            ?? entry.EstimateFeeratePerKw
                            ?? (ulong)_feeService.GetCachedFeeRatePerKw().Satoshi,
                               MinFeeratePerKw);
        var floor = Math.Min(ClosingFeeCalculator.FeeSat(MinFeeratePerKw, weight), maxFee);
        var ideal = Math.Clamp(ClosingFeeCalculator.FeeSat(feerate, weight), floor, maxFee);

        // B2-CLS-02/04: the funder pays up to a multiple of its estimate; the non-funder accepts anything from the
        // relay floor up to what the funder can pay
        var acceptableMax = isFunder
                                ? Math.Clamp(ideal * Math.Max(_options.MaxFeeMultiplier, 1), floor, maxFee)
                                : maxFee;
        // fee_range goes out unless the node options or the IPC request turn it off
        var initial = new ClosingNegotiation
        {
            IsFunder = isFunder,
            Acceptable = new ClosingFeeRange(floor, acceptableMax),
            SendFeeRange = _options.SendFeeRange && (entry.Request?.SendFeeRange ?? true)
        };

        return new CloseContext(funding, localMsat, remoteMsat, isFunder, localScript, remoteScript,
                                (ulong)channel.ChannelParams.Local.DustLimitAmount.Satoshi,
                                (ulong)channel.ChannelParams.Remote.DustLimitAmount.Satoshi, ideal, maxFee, initial);
    }

    /// <summary>
    /// Reads our fee estimate once per negotiation (per connection) into the registry entry, from the process-wide
    /// <see cref="ClosingFeeEstimator"/>. Only the first message of a negotiation may wait for a fetch, and at most
    /// <see cref="ChannelCloseOptions.FeeEstimateWaitUnderLock"/> (this runs under the channel's lock, in the peer's
    /// inbound loop); later ones take whatever the estimator has by then. Without one, <see cref="BuildContext"/> uses
    /// the fee service's cached value, then the floor. (The host's fee service is a transient typed HttpClient, so the
    /// cached value alone made every close propose the 253 sat/kw floor, W4-E.)
    /// </summary>
    private async Task ResolveEstimateAsync(ClosingNegotiationRegistry.Entry entry)
    {
        if (entry.EstimateFeeratePerKw is not null || entry.Request?.FeeRatePerKw is not null)
            return;

        ulong? estimate;
        if (entry.EstimateAttempted)
        {
            estimate = _feeEstimator.Latest;
        }
        else
        {
            entry.EstimateAttempted = true;
            estimate = await _feeEstimator.GetUnderLockAsync();
        }

        if (estimate > 0)
            entry.EstimateFeeratePerKw = estimate;
    }

    /// <summary>No closing deadline applies any more (agreement, or the channel failed out of the negotiation).</summary>
    private static void ClearDeadlines(ClosingNegotiationRegistry.Entry entry)
    {
        entry.ReplyDueAt = null;
        entry.FeeRangeDueAt = null;
    }

    /// <summary>Our <c>closing_signed</c> at <paramref name="feeSat"/>: our variant (our dust limit), signed.</summary>
    private ClosingSignedMessage CreateClosingSigned(ChannelModel channel, CloseContext context, ulong feeSat,
                                                     ClosingFeeRange? feeRange)
    {
        var model = LegacyClosingTransactionFactory.Create(context.Funding, context.LocalBalanceMsat,
                                                           context.RemoteBalanceMsat, context.IsFunder, feeSat,
                                                           context.LocalScript, context.RemoteScript,
                                                           context.LocalDustLimitSat);
        var unsigned = _closingTransactionBuilder.Build(model);
        var signature = _lightningSigner.SignChannelTransaction(channel.ChannelId, unsigned);
        return CreateClosingSigned(channel.ChannelId, feeSat, feeRange, signature);
    }

    private static ClosingSignedMessage CreateClosingSigned(Domain.Channels.ValueObjects.ChannelId channelId,
                                                            ulong feeSat, ClosingFeeRange? feeRange,
                                                            CompactSignature signature)
    {
        var payload = new ClosingSignedPayload(channelId, LightningMoney.Satoshis(feeSat), signature);
        var rangeTlv = feeRange is null
                           ? null
                           : new FeeRangeTlv(LightningMoney.Satoshis(feeRange.MinFeeSat),
                                             LightningMoney.Satoshis(feeRange.MaxFeeSat));
        return new ClosingSignedMessage(payload, rangeTlv);
    }

    /// <summary>
    /// The closing transaction the peer's signature is valid for (B2-CLS-R01, "either variant"): outputs trimmed by
    /// the peer's dust limit, the same without the peer's output (BOLT 3: it MAY eliminate its own), or trimmed by our
    /// dust limit. Null when the signature fits none of them.
    /// </summary>
    private SignedVariant? FindSignedVariant(ChannelModel channel, CloseContext context, ulong feeSat,
                                             CompactSignature signature)
    {
        foreach (var (model, unsigned) in BuildVariants(context, feeSat))
        {
            try
            {
                _lightningSigner.ValidateSignature(channel.ChannelId, signature, unsigned);
                return new SignedVariant(model, unsigned);
            }
            catch (SignerException)
            {
                // Not this variant
            }
        }

        return null;
    }

    /// <summary>
    /// The distinct closing transactions at <paramref name="feeSat"/> either side may have signed (B2-CLS-R01): trimmed
    /// by the peer's dust limit, the same without the peer's output, or trimmed by our dust limit.
    /// </summary>
    private IEnumerable<(ClosingTransactionModel Model, SignedTransaction Unsigned)> BuildVariants(
        CloseContext context, ulong feeSat)
    {
        var seen = new HashSet<TxId>();
        foreach (var (dustLimit, variant) in new[]
                 {
                     (context.RemoteDustLimitSat, ClosingVariant.Full),
                     (context.RemoteDustLimitSat, ClosingVariant.WithoutRemoteOutput),
                     (context.LocalDustLimitSat, ClosingVariant.Full)
                 })
        {
            ClosingTransactionModel model;
            try
            {
                model = LegacyClosingTransactionFactory.Create(context.Funding, context.LocalBalanceMsat,
                                                               context.RemoteBalanceMsat, context.IsFunder, feeSat,
                                                               context.LocalScript, context.RemoteScript, dustLimit,
                                                               variant);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
            {
                continue;
            }

            var unsigned = _closingTransactionBuilder.Build(model);
            if (seen.Add(unsigned.TxId))
                yield return (model, unsigned);
        }
    }

    /// <summary>
    /// Our <c>closing_signed</c> for the stored closing transaction (its fee, our signature of it), or null when we
    /// already answered that fee on this connection, or the stored transaction is none of our variants (e.g. recorded
    /// from the chain).
    /// </summary>
    private ClosingSignedMessage? CreateAgreedClosingSigned(ChannelModel channel, ulong receivedFeeSat)
    {
        if (channel is not { ClosingTransaction: { } stored, FundingOutput: { } funding }
         || channel.LocalShutdownScript is null || channel.RemoteShutdownScript is null)
            return null;

        ulong feeSat;
        try
        {
            var outputsSat = NBitcoinTransaction.Load(stored.RawTxBytes, NBitcoin.Network.Main).Outputs
                                                .Sum(o => o.Value.Satoshi);
            feeSat = (ulong)(funding.Amount.Satoshi - outputsSat);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The stored closing transaction of channel {ChannelId} can't be read",
                               channel.ChannelId);
            return null;
        }

        // The peer sending our fee is either its echo (nothing to answer) or its restart after it lost ours: answered
        // once per connection, which also ends a crossing of two echoes
        var entry = _registry.Get(channel.ChannelId);
        if (feeSat == receivedFeeSat && entry.AgreedClosingSignedSentOnConnection)
            return null;

        var context = BuildContext(channel, entry);
        foreach (var (_, unsigned) in BuildVariants(context, feeSat))
        {
            if (unsigned.TxId != stored.TxId)
                continue;

            var signature = _lightningSigner.SignChannelTransaction(channel.ChannelId, unsigned);
            if (feeSat == receivedFeeSat)
                entry.AgreedClosingSignedSentOnConnection = true;
            return CreateClosingSigned(channel.ChannelId, feeSat, null, signature);
        }

        return null;
    }

    #endregion

    /// <summary>
    /// From the first <c>shutdown</c> on, every proposal we sign can be broadcast by the peer, so a spend of the funding
    /// output is watched (<c>ChannelManager</c> records a mutual close it did not agree on this connection, e.g. when
    /// the peer's final <c>closing_signed</c> was lost). <c>ChannelManager</c> registers it again at startup.
    /// </summary>
    private void WatchFundingSpend(ChannelModel channel)
    {
        if (channel.FundingOutput is { TransactionId: { } fundingTxId, Index: { } fundingIndex })
            _blockchainMonitor?.WatchOutpointSpend(channel.ChannelId, fundingTxId, fundingIndex);
    }

    private async Task PersistAsync(ChannelModel channel)
    {
        await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await _unitOfWork.SaveChangesAsync();
        _channelMemoryRepository.UpdateChannel(channel);
    }
}