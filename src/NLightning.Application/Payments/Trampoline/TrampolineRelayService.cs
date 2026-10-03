using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Trampoline;

using Channels.RoutingPolicies;
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
using Domain.Node;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Codecs;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Onion;
using Switch;

/// <summary>
/// The trampoline relay engine (BOLTs PR 836, NL-875 TR3, decision D-TR5): collects the incoming parts of a payment
/// that reached us as an intermediate trampoline node, checks the complete set against our trampoline policy, pays the
/// next trampoline node (or the recipient's blinded paths) through the <see cref="ITrampolineLegSender"/>, and resolves
/// every incoming part from the leg's outcome.
/// </summary>
/// <remarks>
/// <para>Singleton; it is the node's <see cref="ITrampolineRelayIngress"/> (the switch resolves it per part),
/// <see cref="ITrampolineHtlcHandler"/> (the switch's origin-3 events) and <see cref="ITrampolineLegObserver"/>.
/// Every decision about a relay runs under its payment-hash lock; the leg is started after that lock is released (the
/// leg sender may report its end from within <see cref="ITrampolineLegSender.StartAsync"/>).</para>
/// <para>Saves, in order (the repository's contract): a part with the relay row (the first part creates it) before
/// the part is counted; <c>Sending</c> before the leg starts; <c>Fulfilled</c> with the accounting event and the
/// preimage on the incoming records before any part is fulfilled; <c>Failed</c> only once no outgoing HTLC of the relay
/// is unresolved, before any part is failed. A part whose fulfill or failure is refused (link down, quiescence) is
/// resolved again by its replayed lock-in (<see cref="HandleIncomingPartLockedInAsync"/>) from the stored status.</para>
/// <para>Failures for a part are created with its trampoline secret, then its outer secret
/// (<see cref="TrampolineErrorPackets"/>, attribution on the outer layer); inside a blinded trampoline route a hop past
/// the introduction node sends <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> and the introduction
/// node its own <c>invalid_onion_blinding</c>, whatever the cause (TR-R-14). The blinded facts and the trampoline packet
/// hash are kept in memory from the part's onion; after a restart the part's stored onion is peeled again.</para>
/// <para>Timers: the <c>mpp_timeout</c> of a collecting relay runs from its creation (<c>Node:Switch:MppTimeout</c>);
/// a sending relay has a watchdog at the leg's deadline plus one <see cref="TrampolineOptions.LegTimeout"/> that fails
/// the relay only when no outgoing HTLC is unresolved (and fulfills it when one learnt the preimage).</para>
/// </remarks>
public sealed class TrampolineRelayService : ITrampolineRelayIngress, ITrampolineHtlcHandler, ITrampolineLegObserver,
                                             IDisposable, IAsyncDisposable
{
    private static readonly RoutingOptions s_defaultRouting = new();

    private readonly IAttributionDataService? _attributionDataService;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelPolicyProvider? _channelPolicyProvider;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFailureOnionService _failureOnionService;
    private readonly ILogger<TrampolineRelayService> _logger;
    private readonly INodeDrainState? _nodeDrainState;
    private readonly IOptions<NodeOptions>? _nodeOptions;
    private readonly IncomingOnionProcessor? _onionProcessor;
    private readonly IRetiredScidMap? _retiredScidMap;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ITrampolineFailureOnionService? _trampolineFailureOnionService;
    private readonly TrampolineOptions _options;
    private readonly bool _advertisesAttribution;
    private readonly TimeSpan _blindedErrorMaxDelay;
    private readonly TimeSpan _mppTimeout;

    private readonly KeyedAsyncLock<Hash> _paymentHashLocks = new();
    private readonly ConcurrentDictionary<Hash, ITimer> _mppTimers = new();
    private readonly ConcurrentDictionary<Hash, ITimer> _watchdogs = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), TrampolineFailureKeys> _failureKeys = new();
    private readonly ConcurrentDictionary<Hash, PendingFailure> _failures = new();

    // NL-922: the cltv_expiry_delta each collecting blinded relay keeps (its parts' price check), until it leaves
    // collecting; memory only (see KeptBlindedDeltaOf for a restart)
    private readonly ConcurrentDictionary<Hash, ushort> _blindedHopDeltas = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;

    public TrampolineRelayService(IChannelLockProvider channelLockProvider,
                                  IChannelMemoryRepository channelMemoryRepository,
                                  IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                                  ILogger<TrampolineRelayService> logger, IServiceProvider serviceProvider,
                                  IServiceScopeFactory serviceScopeFactory, TimeProvider? timeProvider = null,
                                  IOptions<TrampolineOptions>? options = null,
                                  IOptions<HtlcSwitchOptions>? switchOptions = null,
                                  IOptions<NodeOptions>? nodeOptions = null,
                                  IBlockchainMonitor? blockchainMonitor = null,
                                  ITrampolineFailureOnionService? trampolineFailureOnionService = null,
                                  IAttributionDataService? attributionDataService = null,
                                  IncomingOnionProcessor? onionProcessor = null,
                                  INodeDrainState? nodeDrainState = null,
                                  IRetiredScidMap? retiredScidMap = null,
                                  IChannelPolicyProvider? channelPolicyProvider = null)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options?.Value ?? new TrampolineOptions();
        _blockchainMonitor = blockchainMonitor;
        _trampolineFailureOnionService = trampolineFailureOnionService;
        _attributionDataService = attributionDataService;
        _onionProcessor = onionProcessor;
        _nodeDrainState = nodeDrainState;
        _retiredScidMap = retiredScidMap;
        _channelPolicyProvider = channelPolicyProvider;
        _nodeOptions = nodeOptions;
        _advertisesAttribution = (nodeOptions?.Value.Features.OptionAttributionData ?? FeatureSupport.Optional)
                              != FeatureSupport.No;
        var mppTimeout = switchOptions?.Value.MppTimeout ?? HtlcSwitchOptions.DefaultMppTimeout;
        _mppTimeout = mppTimeout > TimeSpan.Zero ? mppTimeout : HtlcSwitchOptions.DefaultMppTimeout;
        _blindedErrorMaxDelay = switchOptions?.Value.BlindedErrorMaxDelay
                             ?? HtlcSwitchOptions.DefaultBlindedErrorMaxDelay;
    }

    /// <summary>The payment hashes with a running <c>mpp_timeout</c> timer (tests).</summary>
    internal IReadOnlyCollection<Hash> CollectingPaymentHashes => _mppTimers.Keys.ToList();

    /// <summary>The payment hashes with a running leg watchdog (tests).</summary>
    internal IReadOnlyCollection<Hash> SendingPaymentHashes => _watchdogs.Keys.ToList();

    /// <summary>Waits until no timer round runs in the background (tests).</summary>
    internal async Task WhenIdleAsync()
    {
        while (!_backgroundTasks.IsEmpty)
            await Task.WhenAll(_backgroundTasks.Keys);
    }

    private ITrampolineLegSender? LegSender => _serviceProvider.GetService<ITrampolineLegSender>();

    private uint CurrentHeight => _blockchainMonitor?.LastProcessedBlockHeight ?? 0;

    /// <summary><c>Node:Routing</c>, read on every use (as <c>HtlcForwardingPolicy</c>).</summary>
    private RoutingOptions Routing => _nodeOptions?.Value.Routing ?? s_defaultRouting;

    #region Startup

    /// <summary>
    /// Resumes the unfinished relays after a restart: a collecting relay gets its <c>mpp_timeout</c> back (from its
    /// creation time), a sending one a watchdog (its leg's outcome comes from the leg sender's own reconciliation, which
    /// the host runs first). Call it once every channel is loaded (after <c>PeerManager.StartAsync</c> and the payment
    /// reconciliation).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TrampolineRelayModel> unfinished;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (TrampolineRelayReads.TryGetRepository(unitOfWork) is not { } relays)
                return;

            unfinished = await relays.ListUnfinishedAsync();
        }

        foreach (var relay in unfinished)
        {
            if (relay.Status == TrampolineRelayStatus.Collecting)
                EnsureMppTimer(relay);
            else
                EnsureWatchdog(relay.PaymentHash, _timeProvider.GetUtcNow() + 2 * _options.EffectiveLegTimeout);
        }

        if (unfinished.Count > 0)
            _logger.LogInformation("Resumed {Count} unfinished trampoline relay(s)", unfinished.Count);
    }

    #endregion

    #region Ingress

    /// <inheritdoc />
    public async Task HandleNewPartAsync(IncomingHtlcLockedIn lockedIn, IncomingOnionTrampolineRelay onion,
                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockedIn);
        ArgumentNullException.ThrowIfNull(onion);

        var channelId = lockedIn.ChannelId;
        var htlc = lockedIn.Htlc;
        _failureKeys[(channelId, htlc.Id)] = TrampolineFailureKeys.From(onion);

        TrampolineLegRequest? leg;
        using (await _paymentHashLocks.AcquireAsync(htlc.PaymentHash, cancellationToken))
            leg = await AcceptPartLockedAsync(channelId, htlc, onion, cancellationToken);

        if (leg is not null)
            await StartLegAsync(leg, cancellationToken);
    }

    /// <summary>A new part, under its payment-hash lock. Returns the leg to start once the set completed.</summary>
    private async Task<TrampolineLegRequest?> AcceptPartLockedAsync(ChannelId channelId, HtlcRecord htlc,
                                                                    IncomingOnionTrampolineRelay onion,
                                                                    CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(channelId, htlc.Id) is null)
            return null;

        var amount = LightningMoney.MilliSatoshis(htlc.AmountMsat);
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (TrampolineRelayReads.TryGetRepository(unitOfWork) is not { } relays)
        {
            await FailPartAsync(channelId, htlc.Id, null, FailureMessage.TemporaryTrampolineFailure(), null,
                                cancellationToken);
            return null;
        }

        // Saved meanwhile (a replay that raced the first handling): the relay decides as for any replay
        if (await relays.GetPartAsync(channelId, htlc.Id) is { } saved)
            return await ResumePartLockedAsync(unitOfWork, channelId, saved, cancellationToken);

        // A blinded trampoline hop (NL-895): the HTLC must bring what the outer onion says it brings (BOLT 4 final
        // node: amount and expiry), since payment_relay derived the next amount and expiry from the outer values; and
        // the next node is the recipient data's next_node_id or the peer of the channel its short_channel_id names
        var (nextNodeId, nextChannel) = ResolveNextHop(onion);
        ushort blindedHopDelta = 0;
        if (onion.Blinded is not null
         && DescribeBlindedPartViolation(htlc, onion, nextNodeId, nextChannel, out blindedHopDelta) is { } violation)
        {
            _logger.LogInformation("Blinded trampoline part {HtlcId} of channel {ChannelId} for {PaymentHash} refused: "
                                 + "{Reason}", htlc.Id, channelId, htlc.PaymentHash, violation);
            await FailPartAsync(channelId, htlc.Id, null,
                                FailureMessage.InvalidOnionBlinding(onion.TrampolineOnionSha256), null,
                                cancellationToken);
            return null;
        }

        var stored = await relays.GetAsync(htlc.PaymentHash);
        if (stored is { Relay.Status: TrampolineRelayStatus.Failed } failedRelay
         && await CanReplaceFailedAsync(unitOfWork, failedRelay.Parts, htlc.PaymentHash))
        {
            // A new attempt of the payer (after our NODE|26, an mpp_timeout, a failed leg): the failed relay is over
            // (every part resolved, nothing outgoing unresolved), so it makes way for a new one
            await relays.RemoveFailedAsync(htlc.PaymentHash);
            await unitOfWork.SaveChangesAsync();
            _failures.TryRemove(htlc.PaymentHash, out _);
            _logger.LogInformation("Trampoline relay {PaymentHash} failed before: a new attempt replaces it",
                                   htlc.PaymentHash);
            stored = null;
        }

        if (stored is { } existing)
        {
            var (relay, _) = existing;
            switch (relay.Status)
            {
                case TrampolineRelayStatus.Fulfilled:
                    // The payment went through already: the payer is owed nothing back for a late part
                    _logger.LogInformation("Late part {HtlcId} of channel {ChannelId} for the fulfilled trampoline "
                                         + "relay {PaymentHash}: fulfilling it", htlc.Id, channelId, htlc.PaymentHash);
                    await FulfillPartAsync(channelId, htlc.Id, relay.Preimage!.Value, cancellationToken);
                    return null;
                case TrampolineRelayStatus.Sending:
                case TrampolineRelayStatus.Failed:
                    _logger.LogInformation("Part {HtlcId} of channel {ChannelId} arrived after the trampoline relay "
                                         + "{PaymentHash} left collecting ({Status}): failing it", htlc.Id, channelId,
                                           htlc.PaymentHash, relay.Status);
                    await FailPartAsync(channelId, htlc.Id, null, FailureMessage.TemporaryTrampolineFailure(), null,
                                        cancellationToken);
                    return null;
            }

            if (DescribeMismatch(relay, existing.Parts, onion, nextNodeId) is { } mismatch)
            {
                // BOLT 4 MPP: a part that does not belong to the set (other total, secret or instructions)
                _logger.LogInformation("Part {HtlcId} of channel {ChannelId} does not match the trampoline relay "
                                     + "{PaymentHash} ({Mismatch}): failing it", htlc.Id, channelId, htlc.PaymentHash,
                                       mismatch);
                await FailPartAsync(channelId, htlc.Id, null,
                                    FailureMessage.IncorrectOrUnknownPaymentDetails(amount, CurrentHeight), null,
                                    cancellationToken);
                return null;
            }

            await relays.AddPartAsync(CreatePart(channelId, htlc, onion));
            await unitOfWork.SaveChangesAsync();
            KeepBlindedHopDelta(htlc.PaymentHash, onion, blindedHopDelta);
            return await CheckSetLockedAsync(unitOfWork, htlc.PaymentHash, cancellationToken);
        }

        // A new relay
        if (LegSender is null)
        {
            _logger.LogWarning("Trampoline part {HtlcId} of channel {ChannelId} for {PaymentHash} refused: no "
                             + "trampoline leg sender is registered", htlc.Id, channelId, htlc.PaymentHash);
            await FailPartAsync(channelId, htlc.Id, null, FailureMessage.TemporaryTrampolineFailure(), null,
                                cancellationToken);
            return null;
        }

        if (RefusalOfNewRelays() is { } refusal)
        {
            _logger.LogInformation("Trampoline part {HtlcId} of channel {ChannelId} for {PaymentHash} refused: {Reason}",
                                   htlc.Id, channelId, htlc.PaymentHash, refusal);
            await FailPartAsync(channelId, htlc.Id, null, FailureMessage.TemporaryTrampolineFailure(), null,
                                cancellationToken);
            return null;
        }

        if (CreateRelay(htlc, onion, nextNodeId) is not { } created)
        {
            _logger.LogWarning("Trampoline part {HtlcId} of channel {ChannelId} for {PaymentHash} lacks its relay "
                             + "instructions: failing it", htlc.Id, channelId, htlc.PaymentHash);
            await FailPartAsync(channelId, htlc.Id, null, FailureMessage.TemporaryTrampolineFailure(), null,
                                cancellationToken);
            return null;
        }

        await relays.AddAsync(created);
        await relays.AddPartAsync(CreatePart(channelId, htlc, onion));
        await unitOfWork.SaveChangesAsync();
        KeepBlindedHopDelta(htlc.PaymentHash, onion, blindedHopDelta);
        _logger.LogInformation("Trampoline relay {PaymentHash} started: {AmountOut} msat to {NextNode}, {Total} msat "
                             + "expected in", htlc.PaymentHash, created.AmountOut.MilliSatoshi,
                               created.NextNodeId?.ToString() ?? "blinded paths", created.IncomingTotal.MilliSatoshi);
        EnsureMppTimer(created);
        return await CheckSetLockedAsync(unitOfWork, htlc.PaymentHash, cancellationToken);
    }

    /// <summary>
    /// A failed relay may make way for a new attempt with the same payment hash once every part's incoming HTLC is
    /// resolved (on a loaded channel: no record, or a final one) and no outgoing HTLC of it is unresolved or learnt the
    /// preimage.
    /// </summary>
    private async Task<bool> CanReplaceFailedAsync(IUnitOfWork unitOfWork,
                                                   IReadOnlyList<TrampolineRelayPartModel> parts, Hash paymentHash)
    {
        foreach (var part in parts)
        {
            if (!_channelMemoryRepository.TryGetChannel(part.ChannelId, out var channel)
             || channel.Commitments is not { } commitments
             || (commitments.GetHtlc(HtlcDirection.Incoming, part.HtlcId) is { } record
              && !HtlcStateTable.IsFinal(record.State)))
                return false;
        }

        var (unresolved, preimage) =
            await TrampolineRelayReads.GetOutgoingStateAsync(unitOfWork, _channelMemoryRepository, paymentHash);
        return unresolved == 0 && preimage is null;
    }

    /// <summary>Why no new relay may start now (drain, chain halt, no height), or null.</summary>
    private string? RefusalOfNewRelays()
    {
        if (_nodeDrainState is { IsDraining: true })
            return "the node is draining for its shutdown";
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            return "chain processing is halted";
        return CurrentHeight == 0 ? "no block height yet" : null;
    }

    private static TrampolineRelayPartModel CreatePart(ChannelId channelId, HtlcRecord htlc,
                                                       IncomingOnionTrampolineRelay onion) =>
        new(htlc.PaymentHash, channelId, htlc.Id, LightningMoney.MilliSatoshis(htlc.AmountMsat), htlc.CltvExpiry,
            onion.OuterSharedSecret, onion.TrampolineSharedSecret,
            onion.OuterPayload.PaymentData?.PaymentSecret is { } secret ? (byte[])secret : null);

    /// <summary>
    /// The next trampoline node of a part: the onion's (<c>outgoing_node_id</c>, or the blinded recipient data's
    /// <c>next_node_id</c>), or, when the blinded recipient data names a <c>short_channel_id</c>, the peer of that open
    /// channel of ours, resolved as the switch resolves a blinded forward (real scid, aliases, a scid a splice retired;
    /// <see cref="OutgoingChannelResolver"/>), with that channel; a null node when it names nothing we know (NL-895,
    /// D-NL895-1).
    /// </summary>
    private (CompactPubKey? NodeId, ChannelModel? Channel) ResolveNextHop(IncomingOnionTrampolineRelay onion)
    {
        if (onion.NextShortChannelId is not { } shortChannelId)
            return (onion.NextNodeId, null);

        var channel = OutgoingChannelResolver.Resolve(_channelMemoryRepository, _retiredScidMap, shortChannelId);
        return (channel?.RemoteNodeId, channel);
    }

    /// <summary>
    /// Why a blinded trampoline part is refused before it joins a relay (answered with <c>invalid_onion_blinding</c>
    /// as the blinded rules say), or null with the hop's delta to keep: its recipient data names a
    /// <c>short_channel_id</c> that is none of our open channels (D-NL895-1); the HTLC breaks the outer onion's
    /// final-hop rules (BOLT 4: <c>amount_msat</c> at least <c>amt_to_forward</c>, <c>cltv_expiry</c> at least
    /// <c>outgoing_cltv_value</c>) or <c>payment_constraints.max_cltv_expiry</c>; or the <c>payment_relay</c> is below
    /// our policy for the hop (NL-922, D-NL922-1: <see cref="TrampolineRelayPolicy.CheckBlindedHopPrice"/>). The next
    /// amount and expiry were derived from the outer total and expiry with <c>payment_relay</c>; with these rules every
    /// part's expiry is at least the outer one, so the next expiry is at most the lowest incoming expiry minus the
    /// path's <c>cltv_expiry_delta</c> (D-NL895-2).
    /// </summary>
    private string? DescribeBlindedPartViolation(HtlcRecord htlc, IncomingOnionTrampolineRelay onion,
                                                 CompactPubKey? nextNodeId, ChannelModel? nextChannel,
                                                 out ushort keptCltvExpiryDelta)
    {
        keptCltvExpiryDelta = 0;
        if (nextNodeId is null && onion.NextShortChannelId is { } shortChannelId)
            return $"its recipient data names short_channel_id {shortChannelId}, which is none of our open channels";

        if (onion.OuterPayload.AmtToForward is { } amount && htlc.AmountMsat < amount.MilliSatoshi)
            return $"amount_msat {htlc.AmountMsat} is below the outer amt_to_forward {amount.MilliSatoshi}";

        if (onion.OuterPayload.OutgoingCltvValue is { } cltv && htlc.CltvExpiry < cltv)
            return $"cltv_expiry {htlc.CltvExpiry} is below the outer outgoing_cltv_value {cltv}";

        var recipientData = onion.Blinded!.RecipientData;
        if (recipientData.PaymentConstraints is { } constraints && htlc.CltvExpiry > constraints.MaxCltvExpiry)
            return $"cltv_expiry {htlc.CltvExpiry} is above payment_constraints.max_cltv_expiry "
                 + $"{constraints.MaxCltvExpiry}";

        if (recipientData.PaymentRelay is not { } paymentRelay)
            return "its recipient data has no payment_relay";

        // Our policy for the hop: the named channel's (its setchannelpolicy override, else Node:Routing, with the
        // policies a change replaced within the BOLT 7 grace period), or Node:Routing for a next_node_id hop
        ConfiguredChannelPolicy policy;
        IReadOnlyList<ConfiguredChannelPolicy> previous = [];
        if (nextChannel is not null && _channelPolicyProvider is { } provider)
        {
            policy = provider.GetConfiguredPolicy(nextChannel.ChannelId);
            if (!provider.IsLoaded)
                return "the channel routing policies are not loaded";
            previous = provider.GetPreviousPolicies(nextChannel.ChannelId);
        }
        else
        {
            policy = ConfiguredChannelPolicy.From(Routing, null);
        }

        var price = TrampolineRelayPolicy.CheckBlindedHopPrice(paymentRelay, policy, previous);
        if (!price.IsAccepted)
            return price.Reason;

        keptCltvExpiryDelta = price.KeptCltvExpiryDelta;
        return null;
    }

    /// <summary>
    /// Remembers the delta a joined blinded part keeps until its relay leaves collecting (the largest of its parts': a
    /// policy may change between them).
    /// </summary>
    private void KeepBlindedHopDelta(Hash paymentHash, IncomingOnionTrampolineRelay onion, ushort keptCltvExpiryDelta)
    {
        if (onion.Blinded is not null)
            _blindedHopDeltas.AddOrUpdate(paymentHash, keptCltvExpiryDelta,
                                          (_, kept) => Math.Max(kept, keptCltvExpiryDelta));
    }

    /// <summary>
    /// The delta a blinded relay keeps at its completion: its parts' (<see cref="KeepBlindedHopDelta"/>), or, when
    /// none joined since this process started (a restart between the last part's save and <c>Sending</c>), the most
    /// that <c>Node:Routing</c> or any open channel of ours to the stored next node asks: the relay keeps the node,
    /// not the channel its recipient data named, and the scid is never resolved again.
    /// </summary>
    private ushort KeptBlindedDeltaOf(TrampolineRelayModel relay)
    {
        if (_blindedHopDeltas.TryGetValue(relay.PaymentHash, out var kept))
            return kept;

        var delta = Routing.CltvExpiryDelta;
        if (relay.NextNodeId is not { } nextNodeId || _channelPolicyProvider is not { } provider)
            return delta;

        foreach (var channel in _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open
                                                                         && c.RemoteNodeId == nextNodeId))
            delta = Math.Max(delta, provider.GetConfiguredPolicy(channel.ChannelId).CltvExpiryDelta);

        return delta;
    }

    /// <summary>The relay row of a first part, or null when the onion lacks an instruction.</summary>
    private TrampolineRelayModel? CreateRelay(HtlcRecord htlc, IncomingOnionTrampolineRelay onion,
                                              CompactPubKey? nextNodeId)
    {
        if (onion.AmountToForward is not { IsZero: false } amountOut || onion.OutgoingCltvValue is not { } cltvOut
         || onion.IncomingTotal is not { } total)
            return null;

        var blindedPaths = onion.RecipientBlindedPaths is { Count: > 0 } paths
                               ? PaymentBlindedPathCodec.EncodeList(paths)
                               : null;
        if (nextNodeId is null && blindedPaths is null)
            return null;

        // Inside a blinded trampoline route the next node gets the next path key; the encrypted recipient data we
        // decrypted goes with it (the model keeps both or neither)
        var nextPathKey = onion.NextPathKey is { } pathKey ? (byte[])pathKey : null;
        var recipientData = nextPathKey is null
                                ? null
                                : onion.InnerPayload.EncryptedRecipientData?.ToArray() ?? [];
        return new TrampolineRelayModel(htlc.PaymentHash, nextNodeId, amountOut, cltvOut, total,
                                        _timeProvider.GetUtcNow(), NextPacketOf(onion, nextNodeId), recipientData,
                                        nextPathKey,
                                        onion.RecipientFeatures?.Features.ToArray(), blindedPaths);
    }

    private static byte[]? NextPacketOf(IncomingOnionTrampolineRelay onion, CompactPubKey? nextNodeId) =>
        nextNodeId is null ? null : onion.NextTrampolinePacket?.ToBytes();

    /// <summary>What makes a later part differ from the relay its payment hash started, or null when it belongs.
    /// </summary>
    private static string? DescribeMismatch(TrampolineRelayModel relay, IReadOnlyList<TrampolineRelayPartModel> parts,
                                            IncomingOnionTrampolineRelay onion, CompactPubKey? nextNodeId)
    {
        if (onion.IncomingTotal is not { } total || total != relay.IncomingTotal)
            return $"total_msat {onion.IncomingTotal?.MilliSatoshi} instead of {relay.IncomingTotal.MilliSatoshi}";

        var secret = onion.OuterPayload.PaymentData?.PaymentSecret is { } s ? (byte[])s : null;
        if (parts.Count > 0 && !BytesEqual(parts[0].OuterPaymentSecret, secret))
            return "another payment_secret";

        if (onion.AmountToForward is not { } amountOut || amountOut != relay.AmountOut
         || onion.OutgoingCltvValue != relay.CltvExpiryOut)
            return "other amount or expiry to forward";

        if (nextNodeId != relay.NextNodeId)
            return "another next node";

        if (!BytesEqual(NextPacketOf(onion, nextNodeId), relay.NextTrampolinePacket))
            return "another trampoline onion";

        var blindedPaths = onion.RecipientBlindedPaths is { Count: > 0 } paths
                               ? PaymentBlindedPathCodec.EncodeList(paths)
                               : null;
        if (!BytesEqual(blindedPaths, relay.RecipientBlindedPaths)
         || !BytesEqual(onion.RecipientFeatures?.Features.ToArray(), relay.RecipientFeatures))
            return "other recipient instructions";

        return null;
    }

    private static bool BytesEqual(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    #endregion

    #region Set completion

    /// <summary>
    /// Under the payment-hash lock: once the parts still waiting reach the relay's total, checks the policy and the
    /// in-flight limit, marks the relay <c>Sending</c> and returns the leg to start (or fails every part).
    /// </summary>
    private async Task<TrampolineLegRequest?> CheckSetLockedAsync(IUnitOfWork unitOfWork, Hash paymentHash,
                                                                  CancellationToken cancellationToken)
    {
        var relays = unitOfWork.TrampolineRelayDbRepository;
        if (await relays.GetAsync(paymentHash) is not { Relay.Status: TrampolineRelayStatus.Collecting } stored)
            return null;

        var (relay, parts) = stored;

        var waiting = parts.Where(p => GetAwaitingIncomingHtlc(p.ChannelId, p.HtlcId) is not null
                                    && !IsOnchain(p.ChannelId)).ToList();
        var sumIn = waiting.Aggregate(LightningMoney.Zero, (sum, p) => sum + p.Amount);
        if (waiting.Count == 0 || sumIn < relay.IncomingTotal)
        {
            EnsureMppTimer(relay);
            _logger.LogInformation("Trampoline relay {PaymentHash}: {Parts} part(s), {Sum} of {Total} msat received",
                                   paymentHash, waiting.Count, sumIn.MilliSatoshi, relay.IncomingTotal.MilliSatoshi);
            return null;
        }

        var blindedHopDelta = relay.NextPathKey is not null ? KeptBlindedDeltaOf(relay) : (ushort)0;
        StopMppTimer(paymentHash);
        var height = CurrentHeight;
        var minCltvIn = waiting.Min(p => p.CltvExpiry);
        var blindedPaths = relay.RecipientBlindedPaths is { } pathBytes
                               ? PaymentBlindedPathCodec.DecodeList(pathBytes)
                               : null;
        var set = new TrampolineRelaySet(sumIn, minCltvIn, waiting.Max(p => p.CltvExpiry), relay.AmountOut,
                                         relay.CltvExpiryOut, height)
        {
            RecipientPathCltvExpiryDelta = blindedPaths is { Count: > 0 }
                                               ? blindedPaths.Min(p => p.PayInfo.CltvExpiryDelta)
                                               : (ushort)0
        };
        FailureMessage? failure = null;
        string? reason = null;
        TrampolineRelayDecision? decision = null;
        if (LegSender is null)
        {
            (failure, reason) = (FailureMessage.TemporaryTrampolineFailure(), "no trampoline leg sender is registered");
        }
        else if (RefusalOfNewRelays() is { } refusal)
        {
            (failure, reason) = (FailureMessage.TemporaryTrampolineFailure(), refusal);
        }
        else
        {
            // A blinded trampoline hop's price is the recipient's payment_relay, already applied to the amount and
            // expiry out and checked against our policy for the hop as each part joined; our Node:Trampoline policy
            // and NODE|26 are for the hops a payer prices (NL-895, D-NL895-2, NL-922)
            decision = relay.NextPathKey is not null
                           ? TrampolineRelayPolicy.EvaluateBlinded(_options, Routing, set, blindedHopDelta)
                           : TrampolineRelayPolicy.Evaluate(_options, Routing, set);
            if (!decision.IsAccepted)
                (failure, reason) = (decision.Failure, decision.Reason);
            else if (await CountSendingAsync(relays) >= _options.MaxRelaysInFlight)
                (failure, reason) = (FailureMessage.TemporaryTrampolineFailure(),
                                     $"{_options.MaxRelaysInFlight} trampoline relays in flight already");
        }

        if (failure is not null)
        {
            relay.MarkFailed((ushort)failure.Code, reason, _timeProvider.GetUtcNow());
            await relays.UpdateAsync(relay);
            await unitOfWork.SaveChangesAsync();
            _failures[paymentHash] = new PendingFailure(failure, null);
            _logger.LogInformation("Trampoline relay {PaymentHash} refused: {Reason}", paymentHash, reason);
            await FailPartsAsync(parts, failure, null, cancellationToken);
            return null;
        }

        // The leg must be over well before the lowest incoming expiry leaves our margin (blocks are ten minutes on
        // average; half of it covers fast blocks)
        var blocksLeft = minCltvIn - _options.MinCltvMarginBlocks - height;
        var timeout = TimeSpan.FromMinutes(Math.Min(_options.EffectiveLegTimeout.TotalMinutes, 5.0 * blocksLeft));
        var deadline = _timeProvider.GetUtcNow() + timeout;

        relay.MarkSending();
        await relays.UpdateAsync(relay);
        await unitOfWork.SaveChangesAsync();
        EnsureWatchdog(paymentHash, deadline + _options.EffectiveLegTimeout);

        var features = relay.RecipientFeatures is { } featureBytes
                           ? FeatureSet.DeserializeFromBytes(featureBytes)
                           : null;
        var allowMpp = relay.NextTrampolinePacket is not null
                    || features?.IsFeatureSet(Feature.BasicMpp) == true;
        _logger.LogInformation("Trampoline relay {PaymentHash}: {Sum} msat in for {AmountOut} msat out, paying "
                             + "{NextNode} with at most {MaxFee} msat of fees", paymentHash, sumIn.MilliSatoshi,
                               relay.AmountOut.MilliSatoshi, relay.NextNodeId?.ToString() ?? "blinded paths",
                               decision!.MaxFee!.MilliSatoshi);
        return new TrampolineLegRequest(paymentHash, relay.AmountOut, relay.CltvExpiryOut,
                                        decision.MaxFirstHopCltvExpiry!.Value, decision.MaxFee, relay.NextNodeId,
                                        relay.NextTrampolinePacket,
                                        relay.NextPathKey is { } key ? new CompactPubKey(key) : null, blindedPaths,
                                        features, allowMpp, deadline);
    }

    private static async Task<int> CountSendingAsync(ITrampolineRelayDbRepository relays) =>
        (await relays.ListUnfinishedAsync()).Count(r => r.Status == TrampolineRelayStatus.Sending);

    /// <summary>Starts the leg, outside every lock. A throwing sender leaves the relay to its watchdog.</summary>
    private async Task StartLegAsync(TrampolineLegRequest leg, CancellationToken cancellationToken)
    {
        try
        {
            await LegSender!.StartAsync(leg, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Some HTLCs of the leg may be out: the watchdog fails the relay only once none is unresolved
            _logger.LogError(e, "The outgoing leg of trampoline relay {PaymentHash} could not start", leg.PaymentHash);
        }
    }

    #endregion

    #region Outgoing events (ITrampolineHtlcHandler)

    /// <inheritdoc />
    public async Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash,
                                           CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fulfilled);
        try
        {
            // The leg sender records its payment (and reports the leg's success with what it cost)
            if (LegSender is { } legSender)
                await legSender.HandleOutgoingFulfilledAsync(fulfilled, cancellationToken);
        }
        finally
        {
            // Whatever the leg sender did: once an outgoing HTLC learnt the preimage, every incoming part is owed it
            if (TrampolineRelayReads.Hashes(fulfilled.PaymentPreimage, paymentHash))
                await SucceedAsync(paymentHash, fulfilled.PaymentPreimage, null, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash,
                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (LegSender is not { } legSender)
            throw new InvalidOperationException(
                $"No trampoline leg sender for the failed outgoing HTLC {failed.HtlcId} of {paymentHash}");

        await legSender.HandleOutgoingFailedAsync(failed, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HandleSettledAsync(OutgoingHtlcSettled settled, Hash paymentHash,
                                               CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash) is { Relay.IsCompleted: true };
    }

    /// <inheritdoc />
    public async Task HandleIncomingPartLockedInAsync(IncomingHtlcLockedIn lockedIn, TrampolineRelayPartModel part,
                                                      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockedIn);
        ArgumentNullException.ThrowIfNull(part);

        TrampolineLegRequest? leg;
        using (await _paymentHashLocks.AcquireAsync(part.PaymentHash, cancellationToken))
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            leg = await ResumePartLockedAsync(unitOfWork, lockedIn.ChannelId, part, cancellationToken);
        }

        if (leg is not null)
            await StartLegAsync(leg, cancellationToken);
    }

    /// <summary>A saved part locked in again, under the payment-hash lock: resolved from the relay's status.</summary>
    private async Task<TrampolineLegRequest?> ResumePartLockedAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                    TrampolineRelayPartModel part,
                                                                    CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(channelId, part.HtlcId) is null
         || await TrampolineRelayReads.GetAsync(unitOfWork, part.PaymentHash) is not { } stored)
            return null;

        var relay = stored.Relay;

        switch (relay.Status)
        {
            case TrampolineRelayStatus.Fulfilled:
                if (!IsOnchain(channelId))
                    await FulfillPartAsync(channelId, part.HtlcId, relay.Preimage!.Value, cancellationToken);
                return null;

            case TrampolineRelayStatus.Failed:
                var (failure, packet) = FailureOf(relay);
                await FailPartAsync(channelId, part.HtlcId, part, failure, packet, cancellationToken);
                ForgetFailureIfResolved(stored.Parts);
                return null;

            case TrampolineRelayStatus.Sending:
                EnsureWatchdog(relay.PaymentHash, _timeProvider.GetUtcNow() + 2 * _options.EffectiveLegTimeout);
                return null;

            default:
                // Collecting: a crash may have come between the last part's save and the completion
                return await CheckSetLockedAsync(unitOfWork, relay.PaymentHash, cancellationToken);
        }
    }

    #endregion

    #region Leg outcome (ITrampolineLegObserver)

    /// <inheritdoc />
    public Task OnLegSucceededAsync(Hash paymentHash, Secret preimage, LightningMoney totalSent,
                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(totalSent);
        if (!TrampolineRelayReads.Hashes(preimage, paymentHash))
            throw new ArgumentException("The preimage does not match the payment hash.", nameof(preimage));

        return SucceedAsync(paymentHash, preimage, totalSent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task OnLegFailedAsync(Hash paymentHash, TrampolineLegFailure failure,
                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var ourFailure = failure.Kind == TrampolineLegFailureKind.UnknownNextNode
                             ? FailureMessage.UnknownNextTrampoline()
                             : FailureMessage.TemporaryTrampolineFailure();
        var pending = failure is
        {
            Kind: TrampolineLegFailureKind.DownstreamTrampolineError,
            DownstreamPacketToRewrap: { Length: > 0 } packet
        }
                          ? new PendingFailure(null, packet)
                          : new PendingFailure(ourFailure, null);

        using (await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken))
            await FailRelayLockedAsync(paymentHash, pending, failure.Reason, cancellationToken);
    }

    /// <summary>
    /// Fulfills the relay: one save with <c>Fulfilled</c>, its accounting event and the preimage on the incoming records
    /// of the first part's channel (the others in their own saves after it: the relay's preimage already covers them),
    /// then every part's fulfill. Idempotent: an already fulfilled relay only fulfills the parts still waiting.
    /// </summary>
    /// <param name="totalSent">What the leg's HTLCs carried; null when unknown (read from the leg's payment).</param>
    private async Task SucceedAsync(Hash paymentHash, Secret preimage, LightningMoney? totalSent,
                                    CancellationToken cancellationToken)
    {
        using var hashLock = await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken);
        await SucceedLockedAsync(paymentHash, preimage, totalSent, cancellationToken);
    }

    private async Task SucceedLockedAsync(Hash paymentHash, Secret preimage, LightningMoney? totalSent,
                                          CancellationToken cancellationToken)
    {
        IReadOnlyList<TrampolineRelayPartModel> parts;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash) is not { } stored)
            {
                _logger.LogWarning("The leg of {PaymentHash} succeeded but no trampoline relay is stored for it",
                                   paymentHash);
                return;
            }

            var (relay, storedParts) = stored;
            parts = storedParts;
            if (relay.Status == TrampolineRelayStatus.Failed)
            {
                // Never expected: the relay fails only once no outgoing HTLC is unresolved
                _logger.LogCritical("The leg of trampoline relay {PaymentHash} learnt the preimage after the relay "
                                  + "failed its incoming parts: the amount sent is lost", paymentHash);
                return;
            }

            if (relay.Status != TrampolineRelayStatus.Fulfilled)
            {
                var sumIn = parts.Aggregate(LightningMoney.Zero, (sum, p) => sum + p.Amount);
                var sent = totalSent ?? await ReadTotalSentAsync(unitOfWork, paymentHash) ?? relay.AmountOut;
                if (sent > sumIn)
                    _logger.LogWarning("Trampoline relay {PaymentHash} sent {Sent} msat for {SumIn} msat received",
                                       paymentHash, sent.MilliSatoshi, sumIn.MilliSatoshi);
                var fee = sent > sumIn ? LightningMoney.Zero : sumIn - sent;
                if (relay.Status == TrampolineRelayStatus.Collecting)
                    relay.MarkSending();
                relay.MarkFulfilled(preimage, fee, _timeProvider.GetUtcNow());
                await SaveFulfilledAsync(unitOfWork, relay, parts, preimage, cancellationToken);
                StopMppTimer(paymentHash);
                StopWatchdog(paymentHash);
                _failures.TryRemove(paymentHash, out _);
                _logger.LogInformation("Trampoline relay {PaymentHash} fulfilled: earned {Fee} msat", paymentHash,
                                       fee.MilliSatoshi);
            }
        }

        // The other channels' parts carry the preimage too before their fulfills (the relay's preimage already
        // protects them: the deadline monitor and the resolvers read it)
        foreach (var group in parts.GroupBy(p => p.ChannelId).Skip(1))
            await MarkPreimageAsync(group.Key, group.Select(p => p.HtlcId).ToList(), preimage, null,
                                    cancellationToken);

        foreach (var part in parts)
        {
            if (IsOnchain(part.ChannelId))
                continue; // the BOLT 5 resolver claims it with the preimage
            await FulfillPartAsync(part.ChannelId, part.HtlcId, preimage, cancellationToken);
        }
    }

    /// <summary>What the leg's payment cost (amount and fees), once it succeeded; null when unknown.</summary>
    private static async Task<LightningMoney?> ReadTotalSentAsync(IUnitOfWork unitOfWork, Hash paymentHash)
    {
        try
        {
            return await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(paymentHash) is
            { IsTrampolineRelay: true } payment
                       ? payment.TotalAmount
                       : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The save that marks <paramref name="relay"/> fulfilled, with its accounting event and, under the first part's
    /// channel lock, the preimage on that channel's incoming records.
    /// </summary>
    private async Task SaveFulfilledAsync(IUnitOfWork unitOfWork, TrampolineRelayModel relay,
                                          IReadOnlyList<TrampolineRelayPartModel> parts, Secret preimage,
                                          CancellationToken cancellationToken)
    {
        async Task Stage(IUnitOfWork work, ChannelModel? incoming)
        {
            await work.TrampolineRelayDbRepository.UpdateAsync(relay);
            await PaymentAccountingEvents.StageTrampolineRelaySettledAsync(work, relay, incoming,
                                                                           CurrentHeight, _logger, cancellationToken);
        }

        if (parts.Count == 0)
        {
            await Stage(unitOfWork, null);
            await unitOfWork.SaveChangesAsync();
            return;
        }

        var first = parts[0].ChannelId;
        var marked = await MarkPreimageAsync(first, parts.Where(p => p.ChannelId == first).Select(p => p.HtlcId)
                                                         .ToList(), preimage, Stage, cancellationToken);
        if (!marked)
        {
            // The first part's channel is not loaded (or carries none of them any more): the relay alone
            _channelMemoryRepository.TryGetChannel(first, out var channel);
            await Stage(unitOfWork, channel);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Under <paramref name="channelId"/>'s lock: stores <paramref name="preimage"/> on its waiting incoming records
    /// <paramref name="htlcIds"/> (<c>KnownPreimage</c>, NL-322) with <paramref name="stage"/>'s writes in the same
    /// save. False (nothing written) when the channel is not loaded.
    /// </summary>
    private async Task<bool> MarkPreimageAsync(ChannelId channelId, IReadOnlyList<ulong> htlcIds, Secret preimage,
                                               Func<IUnitOfWork, ChannelModel?, Task>? stage,
                                               CancellationToken cancellationToken)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
         || channel.Commitments is not { } commitments)
            return false;

        var updated = htlcIds.Select(id => commitments.GetHtlc(HtlcDirection.Incoming, id))
                             .Where(r => r is { State: HtlcState.RcvdAddAckRevocation } && r.KnownPreimage != preimage)
                             .Select(r => r! with { KnownPreimage = preimage })
                             .ToList();
        if (updated.Count == 0 && stage is null)
            return true;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var next = commitments;
        if (updated.Count > 0)
        {
            var htlcs = commitments.Htlcs;
            foreach (var record in updated)
                htlcs = htlcs.SetItem(record.Key, record);
            next = ChannelCommitments.Restore(commitments.ChannelId, commitments.Params, commitments.LocalBalanceMsat,
                                              commitments.RemoteBalanceMsat, htlcs.Values, commitments.FeeUpdates,
                                              commitments.LocalNextHtlcId, commitments.RemoteNextHtlcId,
                                              commitments.LocalCommit, commitments.RemoteCommit,
                                              commitments.RemoteNextCommit,
                                              commitments.RemoteNextPerCommitmentPoint);
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(next, new ChannelTransition(updated, [], [], false,
                                                                     false, false, false));
        }

        if (stage is not null)
            await stage(unitOfWork, channel);

        await unitOfWork.SaveChangesAsync();
        if (updated.Count > 0)
            channel.UpdateCommitments(next);
        return true;
    }

    /// <summary>
    /// Fails the relay (under its payment-hash lock) once no outgoing HTLC of it is unresolved: <c>Failed</c> in a save,
    /// then every waiting part. An outgoing HTLC that learnt the preimage fulfills the relay instead; one still
    /// unresolved leaves it to the watchdog (with <paramref name="failure"/> kept for it).
    /// </summary>
    private async Task FailRelayLockedAsync(Hash paymentHash, PendingFailure failure, string reason,
                                            CancellationToken cancellationToken)
    {
        IReadOnlyList<TrampolineRelayPartModel> parts;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash) is not { } stored)
                return;

            var (relay, storedParts) = stored;
            parts = storedParts;
            switch (relay.Status)
            {
                case TrampolineRelayStatus.Fulfilled:
                    _logger.LogWarning("The leg of the fulfilled trampoline relay {PaymentHash} reported a failure "
                                     + "({Reason}): ignored", paymentHash, reason);
                    return;
                case TrampolineRelayStatus.Failed:
                    break;
                default:
                    var (unresolved, preimage) =
                        await TrampolineRelayReads.GetOutgoingStateAsync(unitOfWork, _channelMemoryRepository,
                                                                         paymentHash);
                    if (preimage is { } known)
                    {
                        _logger.LogWarning("The leg of trampoline relay {PaymentHash} reported a failure, but an "
                                         + "outgoing HTLC learnt the preimage: fulfilling", paymentHash);
                        await SucceedLockedAsync(paymentHash, known, null, cancellationToken);
                        return;
                    }

                    if (unresolved > 0)
                    {
                        _failures[paymentHash] = failure;
                        _logger.LogWarning("The leg of trampoline relay {PaymentHash} reported a failure ({Reason}) "
                                         + "while {Count} outgoing HTLC(s) are unresolved: waiting", paymentHash,
                                           reason, unresolved);
                        EnsureWatchdog(paymentHash, _timeProvider.GetUtcNow() + _options.EffectiveLegTimeout);
                        return;
                    }

                    relay.MarkFailed(failure.Failure is { } own ? (ushort)own.Code : null, reason,
                                     _timeProvider.GetUtcNow());
                    await unitOfWork.TrampolineRelayDbRepository.UpdateAsync(relay);
                    await unitOfWork.SaveChangesAsync();
                    StopMppTimer(paymentHash);
                    StopWatchdog(paymentHash);
                    _logger.LogInformation("Trampoline relay {PaymentHash} failed: {Reason}", paymentHash, reason);
                    break;
            }
        }

        _failures[paymentHash] = failure;
        await FailPartsAsync(parts, failure.Failure, failure.DownstreamPacket, cancellationToken);
    }

    /// <summary>The failure to send for a part of a failed relay: the one kept in memory, else rebuilt from the
    /// stored code.</summary>
    private (FailureMessage? Failure, byte[]? Packet) FailureOf(TrampolineRelayModel relay)
    {
        if (_failures.TryGetValue(relay.PaymentHash, out var kept))
            return (kept.Failure, kept.DownstreamPacket);

        var failure = relay.FailureCode is { } code
                          ? (FailureCode)code switch
                          {
                              FailureCode.MppTimeout => FailureMessage.MppTimeout(),
                              FailureCode.UnknownNextTrampoline => FailureMessage.UnknownNextTrampoline(),
                              FailureCode.TrampolineFeeOrExpiryInsufficient =>
                                  TrampolineRelayPolicy.InsufficientFailure(_options),
                              _ => FailureMessage.TemporaryTrampolineFailure()
                          }
                          : FailureMessage.TemporaryTrampolineFailure();
        return (failure, null);
    }

    #endregion

    #region Timers

    private void EnsureMppTimer(TrampolineRelayModel relay)
    {
        if (_disposed || _mppTimers.ContainsKey(relay.PaymentHash))
            return;

        var due = relay.CreatedAt + _mppTimeout - _timeProvider.GetUtcNow();
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        var hash = relay.PaymentHash;
        var timer = _timeProvider.CreateTimer(_ => RunInBackground(() => ExpireCollectingAsync(hash)), null, due,
                                              Timeout.InfiniteTimeSpan);
        if (!_mppTimers.TryAdd(hash, timer))
            timer.Dispose();
    }

    private void StopMppTimer(Hash paymentHash)
    {
        _blindedHopDeltas.TryRemove(paymentHash, out _);
        if (_mppTimers.TryRemove(paymentHash, out var timer))
            timer.Dispose();
    }

    private void EnsureWatchdog(Hash paymentHash, DateTimeOffset at)
    {
        if (_disposed || _watchdogs.ContainsKey(paymentHash))
            return;

        var due = at - _timeProvider.GetUtcNow();
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        var timer = _timeProvider.CreateTimer(_ => RunInBackground(() => WatchdogAsync(paymentHash)), null, due,
                                              Timeout.InfiniteTimeSpan);
        if (!_watchdogs.TryAdd(paymentHash, timer))
            timer.Dispose();
    }

    private void StopWatchdog(Hash paymentHash)
    {
        if (_watchdogs.TryRemove(paymentHash, out var timer))
            timer.Dispose();
    }

    /// <summary>The <c>mpp_timeout</c> of a relay still collecting: fail it and every waiting part.</summary>
    private async Task ExpireCollectingAsync(Hash paymentHash)
    {
        var cancellationToken = _disposeCts.Token;
        using var hashLock = await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken);
        _mppTimers.TryRemove(paymentHash, out var timer);
        timer?.Dispose();
        _blindedHopDeltas.TryRemove(paymentHash, out _);

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash) is not
            { Relay.Status: TrampolineRelayStatus.Collecting } stored)
            return;

        var (relay, parts) = stored;

        var failure = FailureMessage.MppTimeout();
        relay.MarkFailed((ushort)failure.Code, $"incomplete after {_mppTimeout}", _timeProvider.GetUtcNow());
        await unitOfWork.TrampolineRelayDbRepository.UpdateAsync(relay);
        await unitOfWork.SaveChangesAsync();
        _failures[paymentHash] = new PendingFailure(failure, null);
        _logger.LogInformation("Trampoline relay {PaymentHash} incomplete after {Timeout} ({Parts} part(s)): failing "
                             + "it with mpp_timeout", paymentHash, _mppTimeout, parts.Count);
        await FailPartsAsync(parts, failure, null, cancellationToken);
    }

    /// <summary>
    /// A sending relay past its leg's deadline: fulfilled when an outgoing HTLC learnt the preimage, failed when none
    /// is unresolved any more, else checked again one leg timeout later (never failed upstream while an outgoing HTLC
    /// is unresolved).
    /// </summary>
    private async Task WatchdogAsync(Hash paymentHash)
    {
        var cancellationToken = _disposeCts.Token;
        using var hashLock = await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken);
        _watchdogs.TryRemove(paymentHash, out var timer);
        timer?.Dispose();

        TrampolineRelayModel? relay;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            relay = (await TrampolineRelayReads.GetAsync(unitOfWork, paymentHash))?.Relay;
        }

        if (relay is not { Status: TrampolineRelayStatus.Sending })
            return;

        var failure = _failures.GetValueOrDefault(paymentHash)
                   ?? new PendingFailure(FailureMessage.TemporaryTrampolineFailure(), null);
        _logger.LogWarning("The leg of trampoline relay {PaymentHash} has no outcome past its deadline: checking its "
                         + "outgoing HTLCs", paymentHash);
        await FailRelayLockedAsync(paymentHash, failure, "the outgoing leg passed its deadline", cancellationToken);
    }

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
                // Shutting down: the relay is persisted, the next start resumes it
            }
            catch (OperationCanceledException) when (_disposed)
            {
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Trampoline relay timer round failed");
            }
        });
        _backgroundTasks[task] = 0;
        _ = task.ContinueWith(t => _backgroundTasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    #endregion

    #region Resolving parts

    private async Task FailPartsAsync(IEnumerable<TrampolineRelayPartModel> parts, FailureMessage? failure,
                                      byte[]? downstreamPacket, CancellationToken cancellationToken)
    {
        var list = parts.ToList();
        foreach (var part in list)
            await FailPartAsync(part.ChannelId, part.HtlcId, part, failure, downstreamPacket, cancellationToken);
        ForgetFailureIfResolved(list);
    }

    /// <summary>Drops the failure kept for a failed relay once none of its parts waits for it any more.</summary>
    private void ForgetFailureIfResolved(IReadOnlyList<TrampolineRelayPartModel> parts)
    {
        if (parts.Count > 0 && parts.All(p => GetAwaitingIncomingHtlc(p.ChannelId, p.HtlcId) is null))
            _failures.TryRemove(parts[0].PaymentHash, out _);
    }

    /// <summary>
    /// Fails one incoming part (when it still waits and its channel can carry the update): our own
    /// <paramref name="failure"/>, or <paramref name="downstreamPacket"/> re-wrapped, both under the trampoline then
    /// the outer secret, or the blinded answer. A refusal is logged: the part's replay fails it again.
    /// </summary>
    private async Task FailPartAsync(ChannelId channelId, ulong htlcId, TrampolineRelayPartModel? part,
                                     FailureMessage? failure, byte[]? downstreamPacket,
                                     CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(channelId, htlcId) is not { } incoming)
            return;

        if (IsOnchain(channelId))
        {
            _logger.LogInformation("Trampoline part {HtlcId} of channel {ChannelId} is left to time out on chain",
                                   htlcId, channelId);
            return;
        }

        try
        {
            var keys = await GetFailureKeysAsync(channelId, incoming, part);
            if (keys is null)
            {
                _logger.LogError("No secret to fail trampoline part {HtlcId} of channel {ChannelId}", htlcId,
                                 channelId);
                return;
            }

            if (keys.BlindedMalformedSha256 is { } sha256)
            {
                await _channelOperations.FailMalformedHtlcAsync(channelId, htlcId, FailureCode.InvalidOnionBlinding,
                                                                new Hash(sha256), cancellationToken);
                LogFailed(channelId, htlcId, "update_fail_malformed_htlc invalid_onion_blinding");
                return;
            }

            if (keys.IntroductionSha256 is { } introductionSha256)
            {
                // The introduction node of a blinded trampoline route replaces every error by its own
                failure = FailureMessage.InvalidOnionBlinding(introductionSha256);
                downstreamPacket = null;
                await DelayBlindedErrorAsync(cancellationToken);
            }

            failure ??= downstreamPacket is null ? FailureMessage.TemporaryTrampolineFailure() : null;
            var attributed = _attributionDataService is not null && _advertisesAttribution
                                                                 && incoming is { PathKey: null };
            if (attributed)
            {
                var holdTime = await _channelOperations.GetHoldTimeAsync(channelId, htlcId, cancellationToken);
                var packet = failure is not null
                                 ? TrampolineErrorPackets.CreateAttributed(_failureOnionService,
                                                                           _attributionDataService!,
                                                                           keys.TrampolineSharedSecret,
                                                                           keys.OuterSharedSecret, failure, holdTime)
                                 : TrampolineErrorPackets.WrapAttributed(_failureOnionService,
                                                                         _attributionDataService!,
                                                                         keys.TrampolineSharedSecret,
                                                                         keys.OuterSharedSecret, downstreamPacket!,
                                                                         holdTime);
                await _channelOperations.FailHtlcAsync(channelId, htlcId, packet, cancellationToken);
            }
            else
            {
                await _channelOperations.FailHtlcAsync(channelId, htlcId,
                                                       CreateReason(keys, failure, downstreamPacket),
                                                       cancellationToken);
            }

            LogFailed(channelId, htlcId, failure?.Code.ToString() ?? "downstream trampoline error");
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            _logger.LogWarning("Could not fail trampoline part {HtlcId} of channel {ChannelId} yet: {Reason}", htlcId,
                               channelId, e.Message);
        }
    }

    private byte[] CreateReason(TrampolineFailureKeys keys, FailureMessage? failure, byte[]? downstreamPacket)
    {
        if (_trampolineFailureOnionService is { } trampolineFailures)
            return failure is not null
                       ? TrampolineErrorPackets.Create(trampolineFailures, keys.TrampolineSharedSecret,
                                                       keys.OuterSharedSecret, failure)
                       : trampolineFailures.WrapTrampolineErrorPacket(keys.TrampolineSharedSecret,
                                                                      keys.OuterSharedSecret, downstreamPacket!);

        var trampolineLayer = failure is not null
                                  ? _failureOnionService.CreateErrorPacket(keys.TrampolineSharedSecret, failure)
                                  : _failureOnionService.WrapErrorPacket(keys.TrampolineSharedSecret,
                                                                         downstreamPacket!);
        return _failureOnionService.WrapErrorPacket(keys.OuterSharedSecret, trampolineLayer);
    }

    /// <summary>
    /// The keys of a part's failure: kept from its onion, else from its onion peeled again (no replay check), else
    /// the stored secrets (<see cref="TrampolineHtlcFailures.FromStoredPart"/>).
    /// </summary>
    private async Task<TrampolineFailureKeys?> GetFailureKeysAsync(ChannelId channelId, HtlcRecord incoming,
                                                                   TrampolineRelayPartModel? part)
    {
        if (_failureKeys.TryGetValue((channelId, incoming.Id), out var kept))
            return kept;

        IncomingOnionResult? result = null;
        if (_onionProcessor is not null)
        {
            try
            {
                result = await _onionProcessor.ProcessAsync(incoming.OnionRoutingPacket, incoming.PaymentHash,
                                                            null, incoming.PathKey,
                                                            LightningMoney.MilliSatoshis(incoming.AmountMsat),
                                                            incoming.CltvExpiry);
                if (result is IncomingOnionTrampolineRelay relay)
                    return _failureKeys[(channelId, incoming.Id)] = TrampolineFailureKeys.From(relay);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not peel the onion of trampoline part {HtlcId} of channel {ChannelId} "
                                    + "again", incoming.Id, channelId);
            }
        }

        // The stored secrets, outside any blinded route unless the onion peeled again says malformed
        // invalid_onion_blinding (past a blinded introduction node with route blinding off since, NL-921), the same
        // rule as the failures sent outside the engine
        return part is null
                   ? null
                   : TrampolineHtlcFailures.FromStoredPart(part.OuterSharedSecret, part.TrampolineSharedSecret, result);
    }

    /// <summary>Fulfills one incoming part; a refusal is logged (its replay fulfills it from the relay).</summary>
    private async Task FulfillPartAsync(ChannelId channelId, ulong htlcId, Secret preimage,
                                        CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(channelId, htlcId) is not { } incoming || IsOnchain(channelId))
            return;

        try
        {
            if (_attributionDataService is not null && _advertisesAttribution && incoming is { PathKey: null }
             && await GetFailureKeysAsync(channelId, incoming, null) is { } keys)
            {
                var holdTime = await _channelOperations.GetHoldTimeAsync(channelId, htlcId, cancellationToken);
                await _channelOperations.FulfillHtlcAsync(channelId, htlcId, preimage,
                                                          _attributionDataService.CreateFulfillment(
                                                              keys.OuterSharedSecret, holdTime),
                                                          null, cancellationToken);
            }
            else
            {
                await _channelOperations.FulfillHtlcAsync(channelId, htlcId, preimage, cancellationToken);
            }

            _failureKeys.TryRemove((channelId, htlcId), out _);
            _logger.LogInformation("Fulfilled trampoline part {HtlcId} of {AmountMsat} msat on channel {ChannelId}",
                                   htlcId, incoming.AmountMsat, channelId);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            _logger.LogWarning("Could not fulfill trampoline part {HtlcId} of channel {ChannelId} yet: {Reason}",
                               htlcId, channelId, e.Message);
        }
    }

    private Task DelayBlindedErrorAsync(CancellationToken cancellationToken)
    {
        if (_blindedErrorMaxDelay <= TimeSpan.Zero)
            return Task.CompletedTask;

        var maxMs = (int)Math.Min(_blindedErrorMaxDelay.TotalMilliseconds, int.MaxValue - 1);
        var delayMs = System.Security.Cryptography.RandomNumberGenerator.GetInt32(maxMs + 1);
        return delayMs == 0 ? Task.CompletedTask : Task.Delay(delayMs, cancellationToken);
    }

    private void LogFailed(ChannelId channelId, ulong htlcId, string what)
    {
        _failureKeys.TryRemove((channelId, htlcId), out _);
        _logger.LogInformation("Failed back trampoline part {HtlcId} of channel {ChannelId}: {What}", htlcId,
                               channelId, what);
    }

    #endregion

    #region Helpers

    private bool IsOnchain(ChannelId channelId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.State is ChannelState.Failed or ChannelState.OnchainResolving;

    private HtlcRecord? GetAwaitingIncomingHtlc(ChannelId channelId, ulong htlcId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.Commitments?.GetHtlc(HtlcDirection.Incoming, htlcId) is { State: HtlcState.RcvdAddAckRevocation } htlc
            ? htlc
            : null;

    #endregion

    #region Disposal

    /// <summary>Stops every timer; the relays are persisted and the next start resumes them.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        foreach (var timer in _mppTimers.Values.Concat(_watchdogs.Values))
            timer.Dispose();
        _mppTimers.Clear();
        _watchdogs.Clear();
    }

    /// <summary><see cref="Dispose"/>, then waits (at most 5 s) for the timer rounds still running.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Trampoline relay timer rounds still running after the engine was disposed");
        }
    }

    #endregion

    /// <summary>A relay's failure: our own message, or the downstream packet to re-wrap.</summary>
    private sealed record PendingFailure(FailureMessage? Failure, byte[]? DownstreamPacket);
}