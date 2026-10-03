using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Send;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Interpreters;
using Domain.Protocol.Onion.Models;
using Routing;
using Trampoline;

/// <summary>
/// Trampoline routing in <see cref="PaymentService"/> (NL-875, BOLTs PR 836): the outgoing leg of a trampoline relay
/// (<see cref="ITrampolineLegSender"/>) and the rounds of a payment sent through a trampoline node.
/// </summary>
/// <remarks>
/// <para><b>Relay legs.</b> A leg is a session like any payment's (planner, MPP split, retries, per-part rows) with
/// these differences: its target is the next trampoline node (or the recipient's blinded paths), its final
/// <c>outgoing_cltv_value</c> is the absolute expiry the relay asks for, its first hop's expiry is capped
/// (<see cref="TrampolineLegRequest.MaxFirstHopCltvExpiry"/>), its HTLCs carry <c>HtlcOrigin.Trampoline</c>, its row is
/// marked <c>IsTrampolineRelay</c> (no accounting event), and its end is reported once to the
/// <see cref="ITrampolineLegObserver"/> registered in DI, after the save that records it and outside the payment hash's
/// lock (on the thread pool). The last outer payload of a leg to a trampoline node carries the peeled trampoline onion
/// (TLV 20), the next path key when given (TLV 12) and a fresh random outer <c>payment_secret</c> with the leg's total.
/// A failed part of such a leg is unwrapped with its outer secrets
/// (<see cref="ITrampolineFailureOnionService.UnwrapDownstreamErrorPacket"/>): an outer hop's error before the next
/// trampoline node feeds mission control and the retries as any failure; an error the next trampoline node encrypted
/// for the origin ends the leg with <see cref="TrampolineLegFailureKind.DownstreamTrampolineError"/> and the unwrapped
/// packet, and an error the next trampoline node sent on its outer layer ends it with
/// <see cref="TrampolineLegFailureKind.RouteFailure"/> (<see cref="TrampolineLegFailure.ErringNodeIsNextTrampoline"/>
/// set); neither is retried. After a restart, an outcome without a session is matched through the stored rows and
/// reported again; a replayed outcome of a leg already completed is reported again too (the observer is
/// idempotent).</para>
/// <para><b>Payer.</b> A payment through a trampoline node routes the outer onion to that node with the attempt's
/// random outer secret and total (the recipient's amount plus the trampoline's fee) and the same trampoline onion in
/// every part; the trampoline onion is built at the attempt's first round, sized from its planned routes, and its
/// hops' shared secrets are saved in <c>PaymentTrampolineHops</c> before the first offer. Failures are decrypted with
/// both routes' secrets (<see cref="ITrampolineFailureOnionService.DecryptTrampolineErrorPacket"/>): an outer-layer
/// error follows the normal retry rules; the trampoline node's <c>trampoline_fee_or_expiry_insufficient</c> caches its
/// policy and starts a new attempt at it when the fee limit allows, its <c>temporary_trampoline_failure</c> one new
/// attempt at twice the fee; any other trampoline-layer error ends the payment. The outer layer's
/// <c>attribution_data</c> (the trampoline layer has none, BOLTs PR 836) is verified over the outer route as for any
/// payment (NL-898): the verified hops' hold times are recorded, and when no hop of either route authenticated the
/// error the outer hop whose attribution HMAC failed is blamed.</para>
/// </remarks>
public sealed partial class PaymentService
{
    /// <summary>The most attempts a payment starts after <c>trampoline_fee_or_expiry_insufficient</c>.</summary>
    internal const int MaxTrampolinePolicyRetries = 3;

    private ITrampolineLegObserver? _legObserver;

    /// <summary>The trampoline nodes' policies learnt from <c>trampoline_fee_or_expiry_insufficient</c> (memory only,
    /// next to mission control; decision D-TR6).</summary>
    private readonly ConcurrentDictionary<CompactPubKey, TrampolinePolicy> _trampolinePolicies = new();

    /// <summary>
    /// Override of the observer resolved from DI (tests); when unset the first registered
    /// <see cref="ITrampolineLegObserver"/> is resolved on first use.
    /// </summary>
    internal ITrampolineLegObserver? LegObserver
    {
        get => _legObserver ??= ResolveLegObserver();
        set => _legObserver = value;
    }

    #region ITrampolineLegSender

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent per payment hash: a leg in flight (a session of this process, or a stored <c>InFlight</c> relay row)
    /// is left alone; a stored <c>Succeeded</c> leg is reported again; a <c>Failed</c> relay row is replaced by a new
    /// leg (the relay's next attempt). A hash of one of our own payments is refused
    /// (<see cref="TrampolineLegFailureKind.LocalFailure"/>).
    /// </remarks>
    public async Task StartAsync(TrampolineLegRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paymentHash = request.PaymentHash;

        if (ValidateLegRequest(request) is { } invalid)
        {
            ReportLegFailed(null, paymentHash,
                            new TrampolineLegFailure(TrampolineLegFailureKind.LocalFailure, null, null, false, invalid));
            return;
        }

        var height = _blockchainMonitor.LastProcessedBlockHeight;
        if (height == 0)
        {
            ReportLegFailed(null, paymentHash,
                            new TrampolineLegFailure(TrampolineLegFailureKind.LocalFailure, null, null, false,
                                                     "No block has been processed yet."));
            return;
        }

        CompactPubKey target;
        List<BlindedPaymentPath>? blindedPaths = null;
        if (request.NextNodeId is { } nextNodeId)
        {
            if (!IsKnownNextNode(nextNodeId))
            {
                ReportLegFailed(null, paymentHash,
                                new TrampolineLegFailure(TrampolineLegFailureKind.UnknownNextNode, null, null, false,
                                                         $"The next trampoline node {nextNodeId} is neither a channel "
                                                       + "peer nor in our graph."));
                return;
            }

            target = nextNodeId;
        }
        else
        {
            blindedPaths = [];
            foreach (var wire in request.RecipientBlindedPaths!)
            {
                if (wire.TryToBlindedPaymentPath(out var path) && path.Path.Hops.Count > 0)
                    blindedPaths.Add(path);
            }

            if (blindedPaths.Count == 0)
            {
                ReportLegFailed(null, paymentHash,
                                new TrampolineLegFailure(TrampolineLegFailureKind.NoRoute, null, null, false,
                                                         "None of the recipient's blinded paths names its "
                                                       + "introduction node by node id."));
                return;
            }

            target = blindedPaths[0].Path.Hops[^1].BlindedNodeId;
        }

        var sendOptions = _sendOptions.Value;
        var now = _timeProvider.GetUtcNow();
        var outerSecret = new Secret(RandomNumberGenerator.GetBytes(32));
        var maxParts = request.AllowMpp ? Math.Clamp(sendOptions.MaxParts, 1, PaymentSendOptions.MaxPartsLimit) : 1;
        var session = new PaymentSession(new PaymentTarget(target, paymentHash, outerSecret, request.Amount, 0, [],
                                                           SupportsMpp: request.AllowMpp),
                                         null, request.Amount, request.MaxFee, maxParts,
                                         Math.Max(1, sendOptions.MaxAttempts), request.Deadline, now)
        {
            Leg = request,
            BlindedPaths = blindedPaths,
            AbsoluteFinalCltv = request.FinalCltvExpiry,
            MaxFirstHopCltvExpiry = request.MaxFirstHopCltvExpiry,
            // The relay asked for an exact expiry: no shadow offset on top of it
            ShadowCltvOffset = 0
        };

        TrampolineLegFailure? refused = null;
        using (await AcquireHashLockAsync(paymentHash, cancellationToken))
        {
            if (_sessions.TryGetValue(paymentHash, out var running))
            {
                if (running.IsTrampolineRelay)
                    return;

                refused = new TrampolineLegFailure(TrampolineLegFailureKind.LocalFailure, null, null, false,
                                                   "This node is paying the same payment hash itself.");
            }
            else
            {
                var existing = await GetPaymentAsync(paymentHash, CancellationToken.None);
                switch (existing)
                {
                    case { IsTrampolineRelay: true, Status: PaymentStatus.InFlight }:
                        // Its outcomes are reported as they arrive (or by the reconciliation at startup)
                        return;
                    case { IsTrampolineRelay: true, Status: PaymentStatus.Succeeded, Preimage: { } preimage }:
                        ReportLegSucceeded(null, paymentHash, preimage, existing.Amount + existing.Fee);
                        return;
                    case { IsTrampolineRelay: false }:
                        refused = new TrampolineLegFailure(TrampolineLegFailureKind.LocalFailure, null, null, false,
                                                           $"A payment of ours is stored for this payment hash "
                                                         + $"({existing.Status}).");
                        break;
                }
            }

            if (refused is null)
            {
                _sessions[paymentHash] = session;
                try
                {
                    await RunRoundsAsync(session);
                }
                catch (Exception e) when (e is not OperationCanceledException || !session.HasPartsInFlight)
                {
                    _logger.LogError(e, "The outgoing leg of trampoline relay {PaymentHash} failed to start",
                                     paymentHash);
                    session.TerminalReason ??= $"The leg could not be sent: {e.Message}";
                    if (!session.HasPartsInFlight)
                        await FinishLegAfterErrorAsync(session, session.TerminalReason);
                }
            }
        }

        if (refused is not null)
            ReportLegFailed(null, paymentHash, refused);
    }

    /// <inheritdoc />
    public Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken) =>
        HandleOutgoingHtlcFulfilledAsync(fulfilled, cancellationToken);

    /// <inheritdoc />
    public Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken) =>
        HandleOutgoingHtlcFailedAsync(failed, cancellationToken);

    #endregion

    #region Relay leg

    private static string? ValidateLegRequest(TrampolineLegRequest request)
    {
        if (request.Amount is null || request.Amount.IsZero)
            return "The leg's amount must be positive.";
        if (request.MaxFee is null)
            return "The leg needs a fee budget.";
        if (request.NextNodeId is null == request.RecipientBlindedPaths is not { Count: > 0 })
            return "A leg goes either to the next trampoline node or along the recipient's blinded paths.";
        if (request.NextNodeId is not null && request.NextTrampolinePacket is not { Length: > 0 })
            return "A leg to the next trampoline node carries the peeled trampoline onion.";
        if (request.NextTrampolinePacket is { Length: > 0 and < TrampolineOnionConstants.MinPacketLength })
            return "The next trampoline onion is too short to be a packet.";
        if (request.MaxFirstHopCltvExpiry < request.FinalCltvExpiry)
            return $"The final expiry {request.FinalCltvExpiry} is above the first-hop cap "
                 + $"{request.MaxFirstHopCltvExpiry}.";

        return null;
    }

    /// <summary>Stores a leg failed (and reports it) after an unexpected error; never throws.</summary>
    private async Task FinishLegAfterErrorAsync(PaymentSession session, string reason)
    {
        try
        {
            await FinishFailedAsync(session, reason);
        }
        catch (Exception saveError)
        {
            _logger.LogError(saveError, "Could not store the failure of the outgoing leg {PaymentHash}",
                             session.PaymentHash);
            ReportLegFailed(session, session.PaymentHash,
                            new TrampolineLegFailure(TrampolineLegFailureKind.LocalFailure, null, null, false,
                                                     reason));
            CompleteSession(session);
        }
    }

    /// <summary>
    /// Whether we can route to <paramref name="nodeId"/> at all: a channel of ours goes to it, or our graph has it (or
    /// no graph is available, so we cannot tell and let the planner decide).
    /// </summary>
    private bool IsKnownNextNode(CompactPubKey nodeId)
    {
        if (_channelMemoryRepository.FindChannels(c => c.RemoteNodeId == nodeId).Count > 0)
            return true;

        var graph = _graphPathSource?.GetGraph();
        return graph is null || graph.TryGetNodeIndex(nodeId, out _);
    }

    /// <summary>
    /// How a session's relay leg ended when no part's failure decided it: the deadline passed
    /// (<see cref="TrampolineLegFailureKind.Timeout"/>), no HTLC was ever offered (no route, or every offer refused
    /// locally), or the routes failed.
    /// </summary>
    private TrampolineLegFailure BuildLegFailure(PaymentSession session, string reason)
    {
        if (session.LegFailure is { } decided)
            return decided;

        var kind = session.IsPastDeadline(_timeProvider.GetUtcNow())
                       ? TrampolineLegFailureKind.Timeout
                       : !session.EverOffered
                           ? session.LastFailure is null
                                 ? TrampolineLegFailureKind.NoRoute
                                 : TrampolineLegFailureKind.LocalFailure
                           : TrampolineLegFailureKind.RouteFailure;
        return new TrampolineLegFailure(kind, null, session.LastFailureMessage, false, reason);
    }

    /// <summary>
    /// A relay leg's failed part: unwrapped with its outer secrets; an error of the next trampoline node ends the leg
    /// (see the class remarks), an outer hop's goes to the retry policy (null <see cref="TrampolineDecision.Retry"/>).
    /// </summary>
    private TrampolineDecision? DecideLegFailure(PaymentSession session, PaymentPart part, HtlcRemoval removal,
                                                 ITrampolineFailureOnionService service)
    {
        TrampolineDownstreamFailure unwrapped;
        try
        {
            unwrapped = service.UnwrapDownstreamErrorPacket(part.Hops.Select(h => h.SharedSecret).ToList(),
                                                            removal.Reason.Span);
        }
        catch (ArgumentException e)
        {
            _logger.LogWarning(e, "Could not unwrap the error of leg {PaymentHash}", session.PaymentHash);
            return null;
        }

        var last = part.Hops.Count - 1;
        var next = session.Target.PayeeNodeId;
        if (unwrapped.MustRewrap)
        {
            var reason = $"The next trampoline node {next} (or a node behind it) returned an error for the origin";
            session.LegFailure ??= new TrampolineLegFailure(TrampolineLegFailureKind.DownstreamTrampolineError,
                                                            unwrapped.UnwrappedPacket.ToArray(), null, false, reason);
            session.TerminalReason ??= "the error is the origin's to read";
            return new TrampolineDecision(null, last, reason, null, false, "not retried: the origin decides");
        }

        var failure = unwrapped.Failure!;
        var interpretation = FailureInterpreter.Interpret(failure, part.Hops.Count);
        var codeText = failure.Code is { } code ? $"{code} (0x{(ushort)code:X4})" : "an unreadable failure";
        if (failure.ErringHopIndex == last)
        {
            var reason = $"{codeText} from the next trampoline node {next}";
            session.LegFailure ??= new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null,
                                                            failure.Message, true, reason);
            session.TerminalReason ??= "the next trampoline node refused the leg";
            return new TrampolineDecision(failure.Code, last, reason, interpretation, false,
                                          "not retried: the next trampoline node answered");
        }

        return new TrampolineDecision(failure.Code, failure.ErringHopIndex,
                                      $"{codeText} from hop {failure.ErringHopIndex} "
                                    + $"({DescribeHop(part.Hops, failure.ErringHopIndex)})", interpretation, null,
                                      null);
    }

    /// <summary>A relay leg's failure without a session (after a restart): unwrapped as in a session, never retried.
    /// </summary>
    private TrampolineLegFailure DescribeStoredLegFailure(IReadOnlyList<PaymentHop> route, HtlcRemoval removal,
                                                         string? reason)
    {
        var text = reason ?? "The leg failed.";
        if (removal.Kind != HtlcRemovalKind.Fail || route.Count == 0 || _trampolineFailureOnionService is null)
            return new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null, null, false, text);

        try
        {
            var unwrapped = _trampolineFailureOnionService.UnwrapDownstreamErrorPacket(
                route.Select(h => h.SharedSecret).ToList(), removal.Reason.Span);
            if (unwrapped.MustRewrap)
                return new TrampolineLegFailure(TrampolineLegFailureKind.DownstreamTrampolineError,
                                                unwrapped.UnwrappedPacket.ToArray(), null, false, text);

            return new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null, unwrapped.Failure!.Message,
                                            unwrapped.Failure.ErringHopIndex == route.Count - 1, text);
        }
        catch (ArgumentException)
        {
            return new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null, null, false, text);
        }
    }

    /// <summary>Reports a relay leg the startup reconciliation failed (its HTLCs are gone, the outcome unknown).
    /// </summary>
    private void ReportReconciledLeg(PaymentModel payment)
    {
        if (payment is { IsTrampolineRelay: true, Status: PaymentStatus.Failed })
            ReportLegFailed(null, payment.PaymentHash,
                            new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null, null, false,
                                                     payment.FailureReason ?? "The leg's HTLCs are gone."));
    }

    private void ReportLegSucceeded(PaymentSession? session, Hash paymentHash, Secret preimage,
                                    LightningMoney totalSent) =>
        ReportLeg(session, paymentHash, "success",
                  (observer, ct) => observer.OnLegSucceededAsync(paymentHash, preimage, totalSent, ct));

    private void ReportLegFailed(PaymentSession? session, Hash paymentHash, TrampolineLegFailure failure)
    {
        _logger.LogInformation("Outgoing leg of trampoline relay {PaymentHash} failed ({Kind}): {Reason}",
                               paymentHash, failure.Kind, failure.Reason);
        ReportLeg(session, paymentHash, "failure",
                  (observer, ct) => observer.OnLegFailedAsync(paymentHash, failure, ct));
    }

    /// <summary>
    /// Hands a leg's end to the observer on the thread pool, so it never runs under the payment hash's lock (the relay
    /// engine takes its own locks and may start a leg under them); once per session.
    /// </summary>
    private void ReportLeg(PaymentSession? session, Hash paymentHash, string what,
                           Func<ITrampolineLegObserver, CancellationToken, Task> call)
    {
        if (session is not null)
        {
            if (session.LegReported)
                return;
            session.LegReported = true;
        }

        if (LegObserver is not { } observer)
        {
            _logger.LogWarning("No trampoline leg observer is registered; the {What} of leg {PaymentHash} is not "
                             + "reported", what, paymentHash);
            return;
        }

        var report = Task.Run(async () =>
        {
            try
            {
                await call(observer, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The trampoline leg observer failed on the {What} of leg {PaymentHash}", what,
                                 paymentHash);
            }
        });
        _backgroundRounds.TryAdd(report, 0);
        report.ContinueWith(t => _backgroundRounds.TryRemove(t, out _), TaskScheduler.Default);
    }

    private ITrampolineLegObserver? ResolveLegObserver()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            return scope.ServiceProvider.GetService<ITrampolineLegObserver>();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(e, "Could not resolve the trampoline leg observer");
            return null;
        }
    }

    #endregion

    #region Rounds

    /// <summary>
    /// Starts the next trampoline attempt of a payment through a trampoline node when the last one ended (or before
    /// the first): its trampoline route and budget at the current policy, a new random outer secret and the outer
    /// total and expiry. False when the round must not plan now: parts of the ended attempt are still in flight, or the
    /// payment was failed (no budget within the fee limit).
    /// </summary>
    private async Task<bool> PrepareTrampolineAttemptAsync(PaymentSession session, TrampolinePayerState state,
                                                           uint height)
    {
        if (!state.NeedsNewAttempt)
            return true;
        if (session.HasPartsInFlight)
            return false;

        if (!TryBuildTrampolineAttempt(state, state.Policy, height, out var attempt, out var why))
        {
            await FinishFailedAsync(session, why);
            return false;
        }

        var fee = attempt.OuterTotal.MilliSatoshi - session.Amount.MilliSatoshi;
        if (fee > session.MaxFee.MilliSatoshi)
        {
            await FinishFailedAsync(session, $"the trampoline node's fee {fee} msat is above the fee limit of "
                                           + $"{session.MaxFee.MilliSatoshi} msat");
            return false;
        }

        state.Attempt++;
        state.InnerHops = attempt.Hops;
        state.OuterTotal = attempt.OuterTotal;
        state.OuterFinalCltv = attempt.OuterFinalCltv;
        state.Onion = null;
        state.NeedsNewAttempt = false;
        session.AbsoluteFinalCltv = attempt.OuterFinalCltv;
        session.Target = session.Target with
        {
            PaymentSecret = new Secret(RandomNumberGenerator.GetBytes(32)),
            Amount = attempt.OuterTotal
        };
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Payment {PaymentHash}: trampoline attempt {Attempt} through {Trampoline}, fee "
                                 + "{Fee} msat, CLTV delta {Delta}", session.PaymentHash, state.Attempt,
                                   state.TrampolineNode, fee, state.Policy.CltvExpiryDelta);
        return true;
    }

    /// <summary>
    /// The trampoline route of an attempt at <paramref name="policy"/>: the hops (see
    /// <see cref="TrampolineOnionFactory"/>), what the trampoline node must receive in total and the outer expiry at it.
    /// </summary>
    internal static bool TryBuildTrampolineAttempt(TrampolinePayerState state, TrampolinePolicy policy, uint height,
                                                   out TrampolineAttemptPlan attempt, out string why)
    {
        attempt = default;
        var amount = state.Recipient.Amount;
        var trampoline = state.TrampolineNode;
        try
        {
            switch (state.Recipient)
            {
                case Bolt11TrampolineRecipient bolt11:
                    {
                        var finalCltv = checked(height + bolt11.MinFinalCltvExpiryDelta
                                              + HintRouteBuilder.FinalCltvSafetyOffset);
                        TrampolineHop[] hops =
                        [
                            new(trampoline,
                            TrampolineOnionFactory.CreateIntermediatePayload(amount, finalCltv, bolt11.PayeeNodeId),
                            amount, finalCltv),
                        new(bolt11.PayeeNodeId,
                            TrampolineOnionFactory.CreateFinalPayload(amount, finalCltv, bolt11.PaymentSecret, amount,
                                                                      bolt11.PaymentMetadata),
                            amount, finalCltv)
                        ];
                        attempt = new TrampolineAttemptPlan(
                            hops, amount + LightningMoney.MilliSatoshis(policy.FeeMsat(amount.MilliSatoshi)),
                            checked(finalCltv + policy.CltvExpiryDelta));
                        break;
                    }
                case BlindedTrampolineRecipient blinded:
                    {
                        var payInfo = blinded.Path.PayInfo;
                        var finalCltv = checked(height + HintRouteBuilder.FinalCltvSafetyOffset);
                        var introAmount = BlindedRouteComposer.GetIntroductionAmount(payInfo, amount);
                        var introCltv = checked(finalCltv + payInfo.CltvExpiryDelta);
                        var hops = new List<TrampolineHop>
                    {
                        new(trampoline,
                            TrampolineOnionFactory.CreateIntermediatePayload(introAmount, introCltv,
                                                                             blinded.Path.Path.FirstNodeId),
                            introAmount, introCltv)
                    };
                        hops.AddRange(TrampolineOnionFactory.CreateBlindedHops(blinded.Path.Path, amount, finalCltv,
                                                                               amount));
                        attempt = new TrampolineAttemptPlan(
                            hops, introAmount + LightningMoney.MilliSatoshis(policy.FeeMsat(introAmount.MilliSatoshi)),
                            checked(introCltv + policy.CltvExpiryDelta));
                        break;
                    }
                case BlindedPathsTrampolineRecipient paths:
                    {
                        // The trampoline node pays the paths' fee and CLTV delta from its budget: add the dearest path's
                        var finalCltv = checked(height + HintRouteBuilder.FinalCltvSafetyOffset);
                        var pathFee = paths.Paths.Max(p => p.PayInfo.ComputeFeeMsat(amount.MilliSatoshi));
                        var pathDelta = paths.Paths.Max(p => p.PayInfo.CltvExpiryDelta);
                        TrampolineHop[] hops =
                        [
                            new(trampoline,
                            TrampolineOnionFactory.CreateRecipientBlindedPathsPayload(amount, finalCltv, paths.Paths,
                                                                                      paths.Features),
                            amount, finalCltv)
                        ];
                        attempt = new TrampolineAttemptPlan(
                            hops,
                            amount + LightningMoney.MilliSatoshis(checked(policy.FeeMsat(amount.MilliSatoshi) + pathFee)),
                            checked(finalCltv + pathDelta + policy.CltvExpiryDelta));
                        break;
                    }
                default:
                    why = "unknown trampoline recipient";
                    return false;
            }
        }
        catch (OverflowException)
        {
            why = "the trampoline budget overflows";
            return false;
        }

        why = string.Empty;
        return true;
    }

    /// <summary>
    /// The trampoline records of the round's last outer payloads: a relay leg's next trampoline onion, or the payer's
    /// attempt onion (built at the attempt's first round, sized from the round's planned routes, its hops saved). Null
    /// for any other payment, or when the payer's onion cannot be built (<see cref="PaymentSession.TerminalReason"/>
    /// then says why).
    /// </summary>
    private async Task<TrampolineFinalHop?> GetTrampolineFinalHopAsync(PaymentSession session,
                                                                       IReadOnlyList<PlannedPart> planned)
    {
        if (session.Leg is { NextTrampolinePacket: { } packet } leg)
            return new TrampolineFinalHop(packet, leg.NextPathKey);
        if (session.Trampoline is not { } state)
            return null;

        if (state.Onion is null)
        {
            if (_trampolineOnionFactory is null)
            {
                session.TerminalReason ??= "No trampoline onion service is registered.";
                return null;
            }

            var intermediates = 0;
            var finalOther = 0;
            foreach (var part in planned)
            {
                intermediates = Math.Max(intermediates,
                                         await _onionFactory.GetIntermediateFramedLengthAsync(part.Route));
                finalOther = Math.Max(finalOther,
                                      await _onionFactory.GetTrampolineFinalOtherTlvsLengthAsync(part.Route, null));
            }

            var max = _trampolineOnionFactory.GetMaxHopPayloadsLength(intermediates, finalOther);
            if (max <= 0)
            {
                session.TerminalReason ??= "The outer route leaves no room for the trampoline onion.";
                return null;
            }

            try
            {
                state.Onion = await _trampolineOnionFactory.CreateAsync(state.InnerHops!, session.PaymentHash, max);
            }
            catch (ArgumentException e)
            {
                session.TerminalReason ??= $"The trampoline onion does not fit: {e.Message}";
                return null;
            }

            await PersistTrampolineHopsAsync(session, state);
        }

        return new TrampolineFinalHop(state.Onion.ToTlvValue());
    }

    /// <summary>
    /// Saves the attempt's trampoline hops with their shared secrets (<c>PaymentTrampolineHops</c>), so a failure
    /// that arrives after a restart can be decrypted. A failed save is logged: the HTLCs are still sent and their
    /// failures read while the session lives.
    /// </summary>
    private async Task PersistTrampolineHopsAsync(PaymentSession session, TrampolinePayerState state)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var repository = unitOfWork.PaymentTrampolineHopDbRepository;
            var hops = state.InnerHops!;
            var secrets = state.Onion!.SharedSecrets;
            await repository.AddRangeAsync(hops.Select((hop, i) => new PaymentTrampolineHopModel(
                                                           session.PaymentHash, state.Attempt, i, hop.NodeId,
                                                           secrets[i], hop.Amount, hop.CltvExpiry)));
            await unitOfWork.SaveChangesAsync();
        }
        catch (NotSupportedException)
        {
            // A unit of work that stores no trampoline hops (test doubles)
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not store the trampoline hops of attempt {Attempt} of payment {PaymentHash}; its "
                              + "trampoline errors cannot be read after a restart", state.Attempt,
                             session.PaymentHash);
        }
    }

    /// <summary>The reason when a planned route with the trampoline records does not fit the onion; null when all
    /// fit.</summary>
    private async Task<string?> FindOversizedTrampolineRouteAsync(IReadOnlyList<PlannedPart> planned,
                                                                  TrampolineFinalHop finalHop)
    {
        foreach (var part in planned)
        {
            var length = await _onionFactory.GetFramedLengthAsync(part.Route, null, finalHop);
            if (length > OnionConstants.HopPayloadsLength)
                return $"The trampoline onion does not fit the onion over the {part.Route.Hops.Count}-hop route found "
                     + $"({length} of {OnionConstants.HopPayloadsLength} bytes).";
        }

        return null;
    }

    #endregion

    #region Failures

    /// <summary>
    /// What a trampoline session makes of a part's failure, or null for the normal path (not a trampoline session, an
    /// error that is not an onion, no trampoline failure service).
    /// </summary>
    private TrampolineDecision? DecideTrampolineFailure(PaymentSession session, PaymentPart part, HtlcRemoval removal)
    {
        if (removal.Kind != HtlcRemovalKind.Fail || part.Hops.Count == 0
                                                 || _trampolineFailureOnionService is not { } service)
            return null;

        if (session.Leg is { NextTrampolinePacket: not null })
            return DecideLegFailure(session, part, removal, service);
        if (session.Trampoline is { } state && part.TrampolineSecrets is { Count: > 0 } trampolineSecrets)
            return DecidePayerFailure(session, state, part, removal, trampolineSecrets, service);

        return null;
    }

    /// <summary>A failed part of a payment through a trampoline node (see the class remarks).</summary>
    private TrampolineDecision DecidePayerFailure(PaymentSession session, TrampolinePayerState state, PaymentPart part,
                                                  HtlcRemoval removal, IReadOnlyList<Secret> trampolineSecrets,
                                                  ITrampolineFailureOnionService service)
    {
        var outerSecrets = part.Hops.Select(h => h.SharedSecret).ToList();
        var decrypted = service.DecryptTrampolineErrorPacket(outerSecrets, trampolineSecrets, removal.Reason.Span);
        if (decrypted is null)
            return new TrampolineDecision(null, null,
                                          "The HTLC failed with an error onion no hop of either route authenticated",
                                          FailureInterpreter.Interpret(null, part.Hops.Count), null, null);

        var last = part.Hops.Count - 1;
        var isTrampolineLayer = decrypted.Layer == TrampolineFailureLayer.Trampoline;
        var code = decrypted.Code;
        var codeText = code is { } known ? $"{known} (0x{(ushort)known:X4})" : "an unreadable failure";

        // The trampoline node's own trampoline errors, from its trampoline layer or (as some implementations do) its
        // outer one
        var fromTrampolineNode = isTrampolineLayer ? decrypted.ErringHopIndex == 0 : decrypted.ErringHopIndex == last;
        if (fromTrampolineNode && code is FailureCode.TrampolineFeeOrExpiryInsufficient
                                       or FailureCode.TemporaryTrampolineFailure)
        {
            var reason = $"{codeText} from the trampoline node {state.TrampolineNode}";
            if (part.TrampolineAttempt != state.Attempt || state.NeedsNewAttempt)
                return new TrampolineDecision(code, last, reason, null, true,
                                              "the attempt is already being replaced");

            var (retry, note) = code == FailureCode.TrampolineFeeOrExpiryInsufficient
                                    ? DecideTrampolinePolicyRetry(session, state, decrypted.Message)
                                    : DecideTemporaryTrampolineRetry(session, state);
            if (!retry)
                session.TerminalReason ??= note;
            return new TrampolineDecision(code, last, reason, null, retry, note);
        }

        if (!isTrampolineLayer)
        {
            var interpretation = FailureInterpreter.Interpret(decrypted.Failure, part.Hops.Count);
            var index = decrypted.ErringHopIndex;
            var described = index == last
                                ? $"the trampoline node {state.TrampolineNode}"
                                : $"hop {index} ({DescribeHop(part.Hops, index)})";
            return new TrampolineDecision(code, index, $"{codeText} from {described}", interpretation, null, null);
        }

        // Any other error of the trampoline route: from the trampoline node, a node behind it or the payee
        var inner = state.InnerHops ?? [];
        var innerIndex = decrypted.ErringHopIndex;
        var node = innerIndex < inner.Count ? inner[innerIndex].NodeId.ToString() : "an unknown node";
        var isPayee = innerIndex == inner.Count - 1;
        var trampolineReason = $"{codeText} from trampoline hop {innerIndex} ({node}"
                             + (isPayee ? ", the payee)" : ")");
        var stop = isPayee ? "the payee refused the payment" : "the trampoline route failed";
        session.TerminalReason ??= stop;
        return new TrampolineDecision(code, last + innerIndex, trampolineReason, null, false, stop);
    }

    /// <summary>
    /// After <c>trampoline_fee_or_expiry_insufficient</c>: caches the node's policy and starts a new attempt at it when
    /// it differs from what we paid, the fee limit allows it and the retries are not used up.
    /// </summary>
    private (bool Retry, string Note) DecideTrampolinePolicyRetry(PaymentSession session, TrampolinePayerState state,
                                                                  FailureMessage? message)
    {
        if (message is null || !message.TryGetTrampolinePolicy(out var feeBase, out var proportional, out var delta))
            return (false, "the trampoline node's policy could not be read");

        var policy = new TrampolinePolicy(feeBase, proportional, delta);
        _trampolinePolicies[state.TrampolineNode] = policy;
        if (policy == state.Policy)
            return (false, "the trampoline node refused the policy it asks for");
        if (state.PolicyRetries >= MaxTrampolinePolicyRetries)
            return (false, $"{state.PolicyRetries} trampoline policies were tried, the limit");
        if (!FitsFeeLimit(session, state, policy, out var fee))
            return (false, $"the trampoline node asks {fee} msat, above the fee limit of "
                         + $"{session.MaxFee.MilliSatoshi} msat");

        state.Policy = policy;
        state.PolicyRetries++;
        state.NeedsNewAttempt = true;
        return (true, $"retrying at the trampoline node's policy ({feeBase} msat + {proportional} ppm, CLTV delta "
                    + $"{delta})");
    }

    /// <summary>After <c>temporary_trampoline_failure</c>: one new attempt at twice the fee.</summary>
    private (bool Retry, string Note) DecideTemporaryTrampolineRetry(PaymentSession session,
                                                                     TrampolinePayerState state)
    {
        if (state.TemporaryRetryUsed)
            return (false, "the trampoline node failed again after a retry with a doubled budget");

        var doubled = state.Policy with
        {
            FeeBaseMsat = checked(state.Policy.FeeBaseMsat * 2),
            FeeProportionalMillionths = checked(state.Policy.FeeProportionalMillionths * 2)
        };
        state.TemporaryRetryUsed = true;
        if (!FitsFeeLimit(session, state, doubled, out var fee))
            return (false, $"a doubled trampoline budget ({fee} msat) is above the fee limit of "
                         + $"{session.MaxFee.MilliSatoshi} msat");

        state.Policy = doubled;
        state.NeedsNewAttempt = true;
        return (true, "retrying once with a doubled trampoline budget");
    }

    private bool FitsFeeLimit(PaymentSession session, TrampolinePayerState state, TrampolinePolicy policy,
                              out ulong fee)
    {
        fee = ulong.MaxValue;
        if (!TryBuildTrampolineAttempt(state, policy, Math.Max(1, _blockchainMonitor.LastProcessedBlockHeight),
                                       out var attempt, out _))
            return false;

        fee = attempt.OuterTotal.MilliSatoshi - session.Amount.MilliSatoshi;
        return fee <= session.MaxFee.MilliSatoshi;
    }

    /// <summary>
    /// A failure without a session: through the stored trampoline hops when the payment went through a trampoline node
    /// (each attempt tried, newest first, until one authenticates the error), else as any payment's. The outer layer's
    /// <c>attribution_data</c> is verified over <paramref name="route"/> (NL-898).
    /// </summary>
    private (FailureCode? Code, int? SourceIndex, string Reason, FailureInterpretation? Interpretation,
        AttributionVerification Attribution) DescribeStoredFailure(PaymentModel payment,
                                                                   IReadOnlyList<PaymentHop> route,
                                                                   HtlcRemoval removal,
                                                                   IReadOnlyList<PaymentTrampolineHopModel> hops)
    {
        if (hops.Count == 0 || removal.Kind != HtlcRemovalKind.Fail || route.Count == 0
                            || _trampolineFailureOnionService is not { } service)
            return DescribeFailure(route, removal);

        var outerSecrets = route.Select(h => h.SharedSecret).ToList();
        var attribution = VerifyOuterAttribution(outerSecrets, removal);
        var attributionText = DescribeOuterAttribution(route, attribution);
        var last = route.Count - 1;
        foreach (var attempt in hops.GroupBy(h => h.Attempt).OrderByDescending(g => g.Key))
        {
            var inner = attempt.OrderBy(h => h.HopIndex).ToList();
            var decrypted = service.DecryptTrampolineErrorPacket(outerSecrets, inner.Select(h => h.SharedSecret)
                                                                                   .ToList(), removal.Reason.Span);
            if (decrypted is null)
                continue;

            var code = decrypted.Code;
            var codeText = code is { } known ? $"{known} (0x{(ushort)known:X4})" : "an unreadable failure";
            if (decrypted.Layer == TrampolineFailureLayer.Outer)
            {
                // The stored route names the payee for its last hop: that hop is the trampoline node (inner hop 0)
                var described = decrypted.ErringHopIndex == last
                                    ? $"the trampoline node {inner[0].NodeId}"
                                    : $"hop {decrypted.ErringHopIndex} ({DescribeHop(route, decrypted.ErringHopIndex)})";
                return (code, decrypted.ErringHopIndex, $"{codeText} from {described}{attributionText}", null,
                        attribution);
            }

            var index = decrypted.ErringHopIndex;
            var node = index < inner.Count ? inner[index].NodeId.ToString() : "an unknown node";
            return (code, last + index, $"{codeText} from trampoline hop {index} ({node}) of payment "
                                      + $"{payment.PaymentHash}'s attempt {attempt.Key}{attributionText}", null,
                    attribution);
        }

        return (null, attribution.InvalidHopIndex,
                "The HTLC failed with an error onion no hop of either route authenticated" + attributionText, null,
                attribution);
    }

    /// <summary>
    /// The outer layer's <c>attribution_data</c> of a failure through a trampoline node, verified over the outer
    /// route's hops (NL-898; <see cref="IAttributionDataService.DecryptErrorPacket"/>, as for any payment: the hops up
    /// to the outer erring hop, all of them when no outer hop authenticated the return packet, which is the case for an
    /// error of the trampoline layer). The trampoline layer carries none (BOLTs PR 836). Absent without attribution or
    /// the service.
    /// </summary>
    private AttributionVerification VerifyOuterAttribution(IReadOnlyList<Secret> outerSecrets, HtlcRemoval removal)
    {
        if (removal.AttributionData.IsEmpty || _attributionDataService is null || outerSecrets.Count == 0)
            return AttributionVerification.Absent;

        return _attributionDataService.DecryptErrorPacket(outerSecrets, removal.Reason.Span,
                                                          removal.AttributionData.Span).Attribution;
    }

    /// <summary>
    /// What the outer layer's <c>attribution_data</c> adds to a trampoline failure's reason: the outer hop whose HMAC
    /// did not verify (it shares the blame with its upstream neighbour) and the verified hold times; empty without
    /// attribution.
    /// </summary>
    private static string DescribeOuterAttribution(IReadOnlyList<PaymentHop> outerRoute,
                                                   AttributionVerification attribution)
    {
        if (!attribution.IsPresent)
            return "";

        var tampered = attribution.InvalidHopIndex is { } invalid
                           ? $"; the attribution_data of outer hop {invalid} ({DescribeHop(outerRoute, invalid)}) did "
                           + "not verify"
                           : "";
        return tampered + DescribeHoldTimes(attribution);
    }

    /// <summary>The stored trampoline hops of a payment (every attempt); none when the unit of work stores none.
    /// </summary>
    private async Task<IReadOnlyList<PaymentTrampolineHopModel>> GetTrampolineHopsAsync(IServiceScope scope,
                                                                                         Hash paymentHash)
    {
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PaymentTrampolineHopDbRepository
                              .GetByPaymentAsync(paymentHash);
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the trampoline hops of payment {PaymentHash}", paymentHash);
            return [];
        }
    }

    /// <summary>The stored part of a payment offered as an HTLC, or null (none, or no part rows).</summary>
    private async Task<PaymentPartModel?> FindStoredPartAsync(IServiceScope scope, Hash paymentHash,
                                                              ChannelId channelId, ulong htlcId)
    {
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IPaymentPartDbRepository>()
                              .GetByHtlcAsync(paymentHash, channelId, htlcId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Could not read the part of payment {PaymentHash} for HTLC {HtlcId}", paymentHash,
                             htlcId);
            return null;
        }
    }

    #endregion

    /// <summary>
    /// What a trampoline session decided for a failed part.
    /// </summary>
    /// <param name="Code">The failure code, when one was read.</param>
    /// <param name="SourceIndex">The erring hop in the stored outer route (a trampoline-layer hop i counts from the
    /// trampoline node, the outer route's last hop: last + i).</param>
    /// <param name="Reason">The description.</param>
    /// <param name="Interpretation">The interpreted outer-route failure, for the retry policy and mission control.
    /// </param>
    /// <param name="Retry">The decision when the trampoline rules made it; null hands the part to the retry policy.
    /// </param>
    /// <param name="Note">The note of that decision.</param>
    private sealed record TrampolineDecision(
        FailureCode? Code,
        int? SourceIndex,
        string Reason,
        FailureInterpretation? Interpretation,
        bool? Retry,
        string? Note);
}

/// <summary>One trampoline attempt's route and outer budget (<see cref="PaymentService.TryBuildTrampolineAttempt"/>).
/// </summary>
/// <param name="Hops">The trampoline hops, the trampoline node first.</param>
/// <param name="OuterTotal">What the trampoline node receives in total (the outer <c>total_msat</c>).</param>
/// <param name="OuterFinalCltv">The outer <c>outgoing_cltv_value</c> at the trampoline node.</param>
internal readonly record struct TrampolineAttemptPlan(
    IReadOnlyList<TrampolineHop> Hops,
    LightningMoney OuterTotal,
    uint OuterFinalCltv);