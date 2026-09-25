using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Channels.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using FinalHop;
using Gossip.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Onion;

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
///   <item>Otherwise the onion is processed by <see cref="IncomingOnionProcessor"/> (the replay cache is skipped when
///   the HTLC's shared secret is already stored: we processed it before a restart or a reestablish), its shared
///   secret is stored (<see cref="IChannelOperations.RecordOnionSecretAsync"/>), then it is failed
///   (<c>update_fail_malformed_htlc</c>, or <c>update_fail_htlc</c> with an error onion), paid (final hop) or
///   forwarded.</item>
///   <item>Final hop (NL-253): under a per-payment-hash lock, the invoice is re-read, checked by
///   <see cref="FinalHopProcessor"/>, marked <c>Accepted</c> and saved before <c>update_fulfill_htlc</c> is persisted;
///   the invoice is <c>Settled</c> right after the fulfill is persisted (the engine raises no event when an incoming
///   removal becomes irrevocable). A second HTLC for the same hash then sees <c>Accepted</c> and fails with
///   <c>incorrect_or_unknown_payment_details</c>. An <c>Accepted</c> invoice that no incoming HTLC fulfilled (a crash
///   or a refused fulfill between the two saves) is claimed again by the next HTLC that passes the checks.</item>
///   <item>Forward: the onion's <c>short_channel_id</c> is resolved to an open channel (its local aliases or the
///   peer's alias; the real scid only when <c>option_scid_alias</c> is off), checked by <see cref="IForwardingPolicy"/>
///   (a failure is returned with our signed <c>channel_update</c> for the UPDATE codes when its scid is the onion's,
///   else with <c>len = 0</c>), recorded as a <c>Pending</c> <see cref="ForwardCircuitModel"/>, offered with
///   <c>HtlcOrigin.Forwarded</c> (persisted with the add, NL-250), then marked <c>Offered</c>. A refused offer fails the
///   circuit and returns <c>temporary_channel_failure</c>.</item>
/// </list>
/// <para>Outgoing events, routed by the outgoing HTLC's stored <see cref="HtlcOrigin"/>:</para>
/// <list type="bullet">
///   <item><see cref="OutgoingHtlcFulfilled"/>: the upstream HTLC is fulfilled at once (B2-FWD-05), then the circuit
///   is marked <c>Fulfilled</c>.</item>
///   <item><see cref="OutgoingHtlcFailed"/> (raised only once the removal is irrevocable, B2-FWD-02): the downstream
///   error onion is wrapped with the incoming shared secret, or an <c>update_fail_malformed_htlc</c> is converted into
///   our own error onion (BOLT 2), and the upstream HTLC is failed; then the circuit is marked <c>Failed</c>.</item>
///   <item><see cref="OutgoingHtlcSettled"/>: the archived HTLC row is pruned (NL-243) only once nothing needs its
///   replay any more: for a forward, the upstream HTLC has its removal and the circuit is resolved (an upstream
///   channel that is not loaded yet keeps the row); for our own payment, every
///   <see cref="ILocalPaymentHtlcHandler"/> handled its resolution.</item>
///   <item><c>HtlcOrigin.Local</c> resolutions go to the registered <see cref="ILocalPaymentHtlcHandler"/>s.</item>
/// </list>
/// <para>Idempotent: events are re-derived on startup and after a reestablish. The work on one incoming HTLC (its
/// lock-in and the resolutions of the outgoing HTLC that forwards it) is serialized by a per-incoming-HTLC lock, so a
/// fulfill is never sent twice and never lost. Locks are always taken in the order incoming HTLC, payment hash,
/// channel (the channel lock only inside <see cref="IChannelOperations"/> or the prune), and never two channel locks.
/// A refused channel operation (<see cref="CommitmentRefusedException"/>, e.g. the peer is away) is logged: nothing was
/// persisted and the event comes back with the next replay.</para>
/// </remarks>
public sealed class HtlcSwitch : IHtlcSwitch
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly IFailureOnionService _failureOnionService;
    private readonly FinalHopProcessor _finalHopProcessor;
    private readonly IForwardingPolicy _forwardingPolicy;
    private readonly IReadOnlyList<ILocalPaymentHtlcHandler> _localPaymentHandlers;
    private readonly ILogger<HtlcSwitch> _logger;
    private readonly IncomingOnionProcessor _onionProcessor;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    private readonly KeyedAsyncLock<(ChannelId, ulong)> _incomingLocks = new();
    private readonly KeyedAsyncLock<Hash> _paymentHashLocks = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _unhandledLocalResolutions = new();

    public HtlcSwitch(IChannelLockProvider channelLockProvider, IChannelMemoryRepository channelMemoryRepository,
                      IChannelOperations channelOperations, IFailureOnionService failureOnionService,
                      FinalHopProcessor finalHopProcessor, IForwardingPolicy forwardingPolicy,
                      ILogger<HtlcSwitch> logger, IncomingOnionProcessor onionProcessor,
                      IPeerLivenessProbe peerLivenessProbe, IServiceScopeFactory serviceScopeFactory,
                      TimeProvider? timeProvider = null, IBlockchainMonitor? blockchainMonitor = null,
                      IChannelUpdateService? channelUpdateService = null,
                      IEnumerable<ILocalPaymentHtlcHandler>? localPaymentHandlers = null)
    {
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
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            }
        }
        catch (CommitmentRefusedException e)
        {
            // Nothing was persisted or sent by the refused operation: the event is replayed later
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
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, htlcId);
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

        // A stored secret means we processed this onion before (restart, reestablish): its HMAC may be in the cache
        var result = await _onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash, htlc.PathKey,
                                                        checkReplay: storedSecret is null);
        switch (result)
        {
            case IncomingOnionMalformed malformed:
                await _channelOperations.FailMalformedHtlcAsync(channelId, htlcId, malformed.FailureCode,
                                                                new Hash(malformed.Sha256OfOnion.ToArray()),
                                                                cancellationToken);
                LogFailedBack(channelId, htlc, $"malformed onion ({malformed.FailureCode})");
                return;

            case IncomingOnionFailed failed:
                await RecordSecretAsync(channelId, htlcId, failed.SharedSecret, storedSecret, cancellationToken);
                await FailBackAsync(channelId, htlc, failed.SharedSecret, failed.Failure, cancellationToken);
                return;

            case IncomingOnionFinal final:
                await RecordSecretAsync(channelId, htlcId, final.SharedSecret, storedSecret, cancellationToken);
                await ReceiveAsync(channelId, htlc, final, cancellationToken);
                return;

            case IncomingOnionForward forward:
                await RecordSecretAsync(channelId, htlcId, forward.SharedSecret, storedSecret, cancellationToken);
                await ForwardAsync(channelId, htlc, forward, cancellationToken);
                return;
        }
    }

    private async Task RecordSecretAsync(ChannelId channelId, ulong htlcId, Secret sharedSecret, Secret? storedSecret,
                                         CancellationToken cancellationToken)
    {
        // Kept with the HTLC before it is failed or forwarded, so a failure can be wrapped after a restart
        if (storedSecret is null || !storedSecret.Value.Equals(sharedSecret))
            await _channelOperations.RecordOnionSecretAsync(channelId, htlcId, sharedSecret, cancellationToken);
    }

    /// <summary>
    /// Final hop (M4-T3, NL-253): check-and-mark the invoice under its payment hash lock, then fulfill.
    /// </summary>
    private async Task ReceiveAsync(ChannelId channelId, HtlcRecord htlc, IncomingOnionFinal final,
                                    CancellationToken cancellationToken)
    {
        // The payer reads the height to tell an expiry problem from an unknown hash: never report a height of 0
        var height = CurrentHeight;
        if (height == 0)
        {
            await FailBackAsync(channelId, htlc, final.SharedSecret, FailureMessage.TemporaryNodeFailure(),
                                cancellationToken);
            return;
        }

        var amount = LightningMoney.MilliSatoshis(htlc.AmountMsat);
        using var paymentHashLock = await _paymentHashLocks.AcquireAsync(htlc.PaymentHash, cancellationToken);

        Secret preimage;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(htlc.PaymentHash);

            // Accepted but not paid by any HTLC: the fulfill that followed the accept was never persisted
            var reclaim = invoice is { Status: InvoiceStatus.Accepted }
                       && !IsFulfilledByAnyIncomingHtlc(htlc.PaymentHash);
            var decision = _finalHopProcessor.Evaluate(reclaim ? AsOpen(invoice!) : invoice, htlc.PaymentHash,
                                                       amount, htlc.CltvExpiry, final.Payload, height);
            if (!decision.IsAccepted)
            {
                await FailBackAsync(channelId, htlc, final.SharedSecret, decision.Failure!, cancellationToken);
                return;
            }

            preimage = decision.Preimage!.Value;
            if (!reclaim)
            {
                invoice!.Accept(amount);
                await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
                await unitOfWork.SaveChangesAsync();
            }
        }

        await _channelOperations.FulfillHtlcAsync(channelId, htlc.Id, preimage, cancellationToken);
        _logger.LogInformation("Fulfilled incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId} for "
                             + "our invoice {PaymentHash}", htlc.Id, htlc.AmountMsat, channelId, htlc.PaymentHash);

        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(htlc.PaymentHash);
            if (invoice is { Status: InvoiceStatus.Accepted })
            {
                invoice.Settle(_timeProvider.GetUtcNow());
                await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
                await unitOfWork.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Forward (M4-T4): resolve, check the policy, persist the circuit, offer, record the offer.
    /// </summary>
    private async Task ForwardAsync(ChannelId incomingChannelId, HtlcRecord htlc, IncomingOnionForward forward,
                                    CancellationToken cancellationToken)
    {
        var height = CurrentHeight;
        if (height == 0)
        {
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret, FailureMessage.TemporaryNodeFailure(),
                                cancellationToken);
            return;
        }

        var requestedScid = forward.OutgoingShortChannelId;
        var outgoing = ResolveOutgoingChannel(requestedScid);
        var outgoingInfo = outgoing is null ? null : await DescribeAsync(outgoing, cancellationToken);
        var incomingAmount = LightningMoney.MilliSatoshis(htlc.AmountMsat);
        var decision = _forwardingPolicy.Evaluate(new ForwardingRequest(incomingAmount, htlc.CltvExpiry,
                                                                        forward.AmountToForward,
                                                                        forward.OutgoingCltvValue, height,
                                                                        outgoingInfo));
        if (!decision.IsForward)
        {
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                ToFailureMessage(decision, outgoing, requestedScid), cancellationToken);
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
            outgoingHtlcId = await _channelOperations.OfferHtlcAsync(
                outgoingChannel.ChannelId, forward.AmountToForward, htlc.PaymentHash, forward.OutgoingCltvValue,
                forward.NextPacket, null, HtlcOrigin.Forwarded(incomingChannelId, htlc.Id), cancellationToken);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            // Nothing was persisted for the offer: the forward failed before it started
            _logger.LogInformation("Forward of HTLC {HtlcId} of channel {ChannelId} to {ShortChannelId} refused: {Reason}",
                                   htlc.Id, incomingChannelId, requestedScid, e.Message);
            await UpdateCircuitAsync(incomingChannelId, htlc.Id,
                                     c => c.Status == ForwardCircuitStatus.Pending,
                                     c => c.MarkFailed(_timeProvider.GetUtcNow()));
            await FailBackAsync(incomingChannelId, htlc, forward.SharedSecret,
                                FailureMessage.TemporaryChannelFailure(UpdateFor(outgoingChannel, requestedScid)),
                                cancellationToken);
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
                    IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)> outgoing;
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        outgoing = await unitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(
                                       HtlcOrigin.Forwarded(incomingChannelId, incomingHtlcId));
                    }

                    if (outgoing.Count > 0)
                    {
                        // The add persisted but the circuit's Offered update did not: record it; the outgoing HTLC's
                        // events resolve the forward
                        var (outgoingChannelId, outgoingKey) = outgoing[0];
                        await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                                 c => c.Status == ForwardCircuitStatus.Pending,
                                                 c => c.AddOutgoingHtlc(outgoingChannelId, outgoingKey.Id));
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

            case ForwardCircuitStatus.Fulfilled when circuit is { OutgoingChannelId: { } channelId, OutgoingHtlcId: { } id }
                                                   && FindKnownPreimage(channelId, id) is { } preimage:
                await FulfillUpstreamAsync(incomingChannelId, incomingHtlcId, preimage, cancellationToken);
                return;

            default:
                // Offered, or resolved through an outgoing HTLC: its replayed events resolve the upstream HTLC (its
                // record is not pruned before)
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("HTLC {HtlcId} of channel {ChannelId} waits for its {Status} forward", incomingHtlcId,
                                     incomingChannelId, circuit.Status);
                return;
        }
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
                                     FailureMessage failure, CancellationToken cancellationToken)
    {
        var reason = _failureOnionService.CreateErrorPacket(sharedSecret, failure);
        await _channelOperations.FailHtlcAsync(channelId, htlc.Id, reason, cancellationToken);
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

                    // The preimage is final knowledge: fulfill upstream at once (B2-FWD-05)
                    try
                    {
                        await FulfillUpstreamAsync(incomingChannelId, incomingHtlcId, fulfilled.PaymentPreimage,
                                                   cancellationToken);
                    }
                    catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
                    {
                        // Replayed later: the outgoing record (and its preimage) is kept until the upstream is resolved
                        _logger.LogWarning("Could not fulfill upstream HTLC {HtlcId} of channel {ChannelId} yet: {Reason}",
                                           incomingHtlcId, incomingChannelId, e.Message);
                    }

                    await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                             c => c.Status is ForwardCircuitStatus.Pending or ForwardCircuitStatus.Offered,
                                             c => c.MarkFulfilled(fulfilled.ChannelId, fulfilled.HtlcId,
                                                                  _timeProvider.GetUtcNow()));
                    return;
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                await NotifyLocalAsync(fulfilled.ChannelId, fulfilled.HtlcId,
                                       h => h.HandleFulfilledAsync(fulfilled, paymentHash, cancellationToken));
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

                    if (GetAwaitingIncomingHtlc(incomingChannelId, incomingHtlcId) is { } incoming)
                    {
                        var sharedSecret = await GetIncomingSharedSecretAsync(incomingChannelId, incoming);
                        var reason = sharedSecret is { } secret
                                         ? ReturnPacket(secret, failed.Removal)
                                         : null;
                        try
                        {
                            if (reason is not null)
                            {
                                await _channelOperations.FailHtlcAsync(incomingChannelId, incomingHtlcId, reason,
                                                                       cancellationToken);
                                LogFailedBack(incomingChannelId, incoming,
                                              $"downstream {failed.Removal.Kind} on channel {failed.ChannelId}");
                            }
                            else
                            {
                                // Without the incoming shared secret nothing can be encrypted for the origin
                                await _channelOperations.FailMalformedHtlcAsync(
                                    incomingChannelId, incomingHtlcId, FailureCode.InvalidOnionHmac,
                                    Sha256Of(incoming.OnionRoutingPacket), cancellationToken);
                            }
                        }
                        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
                        {
                            // Replayed later: the outgoing record is kept until the upstream is resolved
                            _logger.LogWarning("Could not fail upstream HTLC {HtlcId} of channel {ChannelId} yet: {Reason}",
                                               incomingHtlcId, incomingChannelId, e.Message);
                        }
                    }

                    await UpdateCircuitAsync(incomingChannelId, incomingHtlcId,
                                             c => c.Status is ForwardCircuitStatus.Pending or ForwardCircuitStatus.Offered,
                                             c => c.MarkFailed(failed.ChannelId, failed.HtlcId,
                                                               _timeProvider.GetUtcNow()));
                    return;
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                await NotifyLocalAsync(failed.ChannelId, failed.HtlcId,
                                       h => h.HandleFailedAsync(failed, paymentHash, cancellationToken));
                return;

            default:
                _logger.LogWarning("HTLC {HtlcId} we offered on channel {ChannelId} failed ({Kind}) but has no origin",
                                   failed.HtlcId, failed.ChannelId, failed.Removal.Kind);
                return;
        }
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
            }

            using var channelLock = await _channelLockProvider.AcquireAsync(settled.ChannelId, cancellationToken);
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelStateDbRepository.PruneSettledHtlcsAsync(
                settled.ChannelId, [new HtlcKey(HtlcDirection.Outgoing, settled.HtlcId)]);
            await unitOfWork.SaveChangesAsync();
        }
        finally
        {
            incomingLock?.Dispose();
        }
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

    private async Task FulfillUpstreamAsync(ChannelId incomingChannelId, ulong incomingHtlcId, Secret preimage,
                                            CancellationToken cancellationToken)
    {
        if (GetAwaitingIncomingHtlc(incomingChannelId, incomingHtlcId) is null)
            return;

        await _channelOperations.FulfillHtlcAsync(incomingChannelId, incomingHtlcId, preimage, cancellationToken);
        _logger.LogInformation("Fulfilled upstream HTLC {HtlcId} of channel {ChannelId}", incomingHtlcId,
                               incomingChannelId);
    }

    /// <summary>
    /// The return packet for the upstream <c>update_fail_htlc</c>: the downstream error onion wrapped with our
    /// <c>ammag</c> key, or (for <c>update_fail_malformed_htlc</c>) our own error onion with the code and
    /// <c>sha256_of_onion</c> (BOLT 2; we act as the erring node).
    /// </summary>
    private byte[] ReturnPacket(Secret incomingSharedSecret, HtlcRemoval removal)
    {
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
                                                        incoming.PathKey, checkReplay: false);
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

    #endregion

    #region Helpers

    private uint CurrentHeight => _blockchainMonitor?.LastProcessedBlockHeight ?? 0;

    /// <summary>The incoming HTLC when it is locked in and no removal was sent for it yet.</summary>
    private HtlcRecord? GetAwaitingIncomingHtlc(ChannelId channelId, ulong htlcId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.Commitments?.GetHtlc(HtlcDirection.Incoming, htlcId) is { State: HtlcState.RcvdAddAckRevocation } htlc
            ? htlc
            : null;

    private bool IsFulfilledByAnyIncomingHtlc(Hash paymentHash) =>
        _channelMemoryRepository.FindChannels(c => c.Commitments is not null)
                                .Any(c => c.Commitments!.Htlcs.Values.Any(h => h.Direction == HtlcDirection.Incoming
                                                                          && h.PaymentHash == paymentHash
                                                                          && h.Removal is { IsFulfill: true }));

    private Secret? FindKnownPreimage(ChannelId channelId, ulong htlcId) =>
        _channelMemoryRepository.TryGetChannel(channelId, out var channel)
     && channel.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlcId) is { } htlc
            ? htlc.KnownPreimage ?? htlc.Removal?.PaymentPreimage
            : null;

    private static InvoiceModel AsOpen(InvoiceModel invoice) =>
        new(invoice.PaymentHash, invoice.Preimage, invoice.PaymentSecret, invoice.Amount, invoice.Description,
            invoice.Bolt11, invoice.CreatedAt, invoice.ExpirySeconds, invoice.MinFinalCltvExpiry);

    /// <summary>
    /// The open channel the onion's <c>short_channel_id</c> names: one of our aliases or the peer's alias, or the real
    /// scid when <c>option_scid_alias</c> is not used (BOLT 2 forbids routing into such a channel by its real scid).
    /// </summary>
    private ChannelModel? ResolveOutgoingChannel(ShortChannelId shortChannelId) =>
        _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open
                                                && (c.LocalAliases?.Contains(shortChannelId) == true
                                                 || c.RemoteAlias == shortChannelId
                                                 || (c.ChannelParams.UseScidAlias == FeatureSupport.No
                                                  && c.ShortChannelId != default
                                                  && c.ShortChannelId == shortChannelId)))
                                .FirstOrDefault();

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
            availableMsat = local > pendingOutgoing + reserve ? local - pendingOutgoing - reserve : 0;
        }

        return new OutgoingChannelInfo(channel.ChannelId, usable, channel.ChannelParams.Remote.HtlcMinimumAmount,
                                       LightningMoney.MilliSatoshis(availableMsat));
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
    /// <c>short_channel_id</c> is the one the onion used (BOLT 4); else empty (<c>len = 0</c>).
    /// </summary>
    private byte[] UpdateFor(ChannelModel? outgoing, ShortChannelId requestedScid)
    {
        if (outgoing is null || _channelUpdateService is null
                             || !_channelUpdateService.TryGetLocalChannelUpdate(outgoing.ChannelId, out var update)
                             || update is null || update.Payload.ShortChannelId != requestedScid)
            return [];

        return FailureChannelUpdateFactory.Encode(update);
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
        await unitOfWork.SaveChangesAsync();
    }

    private static Hash Sha256Of(ReadOnlyMemory<byte> bytes) =>
        new(System.Security.Cryptography.SHA256.HashData(bytes.Span));

    private void LogFailedBack(ChannelId channelId, HtlcRecord htlc, string reason)
    {
        _logger.LogInformation("Failed back incoming HTLC {HtlcId} of {AmountMsat} msat on channel {ChannelId}: {Reason}",
                               htlc.Id, htlc.AmountMsat, channelId, reason);
    }

    #endregion
}