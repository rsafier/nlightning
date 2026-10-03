using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Switch;

using Channels.Interfaces;
using Domain.Accounting.Constants;
using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Constants;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using FinalHop;
using Gossip.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Keysend;
using Onchain;
using Onion;
using Trampoline;

/// <summary>
/// The HTLC switch of a node that receives and forwards payments (ONION M4-T2 wiring, M4-T4 forward, M4-T5
/// propagation, M4-T7 replay; BOLT2 N8-T2): it acts on every channel domain event after the transition that raised it
/// is persisted, outside every channel lock, and changes channels only through <see cref="IChannelOperations"/>.
/// </summary>
/// <remarks>
/// <para><see cref="IncomingHtlcLockedIn"/> (B2-FWD-01: nothing is forwarded or resolved before it):</para>
/// <list type="bullet">
///   <item>An HTLC that is no longer waiting for a resolution (removal already sent, or channel gone) is skipped.</item>
///   <item>An HTLC that already has a forward circuit is resumed from it instead of being peeled again (M4-T7): a
///   <c>Pending</c> circuit gets the outgoing HTLC that carries its origin, or is failed with
///   <c>temporary_channel_failure</c> when there is none (the offer never persisted); a <c>Fulfilled</c> one fulfills
///   with the preimage the outgoing HTLC learnt; any other waits for the outgoing HTLC's own (replayed) events.</item>
///   <item>Otherwise the onion is processed by <see cref="IncomingOnionProcessor"/> (its HMAC is recorded in the
///   persistent replay set for this HTLC until its <c>cltv_expiry</c>, NL-078; the check is skipped when the HTLC's
///   shared secret is already stored: we processed it before a restart or a reestablish), its shared
///   secret is stored (<see cref="IChannelOperations.RecordOnionSecretAsync"/>), then it is failed
///   (<c>update_fail_malformed_htlc</c>, or <c>update_fail_htlc</c> with an error onion), paid (final hop) or
///   forwarded.</item>
///   <item>Final hop (NL-253): under a per-payment-hash lock, the invoice is re-read and checked by
///   <see cref="FinalHopProcessor"/>, then <c>update_fulfill_htlc</c> is persisted with the invoice moved to
///   <c>Settled</c> (<c>Accept</c> then <c>Settle</c>, re-read and checked still <c>Open</c>) in the <b>same</b> save
///   (<see cref="IChannelOperations.FulfillHtlcAsync(ChannelId, ulong, Secret, Func{IUnitOfWork, Task}, CancellationToken)"/>):
///   there is no crash window where one is stored without the other. The other parts of a set are committed before
///   (the preimage persisted on their records, NL-322/NL-323), and a refused fulfill commits its part the same way
///   with the settle, so a replayed committed part is fulfilled while any other HTLC for a <c>Settled</c> invoice fails
///   with <c>incorrect_or_unknown_payment_details</c> (see <see cref="FinalHopProcessor"/>).</item>
///   <item>A channel that is failed or on chain (NL-316): its HTLC is only accepted as our final hop, and committed with
///   the preimage on its record instead of fulfilled; the BOLT 5 resolvers claim it on chain with it. Nothing is
///   forwarded from such a channel and nothing on it is failed off chain.</item>
///   <item>Forward: the onion's <c>short_channel_id</c> is resolved to an open channel (its local aliases or the
///   peer's alias; the real scid only when <c>option_scid_alias</c> is off), checked by <see cref="IForwardingPolicy"/>
///   (a failure is returned with our signed <c>channel_update</c> for the UPDATE codes when its scid is the onion's,
///   else with <c>len = 0</c>), recorded as a <c>Pending</c> <see cref="ForwardCircuitModel"/>, offered with
///   <c>HtlcOrigin.Forwarded</c> (persisted with the add, NL-250), then marked <c>Offered</c>. When the offer throws
///   (refused, or any other failure) and no channel HTLC carries the origin, the circuit is failed and the upstream
///   HTLC gets <c>temporary_channel_failure</c>; when the add did persist, the circuit is marked <c>Offered</c>.</item>
///   <item>Trampoline (NL-875, BOLTs PR 836): the final trampoline node receives through the same final-hop path with
///   the merged payload (<see cref="IncomingOnionTrampolineFinal.MergedPayload"/>: the set counts against the inner
///   total, decision D-TR4), and every failure of such an HTLC (refusal, <c>mpp_timeout</c>, a replay's) is created
///   with the trampoline secret then the outer one (<see cref="TrampolineErrorPackets"/>); the trampoline secret is
///   never stored, a replayed lock-in peels the stored onion again. A relay part goes to the registered
///   <see cref="ITrampolineRelayIngress"/>, else it fails with <c>temporary_trampoline_failure</c>.</item>
/// </list>
/// <para>Outgoing events, routed by the outgoing HTLC's stored <see cref="HtlcOrigin"/>:</para>
/// <list type="bullet">
///   <item><see cref="OutgoingHtlcFulfilled"/>: the upstream HTLC is fulfilled at once (B2-FWD-05), then the circuit
///   is marked <c>Fulfilled</c>.</item>
///   <item><see cref="OutgoingHtlcFailed"/> (raised only once the removal is irrevocable, B2-FWD-02): the downstream
///   error onion is wrapped with the incoming shared secret, or an <c>update_fail_malformed_htlc</c> is converted into
///   our own error onion (BOLT 2), and the upstream HTLC is failed; then the circuit is marked <c>Failed</c>. An HTLC
///   settled on chain without a preimage (<see cref="HtlcRemovalKind.OnchainTimeout"/>, raised by the BOLT 5 resolvers
///   once that settlement is reasonably deep) has no downstream error: we fail upstream with our own
///   <c>permanent_channel_failure</c>. The resolvers raise their events again every block until the output is
///   irrevocable, so a refused upstream removal is retried; one that went out is never sent twice. After that, the
///   replayed upstream lock-in (startup, link-up) of a <c>Failed</c> circuit whose outgoing record derives no
///   resolution fails the upstream HTLC the same way.</item>
///   <item><see cref="OutgoingHtlcSettled"/>: the archived HTLC row is pruned (NL-243) only once nothing needs its
///   replay any more: for a forward, the upstream HTLC has its removal and the circuit is resolved (an upstream
///   channel that is not loaded yet keeps the row); for our own payment, every
///   <see cref="ILocalPaymentHtlcHandler"/> handled its resolution.</item>
///   <item><c>HtlcOrigin.Local</c> resolutions go to the registered <see cref="ILocalPaymentHtlcHandler"/>s; so do the
///   resolutions of an HTLC with no stored origin (NL-265: offered before origins were persisted — the handlers match
///   the event by channel and HTLC id and ignore unknown payments).</item>
///   <item><c>HtlcOrigin.Trampoline</c> resolutions (NL-875) go to the registered <see cref="ITrampolineHtlcHandler"/>
///   only, never to the local-payment handlers; its settled record is pruned once the handler agrees and the relay is
///   completed with every incoming part removed. Without a handler nothing happens and the record is kept. A replayed
///   lock-in of an incoming HTLC that is already a relay part goes to the handler too, never peeled again.</item>
///   <item><see cref="IncomingHtlcSettled"/>: our removal of an incoming HTLC is final and nothing reads its archived
///   row any more (the circuit or invoice was resolved before the removal was sent), so it is pruned (NL-243).</item>
/// </list>
/// <para>Idempotent: events are re-derived on startup and after a reestablish. The work on one incoming HTLC (its
/// lock-in and the resolutions of the outgoing HTLC that forwards it) is serialized by a per-incoming-HTLC lock, so a
/// fulfill is never sent twice and never lost. Locks are always taken in the order incoming HTLC, payment hash,
/// channel (the channel lock only inside <see cref="IChannelOperations"/> or the prune), and never two channel locks.
/// A refused channel operation (<see cref="CommitmentRefusedException"/>, e.g. the peer is away) is logged: nothing was
/// persisted, and the HTLC it was for is still pending in the persisted state. Its event is derived again by the next
/// replay: at startup (useless while no link is up) and, with
/// <see cref="HtlcSwitchServiceCollectionExtensions.AddHtlcSwitchServices"/>, whenever the channel's link is marked up
/// (<see cref="LinkUpEventReplayer"/>). Before BOLT2 N7 a link is marked only when a channel opens, so an HTLC refused
/// because its peer disconnected waits for N7's <c>MarkLinkUp</c> after the reestablish (NL-252).</para>
/// </remarks>
public sealed class HtlcSwitch : IHtlcSwitch, IDisposable, IAsyncDisposable
{
    private readonly IAttributionDataService? _attributionDataService;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly IFailureOnionService _failureOnionService;
    private readonly FinalHopProcessor _finalHopProcessor;
    private readonly IForwardingPolicy _forwardingPolicy;
    private readonly IReadOnlyList<ILocalPaymentHtlcHandler> _localPaymentHandlers;
    private readonly ITrampolineHtlcHandler? _trampolineHandler;
    private readonly ILogger<HtlcSwitch> _logger;
    private readonly INodeDrainState? _nodeDrainState;
    private readonly IRefusedHtlcCounter? _refusedHtlcCounter;
    private readonly uint _reasonableDepth;
    private readonly KeysendReceiver _keysendReceiver;
    private readonly IncomingOnionProcessor _onionProcessor;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly IRetiredScidMap? _retiredScidMap;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    private readonly KeyedAsyncLock<(ChannelId, ulong)> _incomingLocks = new();
    private readonly KeyedAsyncLock<Hash> _paymentHashLocks = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _unhandledLocalResolutions = new();

    // NL-875: the outgoing trampoline HTLCs whose resolution the relay handler did not take (it threw, or none is
    // registered): their settled records are kept for the next replay
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _unhandledTrampolineResolutions = new();
    private int _missingTrampolineHandlerLogged;

    /// <summary>
    /// The upstream HTLCs whose preimage was revealed on chain after the upstream was already failed (a close a reorg
    /// replaced, NL-330): the conflict is alerted once per HTLC.
    /// </summary>
    private readonly ConcurrentDictionary<(ChannelId IncomingChannelId, ulong IncomingHtlcId), byte>
        _fulfillConflictsAlerted = new();

    // basic_mpp (ABCD W6-B): the HTLC sets being held, by payment hash (read and changed only under its hash lock),
    // the parts of a timed-out set whose mpp_timeout failure was refused (failed again on their replay), and the
    // timeout rounds running in the background
    private readonly bool _acceptMultiPart;
    private readonly bool _advertisesAttribution;
    private readonly TimeSpan _blindedErrorMaxDelay;
    private readonly TimeSpan _mppTimeout;
    private readonly ConcurrentDictionary<Hash, HtlcSet> _htlcSets = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _timedOutParts = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ISecureKeyManager? _secureKeyManager;
    private readonly ITrampolineFailureOnionService? _trampolineFailureOnionService;
    private readonly IPaymentEventPublisher? _paymentEventPublisher;
    private volatile bool _disposed;

    public HtlcSwitch(IChannelLockProvider channelLockProvider, IChannelMemoryRepository channelMemoryRepository,
                      IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                      FinalHopProcessor finalHopProcessor, IForwardingPolicy forwardingPolicy,
                      ILogger<HtlcSwitch> logger, IncomingOnionProcessor onionProcessor,
                      IPeerLivenessProbe peerLivenessProbe, IServiceScopeFactory serviceScopeFactory,
                      TimeProvider? timeProvider = null, IBlockchainMonitor? blockchainMonitor = null,
                      IChannelUpdateService? channelUpdateService = null,
                      IEnumerable<ILocalPaymentHtlcHandler>? localPaymentHandlers = null,
                      IOptions<NodeOptions>? nodeOptions = null, IOptions<HtlcSwitchOptions>? switchOptions = null,
                      IAttributionDataService? attributionDataService = null,
                      IOptions<OnchainOptions>? onchainOptions = null, IRetiredScidMap? retiredScidMap = null,
                      INodeDrainState? nodeDrainState = null,
                      IRefusedHtlcCounter? refusedHtlcCounter = null, ISecureKeyManager? secureKeyManager = null,
                      ITrampolineHtlcHandler? trampolineHandler = null,
                      ITrampolineFailureOnionService? trampolineFailureOnionService = null,
                      IPaymentEventPublisher? paymentEventPublisher = null)
    {
        _trampolineHandler = trampolineHandler;
        _paymentEventPublisher = paymentEventPublisher;
        _secureKeyManager = secureKeyManager;
        _trampolineFailureOnionService = trampolineFailureOnionService;
        _refusedHtlcCounter = refusedHtlcCounter;
        _nodeDrainState = nodeDrainState;
        _attributionDataService = attributionDataService;
        _blockchainMonitor = blockchainMonitor;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _channelUpdateService = channelUpdateService;
        _failureOnionService = failureOnionService;
        _finalHopProcessor = finalHopProcessor;
        _forwardingPolicy = forwardingPolicy;
        _localPaymentHandlers = localPaymentHandlers?.ToList() ?? [];
        _logger = logger;
        _onionProcessor = onionProcessor;
        _peerLivenessProbe = peerLivenessProbe;
        _retiredScidMap = retiredScidMap;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acceptMultiPart = (nodeOptions?.Value.Features.BasicMpp ?? FeatureSupport.Optional) != FeatureSupport.No;
        _advertisesAttribution = (nodeOptions?.Value.Features.OptionAttributionData ?? FeatureSupport.Optional)
                              != FeatureSupport.No;
        var mppTimeout = switchOptions?.Value.MppTimeout ?? HtlcSwitchOptions.DefaultMppTimeout;
        _mppTimeout = mppTimeout > TimeSpan.Zero ? mppTimeout : HtlcSwitchOptions.DefaultMppTimeout;
        _reasonableDepth = onchainOptions?.Value.ReasonableDepth ?? OutputResolutionFacts.DefaultReasonableDepth;
        _blindedErrorMaxDelay = switchOptions?.Value.BlindedErrorMaxDelay ?? HtlcSwitchOptions.DefaultBlindedErrorMaxDelay;
        _keysendReceiver = new KeysendReceiver(nodeOptions?.Value.Keysend, _timeProvider);
    }

    /// <summary>Waits until no <c>mpp_timeout</c> round runs in the background (tests).</summary>
    internal async Task WhenIdleAsync()
    {
        while (!_backgroundTasks.IsEmpty)
            await Task.WhenAll(_backgroundTasks.Keys);
    }

    /// <summary>The payment hashes whose HTLC set is held (tests).</summary>
    internal IReadOnlyCollection<Hash> HeldPaymentHashes => _htlcSets.Keys.ToList();

    /// <summary>Whether <see cref="Dispose"/> ran (tests).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Stops every <c>mpp_timeout</c> timer and cancels the timeout rounds still running (the host's container disposes
    /// the switch on shutdown, also when <c>DustExposureHtlcSwitch</c> decorates it). The held sets are forgotten: their
    /// parts are persisted and the next startup's replays build them again.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        foreach (var set in _htlcSets.Values)
            set.Timer?.Dispose();
        _htlcSets.Clear();
    }

    /// <summary><see cref="Dispose"/>, then waits (at most 5 s) for the canceled timeout rounds to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("HTLC set timeout rounds still running after the switch was disposed");
        }
    }

    /// <inheritdoc />
    public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelEvent);
        try
        {
            switch (channelEvent)
            {
                case IncomingHtlcLockedIn lockedIn:
                    await HandleLockedInAsync(lockedIn, cancellationToken);
                    break;
                case OutgoingHtlcFulfilled fulfilled:
                    await HandleOutgoingFulfilledAsync(fulfilled, cancellationToken);
                    break;
                case OutgoingHtlcFailed failed:
                    await HandleOutgoingFailedAsync(failed, cancellationToken);
                    break;
                case OutgoingHtlcSettled settled:
                    await HandleOutgoingSettledAsync(settled, cancellationToken);
                    break;
                case IncomingHtlcSettled incomingSettled:
                    await PruneAsync(incomingSettled.ChannelId,
                                     new HtlcKey(HtlcDirection.Incoming, incomingSettled.HtlcId), cancellationToken);
                    break;
            }
        }
        catch (CommitmentRefusedException e)
        {
            // Nothing was persisted or sent by the refused operation: the HTLC is still pending, and its event is
            // derived again when the channel's link comes up (LinkUpEventReplayer) or at the next startup
            _logger.LogWarning("Could not act on {Event} for HTLC {HtlcId} of channel {ChannelId}: {Reason}",
                               channelEvent.GetType().Name, channelEvent.HtlcId, channelEvent.ChannelId, e.Message);
        }
        catch (KeyNotFoundException e)
        {
            _logger.LogWarning("Could not act on {Event} for HTLC {HtlcId} of channel {ChannelId}: {Reason}",
                               channelEvent.GetType().Name, channelEvent.HtlcId, channelEvent.ChannelId, e.Message);
        }
    }

    #region Incoming

    private async Task HandleLockedInAsync(IncomingHtlcLockedIn lockedIn, CancellationToken cancellationToken)
    {
        var channelId = lockedIn.ChannelId;
        var htlcId = lockedIn.Htlc.Id;
        using var incomingLock = await _incomingLocks.AcquireAsync((channelId, htlcId), cancellationToken);

        // Idempotency: the removal was already sent (or the channel is gone)
        if (GetAwaitingIncomingHtlc(channelId, htlcId) is not { } htlc)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Incoming HTLC {HtlcId} of channel {ChannelId} is not waiting for a resolution",
                                 htlcId, channelId);
            return;
        }

        ForwardCircuitModel? circuit;
        Secret? storedSecret;
        TrampolineRelayPartModel? relayPart = null;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, htlcId);
            if (circuit is null)
                relayPart = await TrampolineRelayReads.GetPartAsync(unitOfWork, channelId, htlcId);
            storedSecret = circuit is null
                               ? await unitOfWork.ChannelStateDbRepository.GetOnionSharedSecretAsync(
                                     channelId, new HtlcKey(HtlcDirection.Incoming, htlcId))
                               : circuit.IncomingSharedSecret;
        }

        if (circuit is not null)
        {
            await ResumeCircuitAsync(circuit, cancellationToken);
            return;
        }

        // NL-875: an HTLC that is already a part of a trampoline relay is resumed by the relay, never peeled again nor
        // failed here: an outgoing HTLC of its relay may still be fulfilled downstream
        if (relayPart is not null)
        {
            await HandleRelayPartLockedInAsync(lockedIn, relayPart, cancellationToken);
            return;
        }

        // NL-598: refusals are counted once per HTLC. A stored secret means we processed this onion before (a
        // restart's replay carries one), so only a first handling counts
        var firstHandling = storedSecret is null;

        // A stored secret means we processed this onion before (restart, reestablish). Otherwise the HMAC is recorded
        // for this HTLC until its cltv_expiry (NL-078): a restart between that and the secret's save is not a replay
        OnionReplayOwner? replayOwner =
            storedSecret is null ? new OnionReplayOwner(channelId, htlcId, htlc.CltvExpiry) : null;
        var result = await _onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash, replayOwner,
                                                        htlc.PathKey, LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                        htlc.CltvExpiry);

        // A channel that can no longer carry an update (failed, or its commitment is on chain): its HTLC can only be
        // claimed on chain, and only as our final hop (NL-316, B5-LCL-RO-02). Nothing is forwarded from it, and a
        // failure cannot be sent: such an HTLC is left to time out on chain
        if (IsOnchain(channelId) && result is not (IncomingOnionFinal or IncomingOnionTrampolineFinal))
        {
            _logger.LogInformation("Incoming HTLC {HtlcId} of channel {ChannelId}, which is closing on chain, does not "
                                 + "pay us ({Result}): leaving it to time out on chain", htlcId, channelId,
                                   result.GetType().Name);
            return;
        }

        // NL-279 (BOLT 2 B2-SHUT-S08): the peer added this HTLC after our shutdown (it may not have seen it yet). A node
        // that sent shutdown SHOULD fail to route it, and a shutdown means no new HTLC is accepted either: fail it back
        // instead of forwarding it or accepting it as final hop. Not on a channel closing on chain: a failure can no
        // longer be sent there, and a final-hop HTLC of ours is claimed on chain like any other (NL-316) rather than
        // left to time out; a forward was already left to time out above
        (Secret SharedSecret, bool Introduction)? routable = result switch
        {
            IncomingOnionForward routed => (routed.SharedSecret, routed.Blinded?.IsIntroduction ?? false),
            IncomingOnionFinal received => (received.SharedSecret, false),
            IncomingOnionTrampolineResult trampoline => (trampoline.OuterSharedSecret, false),
            _ => null
        };
        if (routable is var (sharedSecret, introduction) && !IsOnchain(channelId)
         && IsAddedAfterOurShutdown(channelId, htlcId))
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(RefusedHtlcReason.AddedAfterShutdown);
            await RecordSecretAsync(channelId, htlcId, sharedSecret, storedSecret, cancellationToken);
            var trampolineKeys = result is IncomingOnionTrampolineResult trampolineOnion
                                     ? TrampolineFailureKeys.From(trampolineOnion)
                                     : null;
            await FailBackAsync(channelId, htlc, sharedSecret, FailureMessage.TemporaryNodeFailure(), cancellationToken,
                                introduction, trampolineKeys);
            _logger.LogInformation("Failed back incoming HTLC {HtlcId} of channel {ChannelId}: added after our shutdown "
                                 + "(B2-SHUT-S08)", htlcId, channelId);
            return;
        }

        switch (result)
        {
            case IncomingOnionMalformed malformed:
                if (firstHandling)
                    _refusedHtlcCounter?.Count(RefusedHtlcReason.MalformedOnion);
                await _channelOperations.FailMalformedHtlcAsync(channelId, htlcId, malformed.FailureCode,
                                                                new Hash(malformed.Sha256OfOnion.ToArray()),
                                                                cancellationToken);
                LogFailedBack(channelId, htlc, $"malformed onion ({malformed.FailureCode})");
                return;

            case IncomingOnionFailed failed:
                await RecordSecretAsync(channelId, htlcId, failed.SharedSecret, storedSecret, cancellationToken);
                await FailBackAsync(channelId, htlc, failed.SharedSecret, failed.Failure, cancellationToken,
                                    blindedIntroduction: false);
                return;

            case IncomingOnionFinal final:
                await RecordSecretAsync(channelId, htlcId, final.SharedSecret, storedSecret, cancellationToken);
                await ReceiveAsync(channelId, htlc, final, firstHandling, cancellationToken);
                return;

            case IncomingOnionForward forward:
                await RecordSecretAsync(channelId, htlcId, forward.SharedSecret, storedSecret, cancellationToken);
                await ForwardAsync(channelId, htlc, forward, firstHandling, cancellationToken);
                return;

            // Trampoline (NL-875): the outer secret is stored like any other; the trampoline secret is not, a replay
            // peels the stored onion again (its outer HMAC no longer checked) and gets it back
            case IncomingOnionTrampolineFailed trampolineFailed:
                await RecordSecretAsync(channelId, htlcId, trampolineFailed.OuterSharedSecret, storedSecret,
                                        cancellationToken);
                await FailBackAsync(channelId, htlc, trampolineFailed.OuterSharedSecret, trampolineFailed.Failure,
                                    cancellationToken, blindedIntroduction: false,
                                    new TrampolineFailureKeys(trampolineFailed.OuterSharedSecret,
                                                              trampolineFailed.TrampolineSharedSecret, null));
                return;

            case IncomingOnionTrampolineFinal trampolineFinal:
                await RecordSecretAsync(channelId, htlcId, trampolineFinal.OuterSharedSecret, storedSecret,
                                        cancellationToken);
                await ReceiveAsync(channelId, htlc, trampolineFinal.ToFinal(), firstHandling, cancellationToken,
                                   TrampolineFailureKeys.From(trampolineFinal));
                return;

            case IncomingOnionTrampolineRelay trampolineRelay:
                await RecordSecretAsync(channelId, htlcId, trampolineRelay.OuterSharedSecret, storedSecret,
                                        cancellationToken);
                await RelayTrampolineAsync(lockedIn, htlc, trampolineRelay, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// A part of a trampoline relay (NL-875): handed to the registered <see cref="ITrampolineRelayIngress"/> (the relay
    /// engine, resolved here so its registration creates no construction cycle), or, when none is registered, failed
    /// back with <c>temporary_trampoline_failure</c> created with both secrets (inside a blinded trampoline route, our
    /// own <c>invalid_onion_blinding</c> at the introduction node and <c>update_fail_malformed_htlc</c> past it).
    /// </summary>
    private async Task RelayTrampolineAsync(IncomingHtlcLockedIn lockedIn, HtlcRecord htlc,
                                            IncomingOnionTrampolineRelay relay, CancellationToken cancellationToken)
    {
        ITrampolineRelayIngress? ingress;
        using (var scope = _serviceScopeFactory.CreateScope())
            ingress = scope.ServiceProvider.GetService<ITrampolineRelayIngress>();

        if (ingress is not null)
        {
            await ingress.HandleNewPartAsync(lockedIn, relay, cancellationToken);
            return;
        }

        var failure = relay.Blinded is { IsIntroduction: true }
                          ? FailureMessage.InvalidOnionBlinding(relay.TrampolineOnionSha256)
                          : FailureMessage.TemporaryTrampolineFailure();
        await FailBackAsync(lockedIn.ChannelId, htlc, relay.OuterSharedSecret, failure, cancellationToken,
                            blindedIntroduction: false, TrampolineFailureKeys.From(relay));
    }

    private async Task RecordSecretAsync(ChannelId channelId, ulong htlcId, Secret sharedSecret, Secret? storedSecret,
                                         CancellationToken cancellationToken)
    {
        // Kept with the HTLC before it is failed or forwarded, so a failure can be wrapped after a restart
        if (storedSecret is null || !storedSecret.Value.Equals(sharedSecret))
            await _channelOperations.RecordOnionSecretAsync(channelId, htlcId, sharedSecret, cancellationToken);
    }

    /// <summary>
    /// Final hop (M4-T3, NL-253; <c>basic_mpp</c>, ABCD W6-B): check the HTLC under its payment hash lock, add it to
    /// the payment's HTLC set, and once the set's <c>amt_to_forward</c> reach <c>total_msat</c> fulfill every part,
    /// settling the invoice with one of them. A single-part payment is a set of one. A part on a channel that is
    /// closing on chain (NL-316) is accepted the same way (the same <see cref="FinalHopProcessor"/> checks), but
    /// "fulfilled" by persisting the preimage on its record, from which the BOLT 5 resolver claims it on chain; it is
    /// never failed off chain.
    /// </summary>
    private async Task ReceiveAsync(ChannelId channelId, HtlcRecord htlc, IncomingOnionFinal final,
                                    bool firstHandling, CancellationToken cancellationToken,
                                    TrampolineFailureKeys? trampoline = null)
    {
        var amount = LightningMoney.MilliSatoshis(htlc.AmountMsat);
        using var paymentHashLock = await _paymentHashLocks.AcquireAsync(htlc.PaymentHash, cancellationToken);

        // A set's fulfill or timeout acts on the parts of other HTLCs without their incoming locks, under this hash
        // lock: this HTLC may have been resolved while we waited for it
        if (GetAwaitingIncomingHtlc(channelId, htlc.Id) is not { } current)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Incoming HTLC {HtlcId} of channel {ChannelId} was resolved with its HTLC set",
                                 htlc.Id, channelId);
            return;
        }

        var onchain = IsOnchain(channelId);

        // A part of a set that timed out while its failure could not be sent: fail it now (on chain it just times out)
        if (_timedOutParts.ContainsKey((channelId, htlc.Id)))
        {
            if (!onchain)
                await FailBackAsync(channelId, htlc, final.SharedSecret, FailureMessage.MppTimeout(), cancellationToken,
                                    blindedIntroduction: false, trampoline);
            _timedOutParts.TryRemove((channelId, htlc.Id), out _);
            return;
        }

        var height = CurrentHeight;
        FinalHopResult decision;
        InvoiceModel? newKeysendRecord = null;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(htlc.PaymentHash);

            // Keysend (lane lh1-l3): no invoice for the hash and a keysend_preimage in the onion. The record is checked
            // like an invoice and saved only once accepted (below); a refused one leaves the hash unknown
            if (invoice is null && KeysendReceiver.IsKeysend(final.Payload))
            {
                invoice = newKeysendRecord = _keysendReceiver.TryCreateInvoice(htlc.PaymentHash, final.Payload,
                                                                               out var keysendRefusal);
                if (keysendRefusal is not null)
                    _logger.LogInformation("Keysend HTLC {HtlcId} of channel {ChannelId} for {PaymentHash} refused: "
                                         + "{Reason}", htlc.Id, channelId, htlc.PaymentHash, keysendRefusal);
            }

            // NL-323: a part of a set we committed to carries the invoice's preimage in its record, and the invoice is
            // Settled (the settle is the commit point: a mark on a part of an Open invoice, left by a set that became
            // incomplete or by a stop before the settle, commits to nothing and keeps every check)
            var committed = current.KnownPreimage is { } known && invoice is { Status: InvoiceStatus.Settled }
                         && known == invoice.Preimage;
            // NL-335: the on-chain decision judges the invoice's expiry at the HTLC's lock-in (its persisted AddedAt),
            // not at the decision: an HTLC locked in while its invoice was open is claimed on chain even though the
            // invoice has expired since (the peer would otherwise time it out)
            var addedAt = onchain
                ? await unitOfWork.ChannelStateDbRepository.GetHtlcAddedAtAsync(
                      channelId, new HtlcKey(HtlcDirection.Incoming, htlc.Id))
                : null;
            // After dummy hops of our own path (NL-440) the final-hop checks see what our final hop would have
            // received, as if the dummy hops were other nodes
            decision = _finalHopProcessor.Evaluate(invoice, htlc.PaymentHash, final.Blinded?.ReceivedAmount ?? amount,
                                                   final.Blinded?.ReceivedCltvExpiry ?? htlc.CltvExpiry,
                                                   final.Payload, height, _acceptMultiPart, committed,
                                                   final.Blinded?.RecipientData, addedAt);
        }

        if (decision.InvoiceAlreadySettled)
        {
            // A committed part of the set that settled the invoice (its fulfill was refused, or we stopped in
            // between): BOLT 4 requires the whole set to be fulfilled, whatever the height of this replay (also before
            // the monitor has one). On chain its record already carries the preimage the resolver claims it with
            if (onchain)
                return;

            await FulfillFinalAsync(channelId, htlc.Id, decision.Preimage!.Value, final.SharedSecret, null,
                                    cancellationToken);
            _logger.LogInformation("Fulfilled incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}, a part "
                                 + "of the settled payment {PaymentHash}", htlc.Id, htlc.AmountMsat, channelId,
                                   htlc.PaymentHash);
            return;
        }

        // The payer reads the height to tell an expiry problem from an unknown hash: never report a height of 0.
        // NL-216: while chain processing is halted the node cannot claim the HTLC on chain, so it does not reveal a
        // preimage for a new payment (a set already committed to is fulfilled above: that money is owed to us)
        if (height == 0 || _blockchainMonitor is { IsChainProcessingHalted: true })
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(RefusedHtlcReason.ChainHalt);
            if (height != 0 && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failing back incoming HTLC {HtlcId} of channel {ChannelId}: {Reason}", htlc.Id,
                                   channelId, ChainProcessingHalt.Refusal("final-hop acceptance"));
            if (!onchain)
                await FailBackAsync(channelId, htlc, final.SharedSecret, FailureMessage.TemporaryNodeFailure(),
                                    cancellationToken, blindedIntroduction: false, trampoline);
            return;
        }

        // NL-591: a node draining for its shutdown accepts no new payment (a committed set is fulfilled above)
        if (_nodeDrainState is { IsDraining: true })
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(RefusedHtlcReason.ShutdownDrain);
            _logger.LogInformation("Failing back incoming HTLC {HtlcId} of channel {ChannelId}: {Reason}", htlc.Id,
                                   channelId, NodeDrain.Refusal("final-hop acceptance"));
            if (!onchain)
                await FailBackAsync(channelId, htlc, final.SharedSecret, FailureMessage.TemporaryNodeFailure(),
                                    cancellationToken, blindedIntroduction: false, trampoline);
            return;
        }

        if (!decision.IsAccepted)
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(decision.Failure?.Code == FailureCode.IncorrectOrUnknownPaymentDetails
                                               ? RefusedHtlcReason.UnknownPaymentHash
                                               : RefusedHtlcReason.FinalHop);
            if (onchain)
                _logger.LogInformation("Incoming HTLC {HtlcId} of channel {ChannelId}, which is closing on chain, is not "
                                     + "accepted as our final hop: leaving it to time out on chain", htlc.Id,
                                       channelId);
            else
                await FailBackAsync(channelId, htlc, final.SharedSecret, decision.Failure!, cancellationToken,
                                    blindedIntroduction: false, trampoline);
            return;
        }

        if (newKeysendRecord is not null)
            await SaveKeysendRecordAsync(newKeysendRecord, htlc, channelId);

        var part = new HtlcSetPart(channelId, htlc.Id, amount, decision.PartAmount!, final.SharedSecret, trampoline);
        var set = GetHtlcSet(htlc.PaymentHash, decision.TotalMsat!);
        if (set.TotalMsat != decision.TotalMsat!)
        {
            // BOLT 4: SHOULD fail the entire HTLC set if total_msat is not the same for all HTLCs in the set
            _logger.LogInformation("HTLC {HtlcId} of channel {ChannelId} carries total_msat {TotalMsat} but the set of "
                                 + "{PaymentHash} expects {SetTotalMsat}: failing the set", htlc.Id, channelId,
                                   decision.TotalMsat!.MilliSatoshi, htlc.PaymentHash, set.TotalMsat.MilliSatoshi);
            RemoveHtlcSet(set);
            foreach (var member in set.Parts.Where(p => p.Key != part.Key).Append(part).ToList())
                await FailPartAsync(member,
                                    FailureMessage.IncorrectOrUnknownPaymentDetails(member.HtlcAmount, height),
                                    cancellationToken);
            return;
        }

        set.Add(part);
        if (!set.IsComplete)
        {
            set.Timer ??= StartMppTimer(set);
            _logger.LogInformation("Holding HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}: {Parts} part(s), "
                                 + "{PartsMsat} of {TotalMsat} msat of {PaymentHash} received", htlc.Id,
                                   htlc.AmountMsat, channelId, set.Parts.Count, set.PartsSum.MilliSatoshi,
                                   set.TotalMsat.MilliSatoshi, htlc.PaymentHash);
            return;
        }

        await FulfillSetAsync(set, decision.Preimage!.Value, height, cancellationToken);
    }

    /// <summary>
    /// Saves the invoice record of an accepted keysend HTLC (lane lh1-l3) in its own save, under the payment hash lock,
    /// before its set is fulfilled: the settle then finds it <c>Open</c> as for any invoice. A crash after this save
    /// leaves an <c>Open</c> record that the replayed HTLC (or a retry with the same preimage) is checked against.
    /// </summary>
    private async Task SaveKeysendRecordAsync(InvoiceModel record, HtlcRecord htlc, ChannelId channelId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.InvoiceDbRepository.AddAsync(record);
        await unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Keysend payment {PaymentHash} of {AmountMsat} msat received on channel {ChannelId} "
                             + "(HTLC {HtlcId}) with {CustomRecords} custom record(s)", record.PaymentHash,
                               htlc.AmountMsat, channelId, htlc.Id, record.Keysend?.CustomRecords.Count ?? 0);
    }

    /// <summary>
    /// The held set of <paramref name="paymentHash"/> without the parts that no longer wait for us, or a new one when
    /// there is none (or none of its parts is left). Under the payment hash lock.
    /// </summary>
    private HtlcSet GetHtlcSet(Hash paymentHash, LightningMoney totalMsat)
    {
        if (_htlcSets.TryGetValue(paymentHash, out var set))
        {
            set.Prune(IsPartWaiting);
            if (set.Parts.Count > 0)
                return set;

            RemoveHtlcSet(set);
        }

        set = new HtlcSet(paymentHash, totalMsat, _timeProvider.GetUtcNow());
        _htlcSets[paymentHash] = set;
        return set;
    }

    private void RemoveHtlcSet(HtlcSet set)
    {
        set.Timer?.Dispose();
        set.Timer = null;
        _htlcSets.TryRemove(new KeyValuePair<Hash, HtlcSet>(set.PaymentHash, set));
    }

    /// <summary>
    /// The set is complete: commit to it, then fulfill every part (NL-322, NL-323).
    /// </summary>
    /// <remarks>
    /// <para>First the preimage is persisted on the record of every part but one (<see cref="MarkPartAsync"/>): from
    /// then on a replay of such a part fulfills it (a committed member, see <see cref="FinalHopProcessor"/>), and on
    /// chain the BOLT 5 resolvers claim it with that preimage. Then the remaining part settles the invoice in its own
    /// save: its fulfill's, or, when its channel is closing on chain or the fulfill is refused (e.g. the peer is away),
    /// its mark's. So a <c>Settled</c> invoice means every part of its set is fulfilled or carries the preimage, and an
    /// HTLC for a <c>Settled</c> invoice without it is not a part of the set (failed, NL-323). The other parts are then
    /// fulfilled (a refused one on its replay; one on chain is claimed by the resolver).</para>
    /// <para>The invoice's <c>Settled</c> save is the commit point: a mark counts (for the replay and for the on-chain
    /// claim) only once the invoice is <c>Settled</c> with its preimage. Until then, a set that is no longer complete
    /// (a part was resolved elsewhere meanwhile) has its marks taken back and is held again; an invoice that left
    /// <c>Open</c> (canceled) fails the set: the marks are taken back and every part that can still be failed off
    /// chain is; any other failure takes the marks back and holds the set with its timer (which tries again). Under
    /// the payment hash lock.</para>
    /// </remarks>
    private async Task FulfillSetAsync(HtlcSet set, Secret preimage, uint height, CancellationToken cancellationToken)
    {
        set.Timer?.Dispose();
        set.Timer = null;
        var marked = new Dictionary<(ChannelId, ulong), HtlcSetPart>();
        HtlcSetPart? settledBy = null;
        try
        {
            while (settledBy is null)
            {
                set.Prune(IsPartWaiting);
                if (!set.IsComplete)
                {
                    // Nothing was settled, so the marks commit to nothing: take them back before the set waits again
                    // (a part that is later failed or left to time out must never be fulfilled or claimed, NL-323)
                    await UnmarkPartsAsync(marked.Values, preimage, cancellationToken);
                    HoldIncompleteSet(set);
                    return;
                }

                var candidate = set.Parts[0];
                var complete = true;
                foreach (var other in set.Parts.Skip(1).ToList())
                {
                    bool waiting;
                    try
                    {
                        waiting = await MarkPartAsync(other.ChannelId, other.HtlcId, preimage, null,
                                                      cancellationToken);
                    }
                    catch (KeyNotFoundException e)
                    {
                        // Its channel was unloaded meanwhile: the part no longer waits (pruned on the next check)
                        _logger.LogWarning("Could not commit HTLC {HtlcId} of channel {ChannelId} to the set of "
                                         + "{PaymentHash}: {Reason}", other.HtlcId, other.ChannelId, set.PaymentHash,
                                           e.Message);
                        waiting = false;
                    }

                    if (!waiting)
                    {
                        // Resolved elsewhere meanwhile: check the set again
                        complete = false;
                        break;
                    }

                    marked[other.Key] = other;
                }

                if (complete && await SettleWithAsync(candidate, set, preimage, cancellationToken))
                    settledBy = candidate;
            }
        }
        catch (InvoiceNotOpenException e)
        {
            // Canceled since it was checked: nothing was settled, fail the whole set as for an unusable invoice
            _logger.LogInformation("Invoice {PaymentHash} changed before its HTLC set was fulfilled: {Reason}",
                                   set.PaymentHash, e.Message);
            RemoveHtlcSet(set);
            await UnmarkPartsAsync(marked.Values, preimage, cancellationToken);
            foreach (var member in set.Parts.ToList())
                await FailPartAsync(member, FailureMessage.IncorrectOrUnknownPaymentDetails(member.HtlcAmount, height),
                                    cancellationToken);
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nothing was settled (the settle is the last write): take the marks back and keep the set waiting with
            // its timer, so its parts are not left without an mpp_timeout
            _logger.LogError(e, "Could not fulfill the HTLC set of {PaymentHash}: holding it", set.PaymentHash);
            await UnmarkPartsAsync(marked.Values, preimage, cancellationToken);
            if (_htlcSets.TryGetValue(set.PaymentHash, out var registered) && ReferenceEquals(registered, set))
                HoldIncompleteSet(set);
            throw;
        }

        RemoveHtlcSet(set);

        // NL-892: a part on a channel whose close trimmed it is lost with the close
        foreach (var part in set.Parts.Where(p => IsOnchain(p.ChannelId)).ToList())
            await StageLateTrimmedLossAsync(part.ChannelId, part.HtlcId);

        foreach (var part in set.Parts.Where(p => p.Key != settledBy.Key).ToList())
        {
            // Marked above: on chain the resolver claims it; one resolved elsewhere meanwhile is left alone
            if (IsOnchain(part.ChannelId) || GetAwaitingIncomingHtlc(part.ChannelId, part.HtlcId) is null)
                continue;

            try
            {
                await FulfillFinalAsync(part.ChannelId, part.HtlcId, preimage, part.SharedSecret, null,
                                        cancellationToken);
                LogFulfilled(part, set.PaymentHash);
            }
            catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
            {
                // Its record carries the preimage: its replay (link-up, startup) fulfills it
                _logger.LogWarning("Could not fulfill HTLC {HtlcId} of channel {ChannelId} for {PaymentHash} yet: "
                                 + "{Reason}", part.HtlcId, part.ChannelId, set.PaymentHash, e.Message);
            }
        }
    }

    /// <summary>
    /// Settles the invoice with <paramref name="part"/>: in its fulfill's save, or, when its channel is closing on
    /// chain or the fulfill is refused, in the save that persists the preimage on its record. False when the part no
    /// longer waits (the caller checks the set again). Throws <see cref="InvoiceNotOpenException"/> when the invoice
    /// left <c>Open</c> (nothing persisted).
    /// </summary>
    private async Task<bool> SettleWithAsync(HtlcSetPart part, HtlcSet set, Secret preimage,
                                             CancellationToken cancellationToken)
    {
        // The parts still held (just pruned) cover total_msat: the invoice receives their amounts
        var amount = set.HtlcSum;
        var parts = set.Parts.Count;
        Task Settle(IUnitOfWork unitOfWork) => SettleInvoiceAsync(unitOfWork, set.PaymentHash, amount, part.ChannelId,
                                                                  parts);

        if (!IsOnchain(part.ChannelId))
        {
            try
            {
                await FulfillFinalAsync(part.ChannelId, part.HtlcId, preimage, part.SharedSecret, Settle,
                                        cancellationToken);
                LogFulfilled(part, set.PaymentHash);
                PublishSettled(set.PaymentHash, amount);
                return true;
            }
            catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
            {
                // Nothing was persisted: commit to the set with the preimage on the record instead (its replay
                // fulfills it, or the resolver claims it on chain)
                _logger.LogWarning("Could not fulfill HTLC {HtlcId} of channel {ChannelId} for {PaymentHash} yet: "
                                 + "{Reason}", part.HtlcId, part.ChannelId, set.PaymentHash, e.Message);
            }
        }

        if (!await MarkPartAsync(part.ChannelId, part.HtlcId, preimage, Settle, cancellationToken))
            return false;

        PublishSettled(set.PaymentHash, amount);

        _logger.LogInformation("Settled invoice {PaymentHash} with incoming HTLC {HtlcId} of channel {ChannelId}, "
                             + "which is {Resolution}", set.PaymentHash, part.HtlcId, part.ChannelId,
                               IsOnchain(part.ChannelId) ? "claimed on chain" : "fulfilled on its replay");
        return true;
    }

    /// <summary>
    /// Persists <paramref name="preimage"/> (or removes it, when null) on the record of an incoming HTLC we accepted as
    /// final hop (<see cref="HtlcRecord.KnownPreimage"/>, NL-322/NL-323), with <paramref name="stage"/> (the invoice's
    /// settle) in the same save, under the channel's lock; the loaded channel's snapshot gets it after the save. The
    /// record is the proof that the HTLC is a part of a set we committed to: its replay fulfills it and the BOLT 5
    /// resolvers claim it on chain with that preimage (B5-LCL-RO-02). False when the HTLC no longer waits for a
    /// resolution (nothing written).
    /// </summary>
    private async Task<bool> MarkPartAsync(ChannelId channelId, ulong htlcId, Secret? preimage,
                                           Func<IUnitOfWork, Task>? stage, CancellationToken cancellationToken)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");

        if (channel.Commitments is not { } commitments
         || commitments.GetHtlc(HtlcDirection.Incoming, htlcId) is not { State: HtlcState.RcvdAddAckRevocation } record)
            return false;

        if (record.KnownPreimage == preimage && stage is null)
            return true;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var next = commitments;
        if (record.KnownPreimage != preimage)
        {
            var updated = record with { KnownPreimage = preimage };
            next = ChannelCommitments.Restore(commitments.ChannelId, commitments.Params, commitments.LocalBalanceMsat,
                                              commitments.RemoteBalanceMsat,
                                              commitments.Htlcs.SetItem(updated.Key, updated).Values,
                                              commitments.FeeUpdates, commitments.LocalNextHtlcId,
                                              commitments.RemoteNextHtlcId, commitments.LocalCommit,
                                              commitments.RemoteCommit, commitments.RemoteNextCommit,
                                              commitments.RemoteNextPerCommitmentPoint);
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(next, new ChannelTransition([updated], [], [], false,
                                                                                             false, false, false));
        }

        if (stage is not null)
            await stage(unitOfWork);

        await unitOfWork.SaveChangesAsync();
        channel.UpdateCommitments(next);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("{Action} the preimage of incoming HTLC {HtlcId} of channel {ChannelId}",
                             preimage is null ? "Removed" : "Stored", htlcId, channelId);
        return true;
    }

    /// <summary>
    /// Takes the marks of <paramref name="parts"/> back: nothing was settled (the set became incomplete, the invoice
    /// left <c>Open</c>, or the settle failed). Best effort: a mark left behind is neither honored by a replay nor
    /// claimed on chain while the invoice is not <c>Settled</c>.
    /// </summary>
    private async Task UnmarkPartsAsync(IEnumerable<HtlcSetPart> parts, Secret preimage,
                                        CancellationToken cancellationToken)
    {
        foreach (var part in parts.ToList())
            await UnmarkPartAsync(part, preimage, cancellationToken);
    }

    /// <summary>Takes the mark of <paramref name="part"/> back (only <paramref name="preimage"/>'s, or any one when
    /// null); best effort.</summary>
    private async Task UnmarkPartAsync(HtlcSetPart part, Secret? preimage, CancellationToken cancellationToken)
    {
        try
        {
            if (GetAwaitingIncomingHtlc(part.ChannelId, part.HtlcId) is { KnownPreimage: { } known }
             && (preimage is null || known == preimage))
                await MarkPartAsync(part.ChannelId, part.HtlcId, null, null, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not remove the preimage of HTLC {HtlcId} of channel {ChannelId}", part.HtlcId,
                             part.ChannelId);
        }
    }

    private void LogFulfilled(HtlcSetPart part, Hash paymentHash) =>
        _logger.LogInformation("Fulfilled incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId} for our "
                             + "invoice {PaymentHash}", part.HtlcId, part.HtlcAmount.MilliSatoshi, part.ChannelId,
                               paymentHash);

    /// <summary>
    /// A part still counts towards its set: its HTLC waits for a resolution and, on a channel closing on chain, can
    /// still be claimed (the tip is below its <c>cltv_expiry</c>).
    /// </summary>
    private bool IsPartWaiting(HtlcSetPart part) =>
        GetAwaitingIncomingHtlc(part.ChannelId, part.HtlcId) is { } htlc
     && (!IsOnchain(part.ChannelId) || CurrentHeight < htlc.CltvExpiry);

    /// <summary>An incomplete set waits for more parts (or its timeout); an empty one is dropped.</summary>
    private void HoldIncompleteSet(HtlcSet set)
    {
        if (set.Parts.Count == 0)
        {
            RemoveHtlcSet(set);
            return;
        }

        set.Timer ??= StartMppTimer(set);
        _logger.LogInformation("HTLC set of {PaymentHash} is no longer complete ({PartsMsat} of {TotalMsat} msat): "
                             + "holding it", set.PaymentHash, set.PartsSum.MilliSatoshi, set.TotalMsat.MilliSatoshi);
    }

    private ITimer StartMppTimer(HtlcSet set) =>
        _timeProvider.CreateTimer(_ => RunInBackground(() => ExpireHtlcSetAsync(set)), null, _mppTimeout,
                                  Timeout.InfiniteTimeSpan);

    private void RunInBackground(Func<Task> work)
    {
        if (_disposed)
            return;

        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // The host is shutting down: the parts are persisted, the next startup's replays hold them again
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "HTLC set timeout round failed");
            }
        });
        _backgroundTasks[task] = 0;
        _ = task.ContinueWith(t => _backgroundTasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// The <c>mpp_timeout</c> of a set (BOLT 4: fail all HTLCs of an incomplete set after a reasonable timeout, at
    /// least 60 s after the first, with <c>mpp_timeout</c>). A set still registered complete (its fulfill failed before
    /// the settle) is fulfilled again instead. A part whose failure is refused is failed again on its replay.
    /// </summary>
    private async Task ExpireHtlcSetAsync(HtlcSet set)
    {
        if (_disposed)
            return;

        var cancellationToken = _disposeCts.Token;
        using var paymentHashLock = await _paymentHashLocks.AcquireAsync(set.PaymentHash, cancellationToken);
        if (_disposed || !_htlcSets.TryGetValue(set.PaymentHash, out var current) || !ReferenceEquals(current, set))
            return;

        set.Prune(IsPartWaiting);
        if (set.IsComplete)
        {
            // Only a set whose fulfill failed before the settle is still registered complete: try it again
            Secret? preimage;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                preimage = (await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(set.PaymentHash))?.Preimage;
            }

            if (preimage is { } known)
            {
                await FulfillSetAsync(set, known, CurrentHeight, cancellationToken);
                return;
            }
        }

        _logger.LogInformation("HTLC set of {PaymentHash} incomplete after {Timeout} ({PartsMsat} of {TotalMsat} msat "
                             + "in {Parts} part(s)): failing it with mpp_timeout", set.PaymentHash, _mppTimeout,
                               set.PartsSum.MilliSatoshi, set.TotalMsat.MilliSatoshi, set.Parts.Count);
        RemoveHtlcSet(set);
        foreach (var part in set.Parts.ToList())
        {
            _timedOutParts[part.Key] = 0;
            if (await FailPartAsync(part, FailureMessage.MppTimeout(), cancellationToken))
                _timedOutParts.TryRemove(part.Key, out _);
        }
    }

    /// <summary>
    /// Fails one held part; a refusal is logged (the part stays locked in for a replay). A mark left on its record (a
    /// stop between the marks and the settle) is taken back first: a part we fail, or leave to time out on chain, is
    /// never fulfilled or claimed (NL-323).
    /// </summary>
    private async Task<bool> FailPartAsync(HtlcSetPart part, FailureMessage failure,
                                           CancellationToken cancellationToken)
    {
        await UnmarkPartAsync(part, null, cancellationToken);

        if (IsOnchain(part.ChannelId))
        {
            // No failure can be sent any more: the HTLC times out on chain (the resolver claims nothing unmarked)
            _logger.LogInformation("Incoming HTLC {HtlcId} of channel {ChannelId} ({Code}) is left to time out on chain",
                                   part.HtlcId, part.ChannelId, failure.Code);
            return true;
        }

        try
        {
            await SendFailureAsync(part.ChannelId, part.HtlcId, GetAwaitingIncomingHtlc(part.ChannelId, part.HtlcId),
                                   part.SharedSecret, failure, cancellationToken, blindedIntroduction: false,
                                   part.Trampoline);
            _logger.LogInformation("Failed back incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}: "
                                 + "{Reason}", part.HtlcId, part.HtlcAmount.MilliSatoshi, part.ChannelId,
                                   failure.Code);
            return true;
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            _logger.LogWarning("Could not fail HTLC {HtlcId} of channel {ChannelId} with {Code} yet: {Reason}",
                               part.HtlcId, part.ChannelId, failure.Code, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Stages <c>Open</c> → <c>Accepted</c> → <c>Settled</c> on the fulfill's unit of work (under the channel lock and
    /// the payment hash lock), after checking again that the invoice is still <c>Open</c>, with its
    /// <c>InvoiceSettled</c> accounting event (NL-602): the settle happens once per invoice (MPP included, in the save
    /// of the part that settles it), so the event does too. When our own payment is paying the invoice (a circular
    /// rebalance, NL-609: a payment row for the hash that is <c>InFlight</c> or <c>Succeeded</c>), the event is flagged
    /// <c>selfPayment</c>, so the books take it as no income.
    /// </summary>
    private async Task SettleInvoiceAsync(IUnitOfWork unitOfWork, Hash paymentHash, LightningMoney amount,
                                          ChannelId channelId, int parts)
    {
        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash);
        if (invoice is not { Status: InvoiceStatus.Open })
            throw new InvoiceNotOpenException($"the invoice is {invoice?.Status.ToString() ?? "gone"}");

        invoice.Accept(amount);
        invoice.Settle(_timeProvider.GetUtcNow());
        await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
        var selfPayment = await IsOurOwnPaymentAsync(unitOfWork, invoice);
        PaymentAccountingEvents.TryStage(unitOfWork, () =>
        {
            _channelMemoryRepository.TryGetChannel(channelId, out var channel);
            return PaymentAccountingEvents.InvoiceSettled(invoice, amount, channelId, channel, parts,
                                                          IsOnchain(channelId), CurrentHeight, selfPayment);
        }, _logger);
    }

    /// <summary>
    /// Tells the payment event subscribers (Cashu plan C0, NL-901) that the invoice of <paramref name="paymentHash"/>
    /// is settled: called after the save that settled it.
    /// </summary>
    private void PublishSettled(Hash paymentHash, LightningMoney amount) =>
        _paymentEventPublisher?.Publish(new InvoiceSettledEvent(paymentHash, amount, _timeProvider.GetUtcNow()));

    /// <summary>
    /// Whether one of our own payments is paying <paramref name="invoice"/> (NL-609): its row is <c>InFlight</c> or
    /// <c>Succeeded</c> and <see cref="SelfPaymentRule"/> holds (a BOLT 11 invoice of ours, never a keysend record, and
    /// we are the payment's payee, NL-670). False when that cannot be read (logged; the settle goes on) or when our node
    /// id is unknown (no <see cref="ISecureKeyManager"/>).
    /// </summary>
    private async Task<bool> IsOurOwnPaymentAsync(IUnitOfWork unitOfWork, InvoiceModel invoice)
    {
        var paymentHash = invoice.PaymentHash;
        if (_secureKeyManager is null || invoice is not { Bolt11: not null, Keysend: null, Bolt12: null })
            return false;

        try
        {
            return SelfPaymentRule.IsSettledBySelfPayment(
                invoice, await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(paymentHash),
                _secureKeyManager.GetNodePubKey());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not check whether invoice {PaymentHash} is paid by our own payment",
                               paymentHash);
            return false;
        }
    }

    /// <summary>
    /// Forward (M4-T4): resolve, check the policy, persist the circuit, offer, record the offer.
    /// </summary>
    private async Task ForwardAsync(ChannelId incomingChannelId, HtlcRecord htlc, IncomingOnionForward forward,
                                    bool firstHandling, CancellationToken cancellationToken)
    {
        var height = CurrentHeight;
        var introduction = forward.Blinded?.IsIntroduction ?? false;

        // NL-267: right after the start the monitor has not processed a block yet (the daemon connects the peers
        // before it starts it); until it reports one, its stored tip is what it will resume from, so the forward
        // checks read that instead of failing every HTLC received or replayed in the window with
        // temporary_node_failure. Without a stored state (a fresh node before its first block) there is no height to
        // wait for: the failure goes out and the payer retries
        if (height == 0 && _blockchainMonitor is not null)
            height = await StoredTipHeightAsync();

        if (height == 0)
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(RefusedHtlcReason.ChainHalt);
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret, FailureMessage.TemporaryNodeFailure(),
                                cancellationToken, introduction);
            return;
        }

        // Inside a blinded route the recipient names the next hop by short_channel_id or by next_node_id (M5)
        ChannelModel? outgoing;
        ShortChannelId requestedScid;
        OutgoingChannelInfo? outgoingInfo;
        if (forward.HasOutgoingShortChannelId)
        {
            requestedScid = forward.OutgoingShortChannelId;
            outgoing = ResolveOutgoingChannel(requestedScid);
            outgoingInfo = outgoing is null ? null : await DescribeAsync(outgoing, cancellationToken);
        }
        else
        {
            (outgoing, outgoingInfo) = await SelectOutgoingChannelAsync(forward.NextNodeId!.Value,
                                                                        forward.AmountToForward, cancellationToken);
            requestedScid = outgoing is null ? default : ScidOf(outgoing);
        }

        var incomingAmount = LightningMoney.MilliSatoshis(htlc.AmountMsat);
        var decision = _forwardingPolicy.Evaluate(new ForwardingRequest(incomingAmount, htlc.CltvExpiry,
                                                                        forward.AmountToForward,
                                                                        forward.OutgoingCltvValue, height,
                                                                        outgoingInfo,
                                                                        forward.Blinded?.RecipientData.PaymentRelay));
        if (!decision.IsForward)
        {
            if (firstHandling)
                _refusedHtlcCounter?.Count(decision.FailureCode == FailureCode.UnknownNextPeer
                                               ? RefusedHtlcReason.UnknownNextChannel
                                               : RefusedHtlcReason.ForwardPolicy);
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                ToFailureMessage(decision, outgoing, requestedScid), cancellationToken, introduction);
            return;
        }

        var outgoingChannel = outgoing!;
        var circuit = new ForwardCircuitModel(incomingChannelId, htlc.Id, incomingAmount, htlc.CltvExpiry,
                                              htlc.PaymentHash, forward.SharedSecret, requestedScid,
                                              forward.AmountToForward, forward.OutgoingCltvValue,
                                              _timeProvider.GetUtcNow());
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ForwardCircuitDbRepository.AddAsync(circuit);
            await unitOfWork.SaveChangesAsync();
        }

        ulong outgoingHtlcId;
        try
        {
            // Inside a blinded route the next hop gets the next path_key in update_add_htlc (BOLT 2, BOLT 4 M5)
            var nextPathKey = forward.Blinded is { } blinded ? new BlindedPathTlv(blinded.NextPathKey) : null;
            outgoingHtlcId = await _channelOperations.OfferHtlcAsync(
                outgoingChannel.ChannelId, forward.AmountToForward, htlc.PaymentHash, forward.OutgoingCltvValue,
                forward.NextPacket, nextPathKey, HtlcOrigin.Forwarded(incomingChannelId, htlc.Id), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A refusal persisted nothing; any other failure (staging, save, publish) may come after the add was saved,
            // so the channel state decides. A cancellation leaves the circuit Pending for the replay
            // (ResumeCircuitAsync decides the same way)
            if (e is CommitmentRefusedException or KeyNotFoundException)
                _logger.LogInformation("Forward of HTLC {HtlcId} of channel {ChannelId} to {ShortChannelId} refused: "
                                     + "{Reason}", htlc.Id, incomingChannelId, requestedScid, e.Message);
            else
                _logger.LogError(e, "Forward of HTLC {HtlcId} of channel {ChannelId} to {ShortChannelId} failed",
                                 htlc.Id, incomingChannelId, requestedScid);

            if (await FindForwardedHtlcAsync(incomingChannelId, htlc.Id) is { } offered)
            {
                await UpdateCircuitAsync(incomingChannelId, htlc.Id, c => c.Status == ForwardCircuitStatus.Pending,
                                         c => c.AddOutgoingHtlc(offered.ChannelId, offered.Htlc.Id));
                return;
            }

            await UpdateCircuitAsync(incomingChannelId, htlc.Id,
                                     c => c.Status == ForwardCircuitStatus.Pending,
                                     c => c.MarkFailed(_timeProvider.GetUtcNow(),
                                                       failureSource: outgoingChannel.ChannelId));
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                FailureMessage.TemporaryChannelFailure(UpdateFor(outgoingChannel, requestedScid)),
                                cancellationToken, introduction);
            return;
        }

        await UpdateCircuitAsync(incomingChannelId, htlc.Id, c => c.Status == ForwardCircuitStatus.Pending,
                                 c => c.AddOutgoingHtlc(outgoingChannel.ChannelId, outgoingHtlcId));
        _logger.LogInformation("Forwarded HTLC {HtlcId} of channel {ChannelId} as HTLC {OutgoingHtlcId} of channel "
                             + "{OutgoingChannelId} ({AmountMsat} msat, fee {FeeMsat} msat)", htlc.Id,
                               incomingChannelId, outgoingHtlcId, outgoingChannel.ChannelId,
                               forward.AmountToForward.MilliSatoshi, circuit.Fee.MilliSatoshi);
    }

    /// <summary>
    /// A locked-in HTLC that already has a circuit (M4-T7 replay): continue from what was persisted.
    /// </summary>
    private async Task ResumeCircuitAsync(ForwardCircuitModel circuit, CancellationToken cancellationToken)
    {
        var incomingChannelId = circuit.IncomingChannelId;
        var incomingHtlcId = circuit.IncomingHtlcId;
        switch (circuit.Status)
        {
            case ForwardCircuitStatus.Pending:
                {
                    if (await FindForwardedHtlcAsync(incomingChannelId, incomingHtlcId) is { } offered)
                    {
                        // The add persisted but the circuit's Offered update did not: record it; the outgoing HTLC's
                        // events resolve the forward
                        await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                                 c => c.Status == ForwardCircuitStatus.Pending,
                                                 c => c.AddOutgoingHtlc(offered.ChannelId, offered.Htlc.Id));
                        return;
                    }

                    // The offer never persisted: no downstream HTLC can resolve it, so fail it here
                    await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                             c => c.Status == ForwardCircuitStatus.Pending,
                                             c => c.MarkFailed(_timeProvider.GetUtcNow()));
                    await FailCircuitUpstreamLocallyAsync(circuit, cancellationToken);
                    return;
                }

            case ForwardCircuitStatus.Failed when circuit.OutgoingHtlcId is null:
                // The offer was refused and the upstream failure was not sent yet
                await FailCircuitUpstreamLocallyAsync(circuit, cancellationToken);
                return;

            case not ForwardCircuitStatus.Pending
                when circuit is { OutgoingChannelId: { } outgoingChannelId, OutgoingHtlcId: { } outgoingHtlcId }:
                {
                    // The upstream HTLC still waits although its forward may be resolved downstream (an upstream
                    // removal refused while the peer was away, or a crash before it): resolve it from the outgoing
                    // record, live or archived (it is not pruned before the upstream HTLC has its removal)
                    var record = await FindOutgoingRecordAsync(outgoingChannelId, outgoingHtlcId);
                    var closedOutgoing = false;
                    if (record is null && circuit.Status == ForwardCircuitStatus.Offered)
                        (closedOutgoing, record) = await FindClosedOutgoingRecordAsync(outgoingChannelId,
                                                                                       outgoingHtlcId);
                    var resolutions = record is null
                                          ? []
                                          : ChannelDomainEvents.DerivePending(outgoingChannelId, [record]);
                    var resolved = false;
                    foreach (var resolution in resolutions)
                    {
                        switch (resolution)
                        {
                            case OutgoingHtlcFulfilled fulfilled when !resolved:
                                await FulfillForwardLockedAsync(incomingChannelId, incomingHtlcId, fulfilled,
                                                                cancellationToken);
                                resolved = true;
                                break;
                            case OutgoingHtlcFailed failed when !resolved:
                                await FailForwardLockedAsync(incomingChannelId, incomingHtlcId, failed,
                                                             cancellationToken);
                                resolved = true;
                                break;
                            case OutgoingHtlcSettled when resolved
                                                       && await IsForwardDoneAsync(incomingChannelId, incomingHtlcId):
                                // Its settle event was consumed while the upstream still waited: prune it now
                                await PruneAsync(outgoingChannelId, record!.Key, cancellationToken);
                                break;
                        }
                    }

                    if (!resolved && circuit.Status == ForwardCircuitStatus.Failed)
                    {
                        // The circuit is marked Failed only after an irrevocable downstream failure, but no persisted
                        // removal says why: the downstream HTLC was settled on chain without a preimage (an
                        // OnchainTimeout is never persisted as a removal) and the upstream fail was refused, or the
                        // outgoing channel is no longer loaded. The resolver stops raising its event once the output
                        // is irrevocable, so fail upstream here with our own permanent_channel_failure
                        var failed = new OutgoingHtlcFailed(outgoingChannelId, outgoingHtlcId, circuit.PaymentHash,
                                                            HtlcRemoval.OnchainTimeout());
                        await FailForwardLockedAsync(incomingChannelId, incomingHtlcId, failed, cancellationToken);
                        return;
                    }

                    if (!resolved && closedOutgoing && CurrentHeight is var height and > 0
                     && height >= (ulong)circuit.OutgoingCltvExpiry + _reasonableDepth)
                    {
                        // NL-320: the outgoing channel closed on chain and its record shows no preimage, and the
                        // outgoing HTLC expired reasonably deep ago (its resolver's event was lost, or it closed before
                        // the resolver failed such HTLCs): nothing downstream can resolve it any more
                        _logger.LogWarning("Failing upstream HTLC {HtlcId} of channel {ChannelId}: its forward on the "
                                         + "closed channel {OutgoingChannelId} expired at {CltvExpiry} without a "
                                         + "preimage", incomingHtlcId, incomingChannelId, outgoingChannelId,
                                           circuit.OutgoingCltvExpiry);
                        var failed = new OutgoingHtlcFailed(outgoingChannelId, outgoingHtlcId, circuit.PaymentHash,
                                                            HtlcRemoval.OnchainTimeout());
                        await FailForwardLockedAsync(incomingChannelId, incomingHtlcId, failed, cancellationToken);
                        return;
                    }

                    if (!resolved)
                        LogWaiting(circuit);
                    return;
                }

            default:
                LogWaiting(circuit);
                return;
        }
    }

    private void LogWaiting(ForwardCircuitModel circuit)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HTLC {HtlcId} of channel {ChannelId} waits for its {Status} forward", circuit.IncomingHtlcId,
                             circuit.IncomingChannelId, circuit.Status);
    }

    /// <summary>
    /// The record of an HTLC we offered: the live one in memory, else its archived (settled, unpruned) row. Null when
    /// the channel is not loaded or the record is gone.
    /// </summary>
    private async Task<HtlcRecord?> FindOutgoingRecordAsync(ChannelId channelId, ulong htlcId)
    {
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.Commitments is not { } commitments)
            return null;

        if (commitments.GetHtlc(HtlcDirection.Outgoing, htlcId) is { } live)
            return live;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channelId, commitments.Params);
        var key = new HtlcKey(HtlcDirection.Outgoing, htlcId);
        return persisted?.SettledHtlcs.FirstOrDefault(h => h.Key == key);
    }

    /// <summary>
    /// NL-320: the record of an HTLC we offered on a channel that is not loaded because it is <c>Closed</c> (live in its
    /// stored snapshot, else archived). <c>Closed</c> is false when the channel is loaded, unknown or not closed.
    /// </summary>
    private async Task<(bool Closed, HtlcRecord? Record)> FindClosedOutgoingRecordAsync(ChannelId channelId,
                                                                                       ulong htlcId)
    {
        if (_channelMemoryRepository.TryGetChannel(channelId, out _))
            return (false, null);

        using var scope = _serviceScopeFactory.CreateScope();
        return await ClosedChannelHtlcs.FindAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), channelId,
                                                  new HtlcKey(HtlcDirection.Outgoing, htlcId));
    }

    /// <summary>The channel HTLC that carries the forward's origin, when its add was persisted.</summary>
    private async Task<(ChannelId ChannelId, HtlcKey Htlc)?> FindForwardedHtlcAsync(ChannelId incomingChannelId,
                                                                                   ulong incomingHtlcId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var outgoing = await unitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(
                           HtlcOrigin.Forwarded(incomingChannelId, incomingHtlcId));
        return outgoing.Count > 0 ? outgoing[0] : null;
    }

    private async Task FailCircuitUpstreamLocallyAsync(ForwardCircuitModel circuit,
                                                       CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(circuit.IncomingChannelId, circuit.IncomingHtlcId) is not { } htlc)
            return;

        var outgoing = ResolveOutgoingChannel(circuit.OutgoingShortChannelId);
        await FailBackAsync(circuit.IncomingChannelId, htlc, circuit.IncomingSharedSecret,
                            FailureMessage.TemporaryChannelFailure(UpdateFor(outgoing, circuit.OutgoingShortChannelId)),
                            cancellationToken);
    }

    private async Task FailBackAsync(ChannelId channelId, HtlcRecord htlc, Secret sharedSecret,
                                     FailureMessage failure, CancellationToken cancellationToken,
                                     bool? blindedIntroduction = null, TrampolineFailureKeys? trampoline = null)
    {
        await SendFailureAsync(channelId, htlc.Id, htlc, sharedSecret, failure, cancellationToken,
                               blindedIntroduction, trampoline);
        LogFailedBack(channelId, htlc, failure.Code.ToString());
    }

    #endregion

    #region Outgoing

    private async Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled,
                                                    CancellationToken cancellationToken)
    {
        var origin = await GetOriginAsync(fulfilled.ChannelId, fulfilled.HtlcId);
        switch (origin)
        {
            case
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                IncomingHtlcId: { } incomingHtlcId
            }:
                {
                    using var incomingLock =
                        await _incomingLocks.AcquireAsync((incomingChannelId, incomingHtlcId), cancellationToken);
                    await FulfillForwardLockedAsync(incomingChannelId, incomingHtlcId, fulfilled, cancellationToken);
                    return;
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                await NotifyLocalAsync(fulfilled.ChannelId, fulfilled.HtlcId,
                                       h => h.HandleFulfilledAsync(fulfilled, paymentHash, cancellationToken));
                return;

            case { Kind: HtlcOriginKind.Trampoline, PaymentHash: { } relayHash }:
                // NL-875: the relay's outgoing payment; never a local payment's handlers
                await NotifyTrampolineAsync(fulfilled.ChannelId, fulfilled.HtlcId,
                                            h => h.HandleFulfilledAsync(fulfilled, relayHash, cancellationToken));
                return;

            case null:
                // NL-265: an HTLC offered before NL-250 carries no origin: it is our own payment (W2-C "Local or no
                // circuit"). The handlers match the event by channel and HTLC id and ignore unknown payments
                await NotifyLocalAsync(fulfilled.ChannelId, fulfilled.HtlcId,
                                       h => h.HandleFulfilledAsync(fulfilled, fulfilled.PaymentHash,
                                                                   cancellationToken));
                return;

            default:
                _logger.LogWarning("HTLC {HtlcId} we offered on channel {ChannelId} was fulfilled but has no origin",
                                   fulfilled.HtlcId, fulfilled.ChannelId);
                return;
        }
    }

    private async Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken)
    {
        var origin = await GetOriginAsync(failed.ChannelId, failed.HtlcId);
        switch (origin)
        {
            case
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                IncomingHtlcId: { } incomingHtlcId
            }:
                {
                    using var incomingLock =
                        await _incomingLocks.AcquireAsync((incomingChannelId, incomingHtlcId), cancellationToken);
                    await FailForwardLockedAsync(incomingChannelId, incomingHtlcId, failed, cancellationToken);
                    return;
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                await NotifyLocalAsync(failed.ChannelId, failed.HtlcId,
                                       h => h.HandleFailedAsync(failed, paymentHash, cancellationToken));
                return;

            case { Kind: HtlcOriginKind.Trampoline, PaymentHash: { } relayHash }:
                await NotifyTrampolineAsync(failed.ChannelId, failed.HtlcId,
                                            h => h.HandleFailedAsync(failed, relayHash, cancellationToken));
                return;

            case null:
                // NL-265: an HTLC offered before NL-250 carries no origin: it is our own payment (W2-C "Local or no
                // circuit"). The handlers match the event by channel and HTLC id and ignore unknown payments
                await NotifyLocalAsync(failed.ChannelId, failed.HtlcId,
                                       h => h.HandleFailedAsync(failed, failed.PaymentHash, cancellationToken));
                return;

            default:
                _logger.LogWarning("HTLC {HtlcId} we offered on channel {ChannelId} failed ({Kind}) but has no origin",
                                   failed.HtlcId, failed.ChannelId, failed.Removal.Kind);
                return;
        }
    }

    /// <summary>
    /// A forward's downstream HTLC was fulfilled: fulfill upstream at once (B2-FWD-05), then mark the circuit
    /// <c>Fulfilled</c>. The caller holds the incoming HTLC's lock.
    /// </summary>
    private async Task FulfillForwardLockedAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                                 OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken)
    {
        try
        {
            await FulfillUpstreamAsync(incomingChannelId, incomingHtlcId, fulfilled, cancellationToken);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            // The outgoing record (and its preimage) is kept until the upstream is resolved, so the lock-in replayed
            // when the upstream link comes up (or at startup) fulfills from it (ResumeCircuitAsync)
            _logger.LogWarning("Could not fulfill upstream HTLC {HtlcId} of channel {ChannelId} yet: {Reason}",
                               incomingHtlcId, incomingChannelId, e.Message);
        }

        await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                 c => c.Status is ForwardCircuitStatus.Pending or ForwardCircuitStatus.Offered,
                                 c => c.MarkFulfilled(fulfilled.ChannelId, fulfilled.HtlcId,
                                                      _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// A forward's downstream HTLC failed irrevocably: fail upstream with the wrapped (or converted) error, then mark
    /// the circuit <c>Failed</c>. The caller holds the incoming HTLC's lock.
    /// </summary>
    private async Task FailForwardLockedAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                              OutgoingHtlcFailed failed, CancellationToken cancellationToken)
    {
        var awaiting = GetAwaitingIncomingHtlc(incomingChannelId, incomingHtlcId);
        if (awaiting is not null && await FailBlindedUpstreamAsync(incomingChannelId, awaiting, failed,
                                                                   cancellationToken))
        {
            // Inside a blinded route the downstream error is never relayed (BOLT 2, BOLT 4 M5)
        }
        else if (awaiting is { } incoming)
        {
            var sharedSecret = await GetIncomingSharedSecretAsync(incomingChannelId, incoming);
            var attributed = sharedSecret is not null && AddsAttribution(incoming);
            var reason = sharedSecret is { } secret && !attributed
                             ? ReturnPacket(secret, failed.Removal)
                             : null;
            try
            {
                if (attributed)
                {
                    // The attribution seam (NL-326): wrap the downstream attribution_data (all zero when none came)
                    // with our hold time
                    var holdTime = await _channelOperations.GetHoldTimeAsync(incomingChannelId, incomingHtlcId,
                                                                             cancellationToken);
                    await _channelOperations.FailHtlcAsync(incomingChannelId, incomingHtlcId,
                                                           AttributedReturnPacket(sharedSecret!.Value, failed.Removal,
                                                                                  holdTime),
                                                           cancellationToken);
                    LogFailedBack(incomingChannelId, incoming,
                                  $"downstream {failed.Removal.Kind} on channel {failed.ChannelId}");
                }
                else if (reason is not null)
                {
                    await _channelOperations.FailHtlcAsync(incomingChannelId, incomingHtlcId, reason,
                                                           cancellationToken);
                    LogFailedBack(incomingChannelId, incoming,
                                  $"downstream {failed.Removal.Kind} on channel {failed.ChannelId}");
                }
                else
                {
                    // Without the incoming shared secret nothing can be encrypted for the origin
                    await _channelOperations.FailMalformedHtlcAsync(incomingChannelId, incomingHtlcId,
                                                                    FailureCode.InvalidOnionHmac,
                                                                    Sha256Of(incoming.OnionRoutingPacket),
                                                                    cancellationToken);
                }
            }
            catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
            {
                // The outgoing record is kept until the upstream is resolved: the lock-in replayed when the upstream
                // link comes up (or at startup) fails from it (ResumeCircuitAsync)
                _logger.LogWarning("Could not fail upstream HTLC {HtlcId} of channel {ChannelId} yet: {Reason}",
                                   incomingHtlcId, incomingChannelId, e.Message);
            }
        }

        // The failure code is only recorded when it was sent in the clear (a fail_malformed): an opaque failure
        // onion's code is readable by the node that created it, and an on-chain timeout has none
        var failureCode = failed.Removal.Kind == HtlcRemovalKind.FailMalformed
                              ? (ushort?)failed.Removal.FailureCode
                              : null;
        await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                 c => c.Status is ForwardCircuitStatus.Pending or ForwardCircuitStatus.Offered,
                                 c => c.MarkFailed(failed.ChannelId, failed.HtlcId, _timeProvider.GetUtcNow(),
                                                   failureCode));
    }

    private async Task HandleOutgoingSettledAsync(OutgoingHtlcSettled settled, CancellationToken cancellationToken)
    {
        var origin = await GetOriginAsync(settled.ChannelId, settled.HtlcId);
        IDisposable? incomingLock = null;
        try
        {
            switch (origin)
            {
                case
                {
                    Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                    IncomingHtlcId: { } incomingHtlcId
                }:
                    {
                        incomingLock = await _incomingLocks.AcquireAsync((incomingChannelId, incomingHtlcId),
                                                                         cancellationToken);
                        if (!await IsForwardDoneAsync(incomingChannelId, incomingHtlcId))
                        {
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug("Keeping settled HTLC {HtlcId} of channel {ChannelId}: its upstream HTLC "
                                               + "is not resolved yet", settled.HtlcId, settled.ChannelId);
                            return;
                        }

                        break;
                    }

                case { Kind: HtlcOriginKind.Local }
                    when _unhandledLocalResolutions.ContainsKey((settled.ChannelId, settled.HtlcId)):
                    return;

                case { Kind: HtlcOriginKind.Trampoline, PaymentHash: { } relayHash }:
                    if (!await IsTrampolineRelayDoneAsync(settled, relayHash, cancellationToken))
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Keeping settled HTLC {HtlcId} of channel {ChannelId}: its trampoline relay "
                                           + "{PaymentHash} is not resolved yet", settled.HtlcId, settled.ChannelId,
                                             relayHash);
                        return;
                    }

                    break;

                case null when _unhandledLocalResolutions.ContainsKey((settled.ChannelId, settled.HtlcId)):
                    // NL-265: an origin-less HTLC's resolution (treated as our own payment) was not handled yet
                    return;
            }

            await PruneAsync(settled.ChannelId, new HtlcKey(HtlcDirection.Outgoing, settled.HtlcId),
                             cancellationToken);
        }
        finally
        {
            incomingLock?.Dispose();
        }
    }

    /// <summary>
    /// Removes the archived row of a settled HTLC (NL-243) under the channel's lock, in one save. The repository skips a
    /// row that is not final (or already gone), so a replayed settle event is harmless.
    /// </summary>
    private async Task PruneAsync(ChannelId channelId, HtlcKey htlc, CancellationToken cancellationToken)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelStateDbRepository.PruneSettledHtlcsAsync(channelId, [htlc]);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// A forward's outgoing record may go once the upstream HTLC has its removal (or is gone from a loaded channel)
    /// and the circuit recorded the resolution. An upstream channel that is not loaded (yet) keeps the record.
    /// </summary>
    private async Task<bool> IsForwardDoneAsync(ChannelId incomingChannelId, ulong incomingHtlcId)
    {
        if (!_channelMemoryRepository.TryGetChannel(incomingChannelId, out var incomingChannel)
         || incomingChannel.Commitments is not { } commitments
         || commitments.GetHtlc(HtlcDirection.Incoming, incomingHtlcId) is { State: HtlcState.RcvdAddAckRevocation })
            return false;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId, incomingHtlcId);
        return circuit?.Status is null or ForwardCircuitStatus.Fulfilled or ForwardCircuitStatus.Failed;
    }

    private async Task FulfillUpstreamAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                            OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(incomingChannelId, incomingHtlcId) is not { } incoming)
        {
            await AlertUpstreamAlreadyFailedAsync(incomingChannelId, incomingHtlcId, fulfilled);
            return;
        }

        if (AddsAttribution(incoming)
         && await GetIncomingSharedSecretAsync(incomingChannelId, incoming) is { } sharedSecret
         && (!fulfilled.AttributionData.IsEmpty || !fulfilled.FulfillmentPayload.IsEmpty))
        {
            // The attribution seam (NL-326): wrap the downstream attribution_data and fulfillment_payload with our
            // hold time. Nothing came to wrap when both are empty - a downstream that added no attribution, or a
            // fulfill a disconnect reverted, whose replay keeps only the preimage (NL-334): wrapping an all-zero
            // block would make the origin see an invalid HMAC at the downstream hop, so the fulfill goes upstream
            // without attribution_data at all (which the origin reads as "no attribution", not as a garbled block)
            var holdTime = await _channelOperations.GetHoldTimeAsync(incomingChannelId, incomingHtlcId,
                                                                     cancellationToken);
            var attribution = _attributionDataService!.WrapFulfillment(sharedSecret, fulfilled.AttributionData.Span,
                                                                       fulfilled.FulfillmentPayload.Span, holdTime);
            await _channelOperations.FulfillHtlcAsync(incomingChannelId, incomingHtlcId, fulfilled.PaymentPreimage,
                                                      attribution, null, cancellationToken);
        }
        else
        {
            await _channelOperations.FulfillHtlcAsync(incomingChannelId, incomingHtlcId, fulfilled.PaymentPreimage,
                                                      cancellationToken);
        }

        _logger.LogInformation("Fulfilled upstream HTLC {HtlcId} of channel {ChannelId}", incomingHtlcId,
                               incomingChannelId);
    }

    /// <summary>
    /// A preimage arrived for an incoming HTLC that can no longer be fulfilled (not locked in, no removal pending).
    /// When the upstream was already <b>failed</b> — the preimage of a close that a reorg replaced with another
    /// commitment carrying the HTLC again (NL-330), or any other late reveal — the amount was paid downstream without
    /// reimbursement: the operator is told once per HTLC. Any other reason (fulfilled already, the channel gone) stays
    /// silent, as the repeated events of the resolvers always have.
    /// </summary>
    private async Task AlertUpstreamAlreadyFailedAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                                       OutgoingHtlcFulfilled fulfilled)
    {
        try
        {
            var incoming = _channelMemoryRepository.TryGetChannel(incomingChannelId, out var channel)
                        ? channel.Commitments?.GetHtlc(HtlcDirection.Incoming, incomingHtlcId)
                        : null;
            bool failed;
            if (incoming?.Removal is { } removal)
                failed = removal.Kind != HtlcRemovalKind.Fulfill;
            else if (incoming is null)
            {
                // Not loaded (the upstream channel is closed or not in this process): the circuit decides
                using var scope = _serviceScopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                                  incomingHtlcId);
                failed = circuit?.Status == ForwardCircuitStatus.Failed;
            }
            else
                return;

            if (!failed
             || !_fulfillConflictsAlerted.TryAdd((incomingChannelId, incomingHtlcId), 0))
                return;

            _logger.LogCritical(
                "[B5-GEN-06] The preimage of our HTLC {HtlcId} on channel {ChannelId} (payment hash {PaymentHash}) was "
              + "revealed on chain, but the upstream HTLC {IncomingHtlcId} of channel {IncomingChannelId} was already "
              + "failed: it cannot be fulfilled, so the amount was paid downstream without reimbursement. A close "
              + "replaced by a reorg can carry the HTLC again (NL-330); check both channels by hand",
                fulfilled.HtlcId, fulfilled.ChannelId, fulfilled.PaymentHash, incomingHtlcId, incomingChannelId);
            await RecordForwardLostOnchainAsync(incomingChannelId, incomingHtlcId, fulfilled);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The fulfill is a no-op either way; the alert must not break the switch's event handling
            _logger.LogWarning(e, "Could not check the upstream of HTLC {HtlcId} of channel {ChannelId} for the "
                              + "late preimage", incomingHtlcId, incomingChannelId);
        }
    }

    /// <summary>
    /// NL-602: records the loss <see cref="AlertUpstreamAlreadyFailedAsync"/> alerts about as a
    /// <c>ForwardLostOnchain</c> accounting event, in its own save (no state changes with it: the upstream was failed
    /// long before). Once per forward: the alert's memory guard holds within a run, the stored key across restarts
    /// (the resolvers raise the fulfill on every block until the output is irrevocable). Best effort, never thrown.
    /// </summary>
    private async Task RecordForwardLostOnchainAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                                     OutgoingHtlcFulfilled fulfilled)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                              incomingHtlcId);
            if (circuit is null)
            {
                _logger.LogWarning("No circuit for upstream HTLC {HtlcId} of channel {ChannelId}: its on-chain loss is "
                                 + "not recorded in the accounting feed", incomingHtlcId, incomingChannelId);
                return;
            }

            var key = AccountingEventKeys.ForwardLostOnchain(incomingChannelId, incomingHtlcId);
            if (await unitOfWork.AccountingEventDbRepository.ExistsAsync(key))
                return;

            _channelMemoryRepository.TryGetChannel(incomingChannelId, out var incoming);
            _channelMemoryRepository.TryGetChannel(fulfilled.ChannelId, out var outgoing);
            unitOfWork.AccountingEventDbRepository.Add(
                PaymentAccountingEvents.ForwardLostOnchain(circuit, fulfilled.ChannelId, fulfilled.HtlcId, incoming,
                                                           outgoing, _timeProvider.GetUtcNow(), CurrentHeight));
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not record the on-chain loss of the forward of HTLC {HtlcId} of channel "
                              + "{ChannelId}", incomingHtlcId, incomingChannelId);
        }
    }

    /// <summary>
    /// The return packet for the upstream <c>update_fail_htlc</c>: the downstream error onion wrapped with our
    /// <c>ammag</c> key, or (for <c>update_fail_malformed_htlc</c>) our own error onion with the code and
    /// <c>sha256_of_onion</c> (BOLT 2; we act as the erring node), or (for an HTLC settled on chain without a preimage,
    /// <see cref="HtlcRemovalKind.OnchainTimeout"/>) our own <c>permanent_channel_failure</c>.
    /// </summary>
    private byte[] ReturnPacket(Secret incomingSharedSecret, HtlcRemoval removal)
    {
        // Settled on chain without a preimage (BOLT 5 plan O3-T4): no downstream error exists, we are the erring node
        // and the outgoing channel is closed for good
        if (removal.Kind == HtlcRemovalKind.OnchainTimeout)
            return _failureOnionService.CreateErrorPacket(incomingSharedSecret,
                                                          FailureMessage.PermanentChannelFailure());

        if (removal.Kind != HtlcRemovalKind.FailMalformed)
            return _failureOnionService.WrapErrorPacket(incomingSharedSecret, removal.Reason.Span);

        try
        {
            return _failureOnionService.CreateErrorPacketFromMalformed(incomingSharedSecret,
                                                                       (FailureCode)removal.FailureCode,
                                                                       removal.Sha256OfOnion.Span);
        }
        catch (ArgumentException e)
        {
            // The receive handler refuses a code without BADONION, so this is a malformed hash length
            _logger.LogWarning("Cannot convert update_fail_malformed_htlc 0x{Code:x4}: {Reason}", removal.FailureCode,
                               e.Message);
            return _failureOnionService.CreateErrorPacket(incomingSharedSecret,
                                                          FailureMessage.TemporaryChannelFailure());
        }
    }

    /// <summary>
    /// <see cref="ReturnPacket"/> with <c>attribution_data</c> (BOLT 4 intermediate node, NL-326): a downstream failure
    /// is wrapped together with its attribution data (an all-zero block when none came), and a failure we originate
    /// here (malformed downstream, settled on chain) gets fresh attribution data; both carry our hold time.
    /// </summary>
    private AttributedErrorPacket AttributedReturnPacket(Secret incomingSharedSecret, HtlcRemoval removal,
                                                         uint holdTime)
    {
        var service = _attributionDataService!;
        if (removal.Kind == HtlcRemovalKind.OnchainTimeout)
            return service.CreateErrorPacket(incomingSharedSecret, FailureMessage.PermanentChannelFailure(), holdTime);

        if (removal.Kind != HtlcRemovalKind.FailMalformed)
            return service.WrapErrorPacket(incomingSharedSecret, removal.Reason.Span, removal.AttributionData.Span,
                                           holdTime);

        try
        {
            return service.CreateErrorPacketFromMalformed(incomingSharedSecret, (FailureCode)removal.FailureCode,
                                                          removal.Sha256OfOnion.Span, holdTime);
        }
        catch (ArgumentException e)
        {
            _logger.LogWarning("Cannot convert update_fail_malformed_htlc 0x{Code:x4}: {Reason}", removal.FailureCode,
                               e.Message);
            return service.CreateErrorPacket(incomingSharedSecret, FailureMessage.TemporaryChannelFailure(), holdTime);
        }
    }

    /// <summary>
    /// Whether our removal of <paramref name="incoming"/> carries <c>attribution_data</c> (BOLT 4, NL-326): only when
    /// we advertise <c>option_attribution_data</c> and the incoming <c>update_add_htlc</c> had no <c>path_key</c>.
    /// </summary>
    private bool AddsAttribution(HtlcRecord? incoming) =>
        _attributionDataService is not null && _advertisesAttribution && incoming is { PathKey: null };

    /// <summary>
    /// Fails an incoming HTLC as the erring node with our own error onion, with <c>attribution_data</c> and our hold
    /// time when <see cref="AddsAttribution"/>.
    /// </summary>
    private async Task SendFailureAsync(ChannelId channelId, ulong htlcId, HtlcRecord? incoming, Secret sharedSecret,
                                        FailureMessage failure, CancellationToken cancellationToken,
                                        bool? blindedIntroduction = null, TrampolineFailureKeys? trampoline = null)
    {
        // An HTLC that reached us as a trampoline node (NL-875): the trampoline layer under the outer one
        if (trampoline is not null)
        {
            await SendTrampolineFailureAsync(channelId, htlcId, incoming, trampoline, failure, cancellationToken);
            return;
        }

        // Inside a blinded route (M5): a node that got the path_key in update_add_htlc fails every HTLC with
        // update_fail_malformed_htlc + invalid_onion_blinding; the introduction node, when it is not the final node,
        // with update_fail_htlc + invalid_onion_blinding (BOLT 2 "Removing an HTLC", BOLT 4 "Returning Errors")
        if (incoming is { PathKey: not null })
        {
            await _channelOperations.FailMalformedHtlcAsync(channelId, htlcId, FailureCode.InvalidOnionBlinding,
                                                            Sha256Of(incoming.OnionRoutingPacket), cancellationToken);
            return;
        }

        if (incoming is not null && (blindedIntroduction ?? await IsBlindedIntroductionForwardAsync(incoming)))
            failure = FailureMessage.InvalidOnionBlinding(Sha256Of(incoming.OnionRoutingPacket));

        // BOLT 2 / BOLT 4: SHOULD add a random delay before an introduction node's invalid_onion_blinding
        if (failure.Code == FailureCode.InvalidOnionBlinding)
            await DelayBlindedErrorAsync(cancellationToken);

        if (AddsAttribution(incoming))
        {
            var holdTime = await _channelOperations.GetHoldTimeAsync(channelId, htlcId, cancellationToken);
            await _channelOperations.FailHtlcAsync(channelId, htlcId,
                                                   _attributionDataService!.CreateErrorPacket(sharedSecret, failure,
                                                       holdTime),
                                                   cancellationToken);
            return;
        }

        await _channelOperations.FailHtlcAsync(channelId, htlcId,
                                               _failureOnionService.CreateErrorPacket(sharedSecret, failure),
                                               cancellationToken);
    }

    /// <summary>
    /// Fails an incoming HTLC that reached us as a trampoline node as the erring node (NL-875, BOLTs PR 836 "Returning
    /// Errors"): the reason is created with the trampoline secret, then obfuscated with the outer one
    /// (<see cref="TrampolineErrorPackets"/>; <c>attribution_data</c> on the outer layer only, when
    /// <see cref="AddsAttribution"/>). Past the introduction node of a blinded trampoline route,
    /// <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> instead.
    /// </summary>
    private async Task SendTrampolineFailureAsync(ChannelId channelId, ulong htlcId, HtlcRecord? incoming,
                                                  TrampolineFailureKeys trampoline, FailureMessage failure,
                                                  CancellationToken cancellationToken)
    {
        if (trampoline.BlindedMalformedSha256 is { } sha256)
        {
            await _channelOperations.FailMalformedHtlcAsync(channelId, htlcId, FailureCode.InvalidOnionBlinding,
                                                            new Hash(sha256), cancellationToken);
            return;
        }

        if (failure.Code == FailureCode.InvalidOnionBlinding)
            await DelayBlindedErrorAsync(cancellationToken);

        if (AddsAttribution(incoming))
        {
            var holdTime = await _channelOperations.GetHoldTimeAsync(channelId, htlcId, cancellationToken);
            await _channelOperations.FailHtlcAsync(channelId, htlcId,
                                                   TrampolineErrorPackets.CreateAttributed(
                                                       _failureOnionService, _attributionDataService!,
                                                       trampoline.TrampolineSharedSecret, trampoline.OuterSharedSecret,
                                                       failure, holdTime),
                                                   cancellationToken);
            return;
        }

        var reason = _trampolineFailureOnionService is not null
                         ? TrampolineErrorPackets.Create(_trampolineFailureOnionService,
                                                         trampoline.TrampolineSharedSecret,
                                                         trampoline.OuterSharedSecret, failure)
                         : _failureOnionService.WrapErrorPacket(trampoline.OuterSharedSecret,
                                                                _failureOnionService.CreateErrorPacket(
                                                                    trampoline.TrampolineSharedSecret, failure));
        await _channelOperations.FailHtlcAsync(channelId, htlcId, reason, cancellationToken);
    }

    /// <summary>
    /// Waits a uniformly random time up to <see cref="HtlcSwitchOptions.BlindedErrorMaxDelay"/> (real time: the delay
    /// hides timing from the sender and must not wait on a test clock).
    /// </summary>
    private Task DelayBlindedErrorAsync(CancellationToken cancellationToken)
    {
        if (_blindedErrorMaxDelay <= TimeSpan.Zero)
            return Task.CompletedTask;

        var maxMs = (int)Math.Min(_blindedErrorMaxDelay.TotalMilliseconds, int.MaxValue - 1);
        var delayMs = RandomNumberGenerator.GetInt32(maxMs + 1);
        return delayMs == 0 ? Task.CompletedTask : Task.Delay(delayMs, cancellationToken);
    }

    /// <summary>
    /// Fulfills an incoming HTLC we accepted as the final node, with <paramref name="stage"/> in the fulfill's save;
    /// with <c>attribution_data</c> and our hold time when <see cref="AddsAttribution"/>.
    /// </summary>
    private async Task FulfillFinalAsync(ChannelId channelId, ulong htlcId, Secret preimage, Secret sharedSecret,
                                         Func<IUnitOfWork, Task>? stage, CancellationToken cancellationToken)
    {
        if (AddsAttribution(GetAwaitingIncomingHtlc(channelId, htlcId)))
        {
            var holdTime = await _channelOperations.GetHoldTimeAsync(channelId, htlcId, cancellationToken);
            await _channelOperations.FulfillHtlcAsync(channelId, htlcId, preimage,
                                                      _attributionDataService!.CreateFulfillment(sharedSecret,
                                                          holdTime),
                                                      stage, cancellationToken);
            return;
        }

        if (stage is null)
            await _channelOperations.FulfillHtlcAsync(channelId, htlcId, preimage, cancellationToken);
        else
            await _channelOperations.FulfillHtlcAsync(channelId, htlcId, preimage, stage, cancellationToken);
    }

    /// <summary>
    /// A downstream failure of a forward inside a blinded route (M5): never relayed. With a path_key in the incoming
    /// <c>update_add_htlc</c> we send <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>; as the
    /// introduction node our own <c>invalid_onion_blinding</c> in <c>update_fail_htlc</c>. False when the incoming HTLC
    /// is not blinded (the caller relays the failure as usual).
    /// </summary>
    private async Task<bool> FailBlindedUpstreamAsync(ChannelId incomingChannelId, HtlcRecord incoming,
                                                      OutgoingHtlcFailed failed, CancellationToken cancellationToken)
    {
        if (incoming.PathKey is null && !await IsBlindedIntroductionForwardAsync(incoming, failed))
            return false;

        try
        {
            if (incoming.PathKey is null
             && await GetIncomingSharedSecretAsync(incomingChannelId, incoming) is { } sharedSecret)
                await SendFailureAsync(incomingChannelId, incoming.Id, incoming, sharedSecret,
                                       FailureMessage.InvalidOnionBlinding(Sha256Of(incoming.OnionRoutingPacket)),
                                       cancellationToken, blindedIntroduction: true);
            else
                await _channelOperations.FailMalformedHtlcAsync(incomingChannelId, incoming.Id,
                                                                FailureCode.InvalidOnionBlinding,
                                                                Sha256Of(incoming.OnionRoutingPacket),
                                                                cancellationToken);

            LogFailedBack(incomingChannelId, incoming,
                          $"downstream {failed.Removal.Kind} on channel {failed.ChannelId} inside a blinded route");
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            // The outgoing record is kept until the upstream is resolved: the replayed lock-in fails from it
            _logger.LogWarning("Could not fail upstream HTLC {HtlcId} of channel {ChannelId} yet: {Reason}",
                               incoming.Id, incomingChannelId, e.Message);
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="incoming"/> came to us as the introduction node of a blinded route and we are not its
    /// final node: its <c>update_add_htlc</c> had no path_key and its payload a <c>current_path_key</c>. Peels the onion
    /// again (no replay check), whatever <c>option_route_blinding</c> is set to now: a forward made before a restart
    /// with the feature off is still inside its blinded route.
    /// </summary>
    private Task<bool> IsBlindedIntroductionForwardAsync(HtlcRecord incoming) =>
        BlindedHtlcFailures.IsIntroductionForwardAsync(_onionProcessor, incoming);

    /// <summary>
    /// The same for a forward whose downstream HTLC <paramref name="failed"/>: a blinded forward is offered with the
    /// next path_key in <c>update_add_htlc</c>, which the outgoing record keeps (persisted), so an outgoing record with a
    /// path_key under an incoming HTLC without one makes us the introduction node, without a peel and whatever the
    /// feature setting. Only when that record is gone (its channel no longer loaded) is the onion peeled again.
    /// </summary>
    private async Task<bool> IsBlindedIntroductionForwardAsync(HtlcRecord incoming, OutgoingHtlcFailed failed)
    {
        if (incoming.PathKey is not null)
            return false;

        if (await FindOutgoingRecordAsync(failed.ChannelId, failed.HtlcId) is { } outgoing)
            return outgoing.PathKey is not null;

        var (_, closedRecord) = await FindClosedOutgoingRecordAsync(failed.ChannelId, failed.HtlcId);
        return closedRecord is not null
                   ? closedRecord.PathKey is not null
                   : await IsBlindedIntroductionForwardAsync(incoming);
    }

    private async Task<Secret?> GetIncomingSharedSecretAsync(ChannelId incomingChannelId, HtlcRecord incoming)
    {
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                                                                                            incoming.Id);
            if (circuit is not null)
                return circuit.IncomingSharedSecret;

            var stored = await unitOfWork.ChannelStateDbRepository.GetOnionSharedSecretAsync(
                             incomingChannelId, incoming.Key);
            if (stored is not null)
                return stored;
        }

        // Peel again (without the replay cache): the secret only depends on the onion and our node key
        var result = await _onionProcessor.ProcessAsync(incoming.OnionRoutingPacket, incoming.PaymentHash,
                                                        replayOwner: null, incoming.PathKey);
        return result.SharedSecretOrNull;
    }

    private async Task NotifyLocalAsync(ChannelId channelId, ulong htlcId,
                                        Func<ILocalPaymentHtlcHandler, Task> notify)
    {
        foreach (var handler in _localPaymentHandlers)
        {
            try
            {
                await notify(handler);
            }
            catch (Exception e)
            {
                // Keep the settled record (and so the event) for the next replay
                _unhandledLocalResolutions[(channelId, htlcId)] = 0;
                _logger.LogError(e, "Payment handler failed on HTLC {HtlcId} of channel {ChannelId}", htlcId,
                                 channelId);
                return;
            }
        }

        _unhandledLocalResolutions.TryRemove((channelId, htlcId), out _);
        if (_localPaymentHandlers.Count == 0 && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("HTLC {HtlcId} of our payment on channel {ChannelId} resolved (no payment handler)",
                                   htlcId, channelId);
    }

    /// <summary>
    /// NL-875: hands an origin-3 resolution to the registered <see cref="ITrampolineHtlcHandler"/>. Without one, or when
    /// it throws, the outgoing record is kept (and so the event replayed) until a handler takes it.
    /// </summary>
    private async Task NotifyTrampolineAsync(ChannelId channelId, ulong htlcId,
                                             Func<ITrampolineHtlcHandler, Task> notify)
    {
        if (_trampolineHandler is null)
        {
            _unhandledTrampolineResolutions[(channelId, htlcId)] = 0;
            LogMissingTrampolineHandler(channelId, htlcId);
            return;
        }

        try
        {
            await notify(_trampolineHandler);
            _unhandledTrampolineResolutions.TryRemove((channelId, htlcId), out _);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _unhandledTrampolineResolutions[(channelId, htlcId)] = 0;
            _logger.LogError(e, "Trampoline relay handler failed on HTLC {HtlcId} of channel {ChannelId}", htlcId,
                             channelId);
        }
    }

    /// <summary>
    /// NL-875: a replayed lock-in of an incoming HTLC that is a trampoline relay part goes to the relay handler; without
    /// one it is left alone (the deadline monitor and the resolvers protect it).
    /// </summary>
    private async Task HandleRelayPartLockedInAsync(IncomingHtlcLockedIn lockedIn, TrampolineRelayPartModel part,
                                                    CancellationToken cancellationToken)
    {
        if (_trampolineHandler is null)
        {
            LogMissingTrampolineHandler(lockedIn.ChannelId, lockedIn.HtlcId);
            return;
        }

        try
        {
            await _trampolineHandler.HandleIncomingPartLockedInAsync(lockedIn, part, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException and not CommitmentRefusedException
                                      and not KeyNotFoundException)
        {
            _logger.LogError(e, "Trampoline relay handler failed on incoming HTLC {HtlcId} of channel {ChannelId}",
                             lockedIn.HtlcId, lockedIn.ChannelId);
        }
    }

    /// <summary>
    /// NL-875: an outgoing trampoline HTLC's archived record may go once the handler took its resolution and says so,
    /// the relay is completed, and every incoming part has its removal (or is gone from a loaded channel). An incoming
    /// channel that is not loaded keeps the record, as for a forward.
    /// </summary>
    private async Task<bool> IsTrampolineRelayDoneAsync(OutgoingHtlcSettled settled, Hash paymentHash,
                                                        CancellationToken cancellationToken)
    {
        if (_trampolineHandler is null
         || _unhandledTrampolineResolutions.ContainsKey((settled.ChannelId, settled.HtlcId)))
            return false;

        try
        {
            if (!await _trampolineHandler.HandleSettledAsync(settled, paymentHash, cancellationToken))
                return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Trampoline relay handler failed on settled HTLC {HtlcId} of channel {ChannelId}",
                             settled.HtlcId, settled.ChannelId);
            return false;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash) is not { Relay.IsCompleted: true } relay)
            return false;

        foreach (var part in relay.Parts)
        {
            if (!_channelMemoryRepository.TryGetChannel(part.ChannelId, out var incomingChannel)
             || incomingChannel.Commitments is not { } commitments
             || commitments.GetHtlc(HtlcDirection.Incoming, part.HtlcId) is
             { State: HtlcState.RcvdAddAckRevocation })
                return false;
        }

        return true;
    }

    private void LogMissingTrampolineHandler(ChannelId channelId, ulong htlcId)
    {
        if (Interlocked.Exchange(ref _missingTrampolineHandlerLogged, 1) == 0)
            _logger.LogWarning("HTLC {HtlcId} of channel {ChannelId} belongs to a trampoline relay, but no trampoline "
                             + "relay handler is registered: its events are kept for a later replay", htlcId,
                               channelId);
    }

    #endregion

    #region Helpers

    private uint CurrentHeight => _blockchainMonitor?.LastProcessedBlockHeight ?? 0;

    /// <summary>
    /// The tip the monitor persisted when it last stopped (NL-267): while it has not reported a height in this run,
    /// that is the height it will resume from, so the forward checks use it. 0 without a stored state (a fresh node
    /// before its first block).
    /// </summary>
    private async Task<uint> StoredTipHeightAsync()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return (await unitOfWork.BlockchainStateDbRepository.GetStateAsync())?.LastProcessedHeight ?? 0;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the stored chain tip for the forward checks");
            return 0;
        }
    }

    /// <summary>
    /// The channel can no longer carry an update: it failed (our commitment is being broadcast) or a commitment is on
    /// chain. Its incoming HTLCs are resolved on chain only (NL-316).
    /// </summary>
    private bool IsOnchain(ChannelId channelId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.State is ChannelState.Failed or ChannelState.OnchainResolving;

    /// <summary>
    /// The peer added incoming HTLC <paramref name="htlcId"/> after our <c>shutdown</c> (NL-279,
    /// <see cref="ChannelModel.IsRemoteHtlcAddedAfterLocalShutdown"/>).
    /// </summary>
    private bool IsAddedAfterOurShutdown(ChannelId channelId, ulong htlcId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.IsRemoteHtlcAddedAfterLocalShutdown(htlcId);

    /// <summary>The incoming HTLC when it is locked in and no removal was sent for it yet.</summary>
    private HtlcRecord? GetAwaitingIncomingHtlc(ChannelId channelId, ulong htlcId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.Commitments?.GetHtlc(HtlcDirection.Incoming, htlcId) is { State: HtlcState.RcvdAddAckRevocation } htlc
            ? htlc
            : null;

    /// <summary>
    /// The open channel the onion's <c>short_channel_id</c> names: one of our aliases or the peer's alias, or the real
    /// scid unless <c>option_scid_alias</c> is in the channel type (<c>Compulsory</c>): BOLT 2 forbids routing into
    /// such a channel by its real scid. A channel that only negotiated the feature (<c>Optional</c>, e.g. a public
    /// channel, announced by its real scid) accepts both (NL-348).
    /// </summary>
    /// <remarks>Splicing plan D12 (SP2-0 seam, lane SP2-B): a short channel id a splice lock retired resolves through
    /// <see cref="IRetiredScidMap"/> for 72 blocks, after the live ones and the aliases.</remarks>
    private ChannelModel? ResolveOutgoingChannel(ShortChannelId shortChannelId) =>
        _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open
                                                && (c.LocalAliases?.Contains(shortChannelId) == true
                                                 || c.RemoteAlias == shortChannelId
                                                 || (c.ChannelParams.UseScidAlias != FeatureSupport.Compulsory
                                                  && c.ShortChannelId != default
                                                  && c.ShortChannelId == shortChannelId)))
                                .FirstOrDefault()
     ?? ResolveRetiredChannel(shortChannelId);

    /// <summary>The open channel a retired short channel id still names (D12), or null.</summary>
    private ChannelModel? ResolveRetiredChannel(ShortChannelId shortChannelId) =>
        _retiredScidMap is not null && _retiredScidMap.TryResolve(shortChannelId, out var channelId)
     && _channelMemoryRepository.TryGetChannel(channelId, out var channel) && channel.State == ChannelState.Open
            ? channel
            : null;

    /// <summary>
    /// Inside a blinded route (M5), the open channel to <paramref name="nextNodeId"/> (the recipient data's
    /// <c>next_node_id</c>) to forward over; non-strict forwarding lets us pick any of them (BOLT 4). The first usable
    /// one that can send <paramref name="amount"/>, else the usable one that can send the most, else the first
    /// (the policy then refuses it with the right failure).
    /// </summary>
    private async Task<(ChannelModel? Channel, OutgoingChannelInfo? Info)> SelectOutgoingChannelAsync(
        CompactPubKey nextNodeId, LightningMoney amount, CancellationToken cancellationToken)
    {
        var candidates = _channelMemoryRepository
                        .FindChannels(c => c.State == ChannelState.Open && c.RemoteNodeId == nextNodeId)
                        .Where(c => c.ShortChannelId != default || c.LocalAliases?.Count > 0)
                        .ToList();
        var infos = new List<OutgoingChannelInfo>(candidates.Count);
        foreach (var candidate in candidates)
            infos.Add(await DescribeAsync(candidate, cancellationToken));

        var index = SelectOutgoingCandidate(infos, amount);
        return index < 0 ? (null, null) : (candidates[index], infos[index]);
    }

    /// <summary>
    /// The candidate a blinded forward by <c>next_node_id</c> goes over: the first usable one that can send
    /// <paramref name="amount"/>, else the usable one that can send the most, else the first; -1 without candidates.
    /// </summary>
    internal static int SelectOutgoingCandidate(IReadOnlyList<OutgoingChannelInfo> candidates, LightningMoney amount)
    {
        var largest = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            var info = candidates[i];
            if (!info.IsUsable)
                continue;
            if (info.AvailableToSend >= amount)
                return i;
            if (largest < 0 || info.AvailableToSend > candidates[largest].AvailableToSend)
                largest = i;
        }

        return largest >= 0 ? largest : candidates.Count > 0 ? 0 : -1;
    }

    /// <summary>The short_channel_id a forward over <paramref name="channel"/> is recorded under.</summary>
    private static ShortChannelId ScidOf(ChannelModel channel) =>
        channel.ShortChannelId != default && channel.ChannelParams.UseScidAlias != FeatureSupport.Compulsory
            ? channel.ShortChannelId
            : channel.LocalAliases!.First();

    /// <summary>A snapshot of the outgoing channel for the policy (the engine checks the offer again).</summary>
    private async Task<OutgoingChannelInfo> DescribeAsync(ChannelModel channel, CancellationToken cancellationToken)
    {
        var commitments = channel.Commitments;
        var usable = commitments is not null && channel.State == ChannelState.Open && !channel.DataLossDetected
                  && await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId,
                                                           cancellationToken);

        ulong availableMsat = 0;
        if (commitments is not null)
        {
            // Our settled balance, minus what our open HTLCs still hold and the reserve the peer requires of us
            var pendingOutgoing = commitments.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Outgoing
                                                                   && h.Removal is null)
                                             .Aggregate(0UL, (sum, h) => sum + h.AmountMsat);
            var reserve = channel.ChannelParams.Remote.ChannelReserveAmount.MilliSatoshi;
            var local = commitments.LocalBalanceMsat;
            var available = local > pendingOutgoing + reserve ? local - pendingOutgoing - reserve : 0;

            // NL-268: as the funder we also pay the commitment fee of one more HTLC above our reserve at offer time
            // (B2-ADD-S01/S02): estimate it with the engine's own calculator, so this pre-check does not pass a
            // forward the offer then refuses with temporary_channel_failure
            if (commitments.Params.LocalIsFunder)
            {
                var feeMsat = CommitmentFeeForOneMoreHtlcMsat(commitments);
                available = available > feeMsat ? available - feeMsat : 0;
            }

            availableMsat = available;
        }

        return new OutgoingChannelInfo(channel.ChannelId, usable, channel.ChannelParams.Remote.HtlcMinimumAmount,
                                       LightningMoney.MilliSatoshis(availableMsat));
    }

    /// <summary>
    /// What offering one more HTLC would cost us as the funder (NL-268): the fee (and, with <c>option_anchors</c>,
    /// both anchors) of the next commitment with one more untrimmed HTLC, at the feerate the next commitments converge
    /// to — the terms the engine judges an offer by (B2-ADD-S01/S02), without its fee-spike buffer. Our latest
    /// commitment's untrimmed HTLC count undercounts HTLCs still in flight; the estimate errs optimistic only by their
    /// output weight, and the engine stays authoritative.
    /// </summary>
    private static ulong CommitmentFeeForOneMoreHtlcMsat(ChannelCommitments commitments)
    {
        var commitmentParams = commitments.Params;
        var feerate = Math.Max(commitments.FeeratePerKw(CommitmentSide.Local), commitments.LatestFeeratePerKw);
        var untrimmed = CommitmentFeeCalculator.UntrimmedHtlcCount(commitments.LocalCommit.Spec,
                                                                   commitmentParams.Local.DustLimitSatoshis,
                                                                   commitmentParams.OptionAnchors);
        return CommitmentFeeCalculator.FunderCostSatoshis(feerate, commitmentParams.OptionAnchors, untrimmed + 1)
             * 1_000;
    }

    /// <summary>
    /// The failure for a policy decision, with our signed <c>channel_update</c> of the outgoing channel for the UPDATE
    /// codes (W0-E/W1-E types).
    /// </summary>
    private FailureMessage ToFailureMessage(ForwardingDecision decision, ChannelModel? outgoing,
                                            ShortChannelId requestedScid)
    {
        var code = decision.FailureCode ?? FailureCode.TemporaryChannelFailure;
        var update = code.IsUpdate() ? UpdateFor(outgoing, requestedScid) : [];
        return code switch
        {
            FailureCode.UnknownNextPeer => FailureMessage.UnknownNextPeer(),
            FailureCode.TemporaryChannelFailure => FailureMessage.TemporaryChannelFailure(update),
            FailureCode.AmountBelowMinimum => FailureMessage.AmountBelowMinimum(
                LightningMoney.MilliSatoshis(decision.HtlcMsat ?? 0UL), update),
            FailureCode.FeeInsufficient => FailureMessage.FeeInsufficient(
                LightningMoney.MilliSatoshis(decision.HtlcMsat ?? 0UL), update),
            FailureCode.IncorrectCltvExpiry => FailureMessage.IncorrectCltvExpiry(decision.CltvExpiry ?? 0, update),
            FailureCode.ExpiryTooSoon => FailureMessage.ExpiryTooSoon(update),
            FailureCode.ExpiryTooFar => FailureMessage.ExpiryTooFar(),
            FailureCode.PermanentChannelFailure => FailureMessage.PermanentChannelFailure(),
            _ => FailureMessage.TemporaryNodeFailure()
        };
    }

    /// <summary>
    /// Our last signed <c>channel_update</c> for <paramref name="outgoing"/> as a failure field, when its
    /// <c>short_channel_id</c> is the one the onion used (BOLT 4); otherwise one signed for the short channel id the
    /// onion used, when the channel goes by it (NL-266: an <c>option_scid_alias</c> channel's standing update names
    /// the peer's alias, while the onion may name the channel by one of our local aliases or the real scid); else
    /// empty (<c>len = 0</c>).
    /// </summary>
    private byte[] UpdateFor(ChannelModel? outgoing, ShortChannelId requestedScid)
    {
        if (outgoing is null || _channelUpdateService is null)
            return [];

        if (_channelUpdateService.TryGetLocalChannelUpdate(outgoing.ChannelId, out var update)
         && update is not null && update.Payload.ShortChannelId == requestedScid)
            return FailureChannelUpdateFactory.Encode(update);

        var forScid = _channelUpdateService.CreateChannelUpdateForScid(outgoing, requestedScid);
        return forScid is null ? [] : FailureChannelUpdateFactory.Encode(forScid);
    }

    private async Task<HtlcOrigin?> GetOriginAsync(ChannelId channelId, ulong htlcId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                   channelId, new HtlcKey(HtlcDirection.Outgoing, htlcId));
    }

    /// <summary>
    /// Re-reads the circuit of an incoming HTLC and, when <paramref name="when"/> holds, applies
    /// <paramref name="change"/> and saves it (one save). A missing circuit or a status that already moved on is left
    /// alone.
    /// </summary>
    private async Task UpdateCircuitAsync(ChannelId incomingChannelId, ulong incomingHtlcId,
                                          Func<ForwardCircuitModel, bool> when, Action<ForwardCircuitModel> change)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                                                                                        incomingHtlcId);
        if (circuit is null || !when(circuit))
            return;

        var before = circuit.Status;
        try
        {
            change(circuit);
        }
        catch (InvalidOperationException e)
        {
            _logger.LogError(e, "Circuit of HTLC {HtlcId} of channel {ChannelId} cannot move from {Status}",
                             incomingHtlcId, incomingChannelId, circuit.Status);
            return;
        }

        await unitOfWork.ForwardCircuitDbRepository.UpdateAsync(circuit);
        if (before != ForwardCircuitStatus.Fulfilled && circuit.Status == ForwardCircuitStatus.Fulfilled)
        {
            // NL-602: a circuit becomes Fulfilled once (its status only moves forward and was re-read here), so the
            // forward's fee is recorded in this save, whichever path resolved it (live, replay, on chain)
            PaymentAccountingEvents.TryStage(unitOfWork, () =>
            {
                _channelMemoryRepository.TryGetChannel(incomingChannelId, out var incoming);
                ChannelModel? outgoing = null;
                if (circuit.OutgoingChannelId is { } outgoingChannelId)
                    _channelMemoryRepository.TryGetChannel(outgoingChannelId, out outgoing);
                return PaymentAccountingEvents.ForwardSettled(circuit, incoming, outgoing);
            }, _logger);
        }

        await unitOfWork.SaveChangesAsync();

        // NL-892: a forward settled after its incoming channel's close, whose incoming HTLC the close trimmed
        if (before != ForwardCircuitStatus.Fulfilled && circuit.Status == ForwardCircuitStatus.Fulfilled)
            await StageLateTrimmedLossAsync(incomingChannelId, incomingHtlcId);
    }

    /// <summary>
    /// NL-892: a forward or an invoice part settled after the force close of its incoming channel had been recorded,
    /// whose incoming HTLC that close trimmed (below dust, no output), is lost with the close: its
    /// <c>ForwardLostOnchain</c>/<c>InvoiceLostOnchain</c> goes in a save of its own right after the settle's (the
    /// close's save could not book it, and no resolution ever sees the HTLC; the executor's rounds catch a crash in
    /// between). Nothing for a channel that is not closed on chain. Never throws: a failure is logged.
    /// </summary>
    private async Task StageLateTrimmedLossAsync(ChannelId channelId, ulong htlcId)
    {
        // Only a channel closing on chain (Failed waits for its commitment) or gone from memory (Closed) can have one
        if (_channelMemoryRepository.TryGetChannel(channelId, out var loaded)
         && loaded.State is not (ChannelState.Failed or ChannelState.OnchainResolving or ChannelState.Closed))
            return;

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (unitOfWork.AccountingEventDbRepository is null
             || await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is not { } close)
                return;

            var channel = loaded ?? await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
            if (channel is null
             || !await OnchainResolutionExecutor.StageTrimmedIncomingLossesAsync(
                    unitOfWork, channel, close, htlcId, CurrentHeight, _timeProvider.GetUtcNow(), _logger,
                    CancellationToken.None))
                return;

            await unitOfWork.SaveChangesAsync();
            _logger.LogWarning("Incoming HTLC {HtlcId} of channel {ChannelId} was settled after the channel's close, "
                             + "which trimmed it: recorded as lost on chain", htlcId, channelId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not record the loss of trimmed incoming HTLC {HtlcId} of closed channel "
                              + "{ChannelId}", htlcId, channelId);
        }
    }

    private static Hash Sha256Of(ReadOnlyMemory<byte> bytes) =>
        new(SHA256.HashData(bytes.Span));

    private void LogFailedBack(ChannelId channelId, HtlcRecord htlc, string reason)
    {
        _logger.LogInformation("Failed back incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}: {Reason}",
                               htlc.Id, htlc.AmountMsat, channelId, reason);
    }

    #endregion
}

/// <summary>The invoice left <c>Open</c> between the final-hop check and the fulfill's save (e.g. it was canceled).
/// </summary>
internal sealed class InvoiceNotOpenException(string message) : InvalidOperationException(message);