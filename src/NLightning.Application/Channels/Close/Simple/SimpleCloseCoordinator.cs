using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Close.Simple;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The <c>option_simple_close</c> negotiation of one channel (BOLT 2 "Closing Negotiation: closing_complete and
/// closing_sig", BOLT2 plan N11-T2): each side proposes its own closing transaction, pays its fee, and the other side
/// signs it. Runs after both <c>shutdown</c>s, once nothing is left in either commitment
/// (<see cref="ChannelState.Negotiating"/>); the first fully signed transaction (ours completed by a
/// <c>closing_sig</c>, or the peer's we signed) moves the channel to <see cref="ChannelState.Closing"/>. A channel in
/// Closing still signs a new <c>closing_complete</c> of the peer and sends one of its own on request (RBF); the last
/// signed transaction is the stored one, and whichever of them confirms closes the channel (ChannelManager records a
/// spend of the funding output by another one of them).
/// </summary>
/// <remarks>
/// Scoped; every method runs under the channel's lock and returns the messages to send in wire order. Each fully
/// signed transaction is persisted, with its watch, before the message that reveals it goes out and before it is
/// broadcast (invariant I1). Protocol failures send a <c>warning</c> and close the connection (BOLT 2 allows that or
/// failing the channel for every closing_complete/closing_sig rule): failing would broadcast our commitment while a
/// mutual close is still possible.
/// </remarks>
public sealed class SimpleCloseCoordinator
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IClosingTransactionBuilder _closingTransactionBuilder;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ClosingFeeEstimator _feeEstimator;
    private readonly IFeeService _feeService;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<SimpleCloseCoordinator> _logger;
    private readonly ChannelCloseOptions _options;
    private readonly ClosingNegotiationRegistry _registry;
    private readonly IUnitOfWork _unitOfWork;

    public SimpleCloseCoordinator(IClosingTransactionBuilder closingTransactionBuilder,
                                  IChannelMemoryRepository channelMemoryRepository, IFeeService feeService,
                                  ILightningSigner lightningSigner, ILogger<SimpleCloseCoordinator> logger,
                                  IOptions<ChannelCloseOptions> options, ClosingNegotiationRegistry registry,
                                  IUnitOfWork unitOfWork, IBlockchainMonitor? blockchainMonitor = null,
                                  ClosingFeeEstimator? feeEstimator = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _closingTransactionBuilder = closingTransactionBuilder;
        _channelMemoryRepository = channelMemoryRepository;
        _feeEstimator = feeEstimator
                     ?? new ClosingFeeEstimator(feeService, options, NullLogger<ClosingFeeEstimator>.Instance);
        _feeService = feeService;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _options = options.Value;
        _registry = registry;
        _unitOfWork = unitOfWork;
    }

    /// <summary>True when <paramref name="negotiatedFeatures"/> has <c>option_simple_close</c>.</summary>
    public static bool IsNegotiated(FeatureOptions negotiatedFeatures) =>
        negotiatedFeatures is { OptionSimpleClose: > FeatureSupport.No };

    #region Our closing_complete

    /// <summary>
    /// Our own <c>closing_complete</c> once due (B2-SC-01): simple close negotiated, both <c>shutdown</c>s exchanged
    /// on the current connection, the channel <see cref="ChannelState.Negotiating"/>, none of ours outstanding and none
    /// sent on this connection yet. Null when not due or when our balance can't pay a relayable fee (the peer's own
    /// proposal closes the channel then).
    /// </summary>
    public async Task<ClosingCompleteMessage?> ProposeIfDueAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var entry = _registry.Get(channel.ChannelId);
        if (channel.State != ChannelState.Negotiating
         || entry is not
         {
             SimpleClose: true, ShutdownSentOnConnection: true, ShutdownReceivedOnConnection: true,
             SimpleProposal: null, SimpleProposalSentOnConnection: false
         })
            return null;

        var message = await CreateProposalAsync(channel, entry, entry.Request?.FeeRatePerKw);
        entry.SimpleProposalSentOnConnection = true;
        return message;
    }

    /// <summary>
    /// A new <c>closing_complete</c> of ours at <paramref name="feeratePerKw"/> (RBF of our closing transaction, BOLT 2
    /// "MAY send another closing_complete", IPC <c>closechannel</c> with a feerate on a closing channel).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not negotiated, not ready, or our previous one is not answered yet
    /// (B2-SC-C09).</exception>
    public async Task<ClosingCompleteMessage> BumpAsync(ChannelModel channel, uint feeratePerKw)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var entry = _registry.Get(channel.ChannelId);
        if (!entry.SimpleClose)
            throw new InvalidOperationException(
                $"Channel {channel.ChannelId} does not use option_simple_close; its fee can't be bumped");
        if (channel.State is not (ChannelState.Negotiating or ChannelState.Closing)
         || !entry.ShutdownSentOnConnection || !entry.ShutdownReceivedOnConnection)
            throw new InvalidOperationException(
                $"Channel {channel.ChannelId} is not ready for a closing_complete on this connection");
        if (entry.SimpleProposal is not null)
            throw new InvalidOperationException(
                $"[B2-SC-C09] Channel {channel.ChannelId} still waits for the closing_sig of our last closing_complete");

        var message = await CreateProposalAsync(channel, entry, feeratePerKw)
                   ?? throw new InvalidOperationException(
                          $"Channel {channel.ChannelId}: our balance can't pay a closing fee at {feeratePerKw} sat/kw");
        entry.SimpleProposalSentOnConnection = true;
        return message;
    }

    private async Task<ClosingCompleteMessage?> CreateProposalAsync(ChannelModel channel,
                                                                     ClosingNegotiationRegistry.Entry entry,
                                                                     uint? feeratePerKw)
    {
        var (funding, localScript, remoteScript) = GetCloseInputs(channel);
        var (localMsat, remoteMsat) = GetBalances(channel);
        var feerate = feeratePerKw ?? await ResolveFeerateAsync(entry);

        // B2-SC-C01/C02: at most our balance, at least one output above dust
        var fee = SimpleCloseRules.ChooseFee(localMsat, remoteMsat, localScript, remoteScript, feerate);
        if (fee is null)
        {
            _logger.LogInformation(
                "No closing_complete of ours for channel {ChannelId}: our balance of {Balance} msat can't pay a relayable fee",
                channel.ChannelId, localMsat);
            return null;
        }

        var terms = new SimpleClosingTerms(funding, localMsat, remoteMsat, localScript, remoteScript, fee.Value, true);
        var selection = SimpleCloseRules.SelectCloserKinds(terms);
        if (!selection.CanPropose)
        {
            _logger.LogInformation("No closing_complete of ours for channel {ChannelId} [{Requirement}]: {Reason}",
                                   channel.ChannelId, selection.RequirementId, selection.Reason);
            return null;
        }

        // B2-SC-C03..C05, C08: our script, the peer's last script, our lock time; BOLT 3 transaction per variant
        var lockTime = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var variants = new Dictionary<ClosingSigKind, SignedClosingVariant>();
        foreach (var kind in selection.Kinds)
        {
            var unsigned = _closingTransactionBuilder.BuildSimple(terms.Build(kind), lockTime);
            variants[kind] = new SignedClosingVariant(
                unsigned, _lightningSigner.SignChannelTransaction(channel.ChannelId, unsigned));
        }

        var payload = new ClosingCompletePayload(channel.ChannelId, localScript, remoteScript,
                                                 LightningMoney.Satoshis(terms.FeeSat), lockTime);
        var signatures = new ClosingSignatures(variants.GetValueOrDefault(ClosingSigKind.CloserOutputOnly)?.OurSignature,
                                               variants.GetValueOrDefault(ClosingSigKind.CloseeOutputOnly)?.OurSignature,
                                               variants.GetValueOrDefault(ClosingSigKind.CloserAndCloseeOutputs)
                                                      ?.OurSignature);
        entry.SimpleProposal = new SimpleCloseProposal(payload, terms, variants);
        _logger.LogInformation(
            "Sending closing_complete for channel {ChannelId}: fee {Fee} sat at {Feerate} sat/kw, lock time {LockTime}, {Kinds}",
            channel.ChannelId, terms.FeeSat, feerate, lockTime, string.Join(",", selection.Kinds));
        return new ClosingCompleteMessage(payload, signatures);
    }

    #endregion

    #region Peer's closing_complete

    /// <summary>
    /// The peer's <c>closing_complete</c> (B2-SC-E01..E10): checks its fee and scripts, rebuilds its transaction,
    /// verifies the signature BOLT 2 selects, signs, persists the fully signed transaction with
    /// <see cref="ChannelState.Closing"/> and its watch, broadcasts it and returns our <c>closing_sig</c> (same TLV
    /// field). The <c>closer_scriptpubkey</c> becomes the peer's script for our next proposals.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveClosingCompleteAsync(
        ChannelModel channel, ClosingCompleteMessage message, FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        var channelId = channel.ChannelId;
        var payload = message.Payload;
        ThrowIfNotNegotiated(channelId, negotiatedFeatures, "closing_complete");
        ThrowIfNotReady(channel, "closing_complete");

        var (funding, localScript, _) = GetCloseInputs(channel);
        var (localMsat, remoteMsat) = GetBalances(channel);
        var feeSat = (ulong)payload.FeeSatoshis.Satoshi;

        // B2-SC-E01
        if (feeSat > remoteMsat / 1000)
            throw Warning(channelId, "B2-SC-E01",
                          $"closing_complete fee of {feeSat} sat is above the closer's balance of {remoteMsat / 1000} sat",
                          "fee_satoshis above the closer's balance");

        // B2-SC-E02: the closee script must be the last one we sent (we never change ours)
        if (payload.CloseeScriptPubKey != localScript)
            throw Warning(channelId, "B2-SC-E02",
                          $"closing_complete closee_scriptpubkey {payload.CloseeScriptPubKey} is not our script {localScript}",
                          "closee_scriptpubkey is not our last script");

        // B2-SC-E03: the closer's script must be a valid shutdown script (and the upfront one when bound)
        var closerScript = payload.CloserScriptPubKey;
        var anySegwit = negotiatedFeatures.BeyondSegwitShutdown > FeatureSupport.No;
        if (!ShutdownScriptValidator.IsValid((byte[])closerScript, anySegwit, simpleClose: true))
            throw Warning(channelId, "B2-SC-E03", $"closing_complete closer_scriptpubkey {closerScript} is not allowed",
                          "closer_scriptpubkey is not a valid shutdown script");
        if (negotiatedFeatures.UpfrontShutdownScript > FeatureSupport.No
         && channel.RemoteUpfrontShutdownScript is { Length: > 0 } upfront && upfront != closerScript)
            throw Warning(channelId, "B2-SC-E03",
                          $"closing_complete closer_scriptpubkey {closerScript} differs from the upfront script {upfront}",
                          "closer_scriptpubkey differs from upfront_shutdown_script");

        // B2-SC-E04/E05: the closer's transaction (an OP_RETURN output carries 0)
        var terms = new SimpleClosingTerms(funding, remoteMsat, localMsat, closerScript, localScript, feeSat, false);

        // B2-SC-E06..E08
        var kind = SimpleCloseRules.SelectCloseeKind(terms, message.Signatures);
        var peerSignature = message.Signatures.Get(kind)
                         ?? throw Warning(channelId, "B2-SC-E07",
                                          $"closing_complete has no {kind} signature, which our output requires",
                                          $"closing_complete lacks the {ToTlvName(kind)} signature");

        ClosingTransactionModel model;
        try
        {
            model = terms.Build(kind);
        }
        catch (ArgumentException e)
        {
            throw Warning(channelId, "B2-SC-E05", $"closing_complete {kind} transaction can't be built: {e.Message}",
                          "closing transaction can't be built");
        }

        var unsigned = _closingTransactionBuilder.BuildSimple(model, payload.LockTime);
        ValidatePeerSignature(channelId, peerSignature, unsigned, "B2-SC-E08", "closing_complete");

        // B2-SC-E09: sign, persist, broadcast, reply
        var ourSignature = _lightningSigner.SignChannelTransaction(channelId, unsigned);
        var closingTransaction = _closingTransactionBuilder.AddWitness(unsigned, funding, ourSignature, peerSignature);

        // B2-SC-E10: the closer's script is the one our later closing_complete pays
        if (channel.RemoteShutdownScript != closerScript)
        {
            _logger.LogInformation("Peer {Peer} now closes channel {ChannelId} to {Script}", channel.RemoteNodeId,
                                   channelId, closerScript);
            channel.ReplaceRemoteShutdownScript(closerScript);
        }

        await RecordClosingTransactionAsync(channel, closingTransaction);
        _logger.LogInformation(
            "Signed the peer's closing transaction {TxId} for channel {ChannelId} ({Kind}, fee {Fee} sat, lock time {LockTime})",
            closingTransaction.TxId, channelId, kind, feeSat, payload.LockTime);

        var reply = new ClosingSigMessage(
            new ClosingSigPayload(channelId, closerScript, localScript, payload.FeeSatoshis, payload.LockTime),
            ClosingSignatures.Single(kind, ourSignature));
        await BroadcastAsync(channel, closingTransaction);
        return [reply];
    }

    #endregion

    #region Peer's closing_sig

    /// <summary>
    /// The peer's <c>closing_sig</c> for our outstanding <c>closing_complete</c> (B2-SC-G01..G06): same fields, exactly
    /// one signature in one of the fields we sent, valid for that variant; then the fully signed transaction is
    /// persisted with <see cref="ChannelState.Closing"/> and broadcast.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveClosingSigAsync(ChannelModel channel,
                                                                            ClosingSigMessage message,
                                                                            FeatureOptions negotiatedFeatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(negotiatedFeatures);
        var channelId = channel.ChannelId;
        ThrowIfNotNegotiated(channelId, negotiatedFeatures, "closing_sig");

        var entry = _registry.Get(channelId);
        if (entry.SimpleProposal is not { } proposal)
            throw new ChannelWarningException($"closing_sig on channel {channelId} without a closing_complete of ours",
                                              channelId, "closing_sig without an outstanding closing_complete");

        // B2-SC-G01: the fields of our closing_complete
        var payload = message.Payload;
        var sent = proposal.Payload;
        if (payload.CloserScriptPubKey != sent.CloserScriptPubKey
         || payload.CloseeScriptPubKey != sent.CloseeScriptPubKey
         || payload.FeeSatoshis.Satoshi != sent.FeeSatoshis.Satoshi || payload.LockTime != sent.LockTime)
            throw Warning(channelId, "B2-SC-G01",
                          $"closing_sig ({payload.FeeSatoshis.Satoshi} sat, lock time {payload.LockTime}) does not match our closing_complete ({sent.FeeSatoshis.Satoshi} sat, lock time {sent.LockTime})",
                          "closing_sig does not match our closing_complete");

        // B2-SC-G02/G03: exactly one signature, in a field we sent
        var kinds = message.Signatures.Kinds;
        if (kinds.Count != 1)
            throw Warning(channelId, "B2-SC-G02", $"closing_sig carries {kinds.Count} signatures",
                          "closing_sig must carry exactly one signature");
        var kind = kinds[0];
        if (!proposal.Variants.TryGetValue(kind, out var variant))
            throw Warning(channelId, "B2-SC-G03", $"closing_sig signs {kind}, which our closing_complete did not",
                          $"closing_sig signs {ToTlvName(kind)}, which we did not send");

        // B2-SC-G04/G05
        var peerSignature = message.Signatures.Get(kind)!;
        ValidatePeerSignature(channelId, peerSignature, variant.Unsigned, "B2-SC-G04", "closing_sig");

        // B2-SC-G06: broadcast it
        var (funding, _, _) = GetCloseInputs(channel);
        var closingTransaction = _closingTransactionBuilder.AddWitness(variant.Unsigned, funding,
                                                                       variant.OurSignature, peerSignature);
        entry.SimpleProposal = null;
        await RecordClosingTransactionAsync(channel, closingTransaction);
        _logger.LogInformation(
            "The peer signed our closing transaction {TxId} for channel {ChannelId} ({Kind}, fee {Fee} sat)",
            closingTransaction.TxId, channelId, kind, sent.FeeSatoshis.Satoshi);
        await BroadcastAsync(channel, closingTransaction);
        return [];
    }

    #endregion

    #region Helpers

    private static void ThrowIfNotNegotiated(ChannelId channelId, FeatureOptions negotiatedFeatures, string name)
    {
        if (!IsNegotiated(negotiatedFeatures))
            throw new ChannelWarningException($"{name} on channel {channelId} without option_simple_close", channelId,
                                              $"{name} without option_simple_close");
    }

    /// <summary>
    /// A <c>closing_complete</c> needs both <c>shutdown</c>s and nothing left in either commitment (moves a cleared
    /// ShuttingDown channel on to Negotiating first).
    /// </summary>
    private void ThrowIfNotReady(ChannelModel channel, string name)
    {
        if (channel.State is ChannelState.Negotiating or ChannelState.Closing
         && channel is { LocalShutdownScript: not null, RemoteShutdownScript: not null })
            return;

        throw new ChannelWarningException(
            $"{name} on channel {channel.ChannelId} in state {Enum.GetName(channel.State)}", channel.ChannelId,
            $"{name} before both shutdowns and with updates pending")
        {
            CloseConnection = true
        };
    }

    private void ValidatePeerSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsigned,
                                       string requirementId, string name)
    {
        try
        {
            _lightningSigner.ValidateSignature(channelId, signature, unsigned);
        }
        catch (SignerException e)
        {
            throw Warning(channelId, requirementId,
                          $"{name} signature is not valid for closing transaction {unsigned.TxId}: {e.Message}",
                          $"invalid {name} signature");
        }
    }

    /// <summary>
    /// Stores <paramref name="closingTransaction"/> as the channel's closing transaction (Closing, first time) and
    /// stages its watch in the same save; then tracks the watch and completes the IPC waiters.
    /// </summary>
    private async Task RecordClosingTransactionAsync(ChannelModel channel, SignedTransaction closingTransaction)
    {
        channel.SetClosingTransaction(closingTransaction);
        if (channel.State < ChannelState.Closing)
            channel.UpdateState(ChannelState.Closing);

        WatchedTransactionModel? watch = null;
        if (_blockchainMonitor is not null
         && await _unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(closingTransaction.TxId) is null)
        {
            watch = new WatchedTransactionModel(channel.ChannelId, closingTransaction.TxId, _options.ConfirmationDepth);
            _unitOfWork.WatchedTransactionDbRepository.Add(watch);
        }

        await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await _unitOfWork.SaveChangesAsync();
        _channelMemoryRepository.UpdateChannel(channel);

        if (watch is not null)
            _blockchainMonitor!.TrackWatchedTransaction(watch);
        _registry.Get(channel.ChannelId).CompleteWaiters(closingTransaction.TxId);
    }

    /// <summary>Publishes a closing transaction; a failure (e.g. a conflicting one with a higher fee) is logged.</summary>
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

    private static (FundingOutputInfo Funding, BitcoinScript Local, BitcoinScript Remote) GetCloseInputs(
        ChannelModel channel)
    {
        var funding = channel.FundingOutput
                   ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding output");
        var local = channel.LocalShutdownScript
                 ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no shutdown script");
        var remote = channel.RemoteShutdownScript
                  ?? throw new InvalidOperationException(
                         $"Channel {channel.ChannelId} has no shutdown script from the peer");
        return (funding, local, remote);
    }

    /// <summary>The final balances (no HTLC is left once Negotiating).</summary>
    private static (ulong Local, ulong Remote) GetBalances(ChannelModel channel) =>
        (channel.Commitments?.LocalBalanceMsat ?? channel.LocalBalance.MilliSatoshi,
         channel.Commitments?.RemoteBalanceMsat ?? channel.RemoteBalance.MilliSatoshi);

    /// <summary>
    /// The feerate of our proposal: the IPC request's, else the estimate read once per connection (the first read may
    /// wait briefly under the lock), else the fee service's cached value, else BOLT 3's floor.
    /// </summary>
    private async Task<uint> ResolveFeerateAsync(ClosingNegotiationRegistry.Entry entry)
    {
        if (entry.EstimateFeeratePerKw is null)
        {
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

        var feerate = entry.EstimateFeeratePerKw ?? (ulong)_feeService.GetCachedFeeRatePerKw().Satoshi;
        return (uint)Math.Clamp(feerate, SimpleCloseRules.MinFeeratePerKw, uint.MaxValue);
    }

    private static ChannelWarningException Warning(ChannelId channelId, string requirementId, string reason,
                                                   string peerMessage) =>
        new($"[{requirementId}] {reason}", channelId, peerMessage) { CloseConnection = true };

    private static string ToTlvName(ClosingSigKind kind) => kind switch
    {
        ClosingSigKind.CloserOutputOnly => "closer_output_only",
        ClosingSigKind.CloseeOutputOnly => "closee_output_only",
        _ => "closer_and_closee_outputs"
    };

    #endregion
}