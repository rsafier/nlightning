using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Send;

using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Payments.Policies;
using Domain.Persistence.Interfaces;
using Keysend;
using Routing;

/// <summary>
/// The <c>payroute</c> entry (<see cref="IPaymentService.PayRouteAsync"/>, NL-1082): validation of the caller's
/// routes and identity, the session over them, and the per-route outcomes.
/// </summary>
public sealed partial class PaymentService
{
    private const ushort RawFormMinFinalCltvExpiryDelta = 18;

    /// <inheritdoc />
    public async Task<PayRouteResult> PayRouteAsync(PayRouteRequest request, PayInvoiceOptions options,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "The timeout must be positive.");
        if (request.Routes.Count is < 1 or > PaymentSendOptions.MaxPartsLimit)
            throw new ArgumentException(
                $"The route set must hold 1 to {PaymentSendOptions.MaxPartsLimit} routes.", nameof(request));
        if ((request.Bolt11 is null) == (request.PaymentHash is null))
            throw new ArgumentException(
                "Give exactly one payment identity: a BOLT 11 invoice or a payment hash.", nameof(request));
        if (_blockchainMonitor.LastProcessedBlockHeight == 0)
            throw new InvalidOperationException("No block has been processed yet; cannot set the HTLC expiry.");

        // ---- The identity: an invoice (hash, secret, amount, basic_mpp from it) or the raw form
        if (request.CustomRecords.Count > 0 && request.KeysendPreimage is null)
            throw new ArgumentException("Custom records are sent only with a keysend preimage.", nameof(request));
        KeysendFinalRecords? keysend = null;
        PaymentTarget? invoiceTarget = null;
        string? bolt11 = null;
        Hash paymentHash;
        Secret paymentSecret;
        ushort minFinalCltvExpiryDelta;
        bool supportsMpp;
        if (request.Bolt11 is { } invoiceString)
        {
            var invoice = DecodeInvoice(invoiceString);
            if (invoice.BlindedPaymentPaths.Count > 0)
                throw new ArgumentException(
                    "An invoice with blinded paths names its recipient through them; pay it with payinvoice.",
                    nameof(request));
            invoiceTarget = PaymentTarget.FromInvoice(invoice);
            paymentHash = invoiceTarget.PaymentHash;
            paymentSecret = invoiceTarget.PaymentSecret;
            minFinalCltvExpiryDelta = invoiceTarget.MinFinalCltvExpiryDelta;
            supportsMpp = invoiceTarget.SupportsMpp;
            bolt11 = invoiceString;
            if (invoiceTarget.PayeeNodeId == _secureKeyManager.GetNodePubKey())
                throw new ArgumentException(
                    "The payee is this node; a rebalance is payinvoice of one of our own invoices.",
                    nameof(request));
        }
        else
        {
            paymentHash = request.PaymentHash!.Value;
            paymentSecret = request.PaymentSecret ?? new Secret(new byte[32]);
            if (request.KeysendPreimage is { } preimage)
            {
                if (request.Routes.Count != 1 || request.PaymentSecret is not null || request.TotalAmount is not null)
                    throw new ArgumentException(
                        "A keysend payment goes over one route without a payment secret or total.", nameof(request));
                // The hash need not be the preimage's SHA256: LND's SendToRouteV2 passes such a route through, and
                // bos probes keysend with a random hash and the real preimage, expecting the payee's
                // incorrect_or_unknown_payment_details (NL-1251); a mismatched payment can only fail at the payee
                keysend = new KeysendFinalRecords(preimage, CustomRecordCodec.Validate(request.CustomRecords));
            }
            minFinalCltvExpiryDelta = RawFormMinFinalCltvExpiryDelta;
            // The caller asserts the payee accepts a multi-part payment of this total; we cannot know (documented)
            supportsMpp = true;
        }

        // ---- Per-route shape and first-hop channels, then the routes proper
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var maxCltv = height + _nodeOptions.Value.Routing.MaxCltvExpiryDistance;
        var channels = new Dictionary<ChannelId, ChannelModel>(request.Routes.Count);
        var parsedRoutes = new List<(PayRouteRoute Supplied, ChannelModel Channel, List<RouteHop> Hops)>(
            request.Routes.Count);
        foreach (var (index, supplied) in request.Routes.Select((r, i) => (i, r)))
        {
            var routeName = $"route {index + 1}";
            if (supplied.Hops.Count == 0)
                throw new ArgumentException($"{routeName} has no hops.", nameof(request));
            if (supplied.Hops[^1].OutgoingShortChannelId is not null)
                throw new ArgumentException($"{routeName}'s last hop is the payee and has no short_channel_id.",
                                            nameof(request));
            for (var i = 0; i < supplied.Hops.Count - 1; i++)
                if (supplied.Hops[i].OutgoingShortChannelId is null)
                    throw new ArgumentException($"{routeName}'s hop {i + 1} has no short_channel_id.",
                                                nameof(request));

            if (!_channelMemoryRepository.TryGetChannel(supplied.FirstHopChannelId, out var channel)
             || channel is not { State: ChannelState.Open, Commitments: not null })
                throw new ArgumentException(
                    $"{routeName}'s first hop channel {supplied.FirstHopChannelId} is not an open channel of ours.",
                    nameof(request));
            if (channel.RemoteNodeId != supplied.Hops[0].NodeId)
                throw new ArgumentException(
                    $"{routeName}'s first hop channel is to {channel.RemoteNodeId}, not to {supplied.Hops[0].NodeId}.",
                    nameof(request));
            if (!await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                throw new ArgumentException($"{routeName}'s first hop channel {channel.ChannelId} is not established "
                                          + "on the current connection.", nameof(request));
            channels[channel.ChannelId] = channel;

            // CLTV sanity: every hop's outgoing expiry strictly below the previous one's, all within the node's
            // distance limit, the first HTLC in the future and the payee's at least min_final + the height
            if (supplied.FirstHopCltvExpiry <= height || supplied.FirstHopCltvExpiry > maxCltv)
                throw new ArgumentException(
                    $"{routeName}'s first-hop cltv_expiry {supplied.FirstHopCltvExpiry} is outside the height "
                  + $"{height}..{maxCltv}.", nameof(request));
            // A route over one hop is direct: that hop is the payee and its cltv is the first HTLC's
            if (supplied.Hops.Count > 1 && supplied.Hops[0].OutgoingCltvValue >= supplied.FirstHopCltvExpiry)
                throw new ArgumentException(
                    $"{routeName}'s first hop does not lower the cltv_expiry.", nameof(request));
            // The payee's expiry is the last forwarding hop's outgoing expiry (the same HTLC), so the strict
            // decrease ends before it
            for (var i = 1; i < supplied.Hops.Count - 1; i++)
                if (supplied.Hops[i].OutgoingCltvValue >= supplied.Hops[i - 1].OutgoingCltvValue)
                    throw new ArgumentException(
                        $"{routeName}'s hop {i + 1} does not lower the cltv_expiry.", nameof(request));
            var finalCltv = supplied.Hops[^1].OutgoingCltvValue;
            if (finalCltv < height + minFinalCltvExpiryDelta)
                throw new ArgumentException(
                    $"{routeName}'s final cltv_expiry {finalCltv} is below the height {height} + the payee's minimum "
                  + $"delta {minFinalCltvExpiryDelta}.", nameof(request));

            parsedRoutes.Add((supplied, channel,
                              supplied.Hops.Select(h => new RouteHop(h.NodeId, h.AmountToForward,
                                                                     h.OutgoingCltvValue, h.OutgoingShortChannelId))
                                          .ToList()));
        }

        // ---- The total: what every route reports as total_msat and the payee must receive in sum
        var delivered = parsedRoutes.Select(r => r.Supplied.Hops[^1].AmountToForward).ToList();
        LightningMoney total;
        if (request.TotalAmount is { } explicitTotal)
        {
            total = explicitTotal;
        }
        else if (invoiceTarget?.Amount is { } invoiceAmount)
        {
            total = invoiceAmount;
        }
        else if (parsedRoutes.Count == 1)
        {
            total = delivered[0];
        }
        else
        {
            throw new ArgumentException(
                "The raw form of a multi-route payment needs the total (total_msat) every route reports.",
                nameof(request));
        }

        // An attached round is one more part of a multi-part payment; a shard of an LND set (IndependentShards) may
        // carry part of the total only, and the rest comes in later calls
        var multipart = parsedRoutes.Count > 1 || delivered.Any(amount => amount < total)
                     || request.Attach == PayRouteAttachMode.Required;
        if (multipart && !supportsMpp)
            throw new ArgumentException(
                "The payment is multi-part but the invoice does not offer basic_mpp.", nameof(request));
        var deliveredSum = delivered.Aggregate(LightningMoney.Zero, (sum, amount) => sum + amount);
        if (request.IndependentShards)
        {
            // LND: the attempted value may not exceed the payment amount (the parts in flight are added under the
            // hash's lock, when the call attaches)
            if (deliveredSum > total)
                throw new ArgumentException(
                    $"The routes deliver {deliveredSum.MilliSatoshi} msat together, over the payment's total "
                  + $"{total.MilliSatoshi} msat.", nameof(request));
        }
        else if (request.Attach == PayRouteAttachMode.Never)
        {
            if (deliveredSum < total)
                throw new ArgumentException(
                    $"The routes deliver {deliveredSum.MilliSatoshi} msat together, less than the total "
                  + $"{total.MilliSatoshi} msat; the payee would never complete the set.", nameof(request));
            if (deliveredSum > total && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "PayRoute {PaymentHash}: the routes deliver {Delivered} msat together, over the total {Total} msat "
                  + "(the payee accepts the surplus)", paymentHash, deliveredSum.MilliSatoshi, total.MilliSatoshi);
        }

        // ---- Fees and, route by route, the PaymentRoute itself (its constructor re-checks the invariants). An
        // attached round's fees are checked with the fees of the parts in flight, under the hash's lock
        // LND's sendToRoute applies no fee limit ("we are not requesting routes"): an LND shard without an explicit
        // limit is not checked against the default one, alone or with the shards in flight
        var unlimitedFee = request.IndependentShards && options.MaxFee is null;
        var maxFee = options.MaxFee ?? _sendOptions.Value.GetMaxFee(total);
        var fee = parsedRoutes.Select(r => r.Supplied.FirstHopAmount - r.Supplied.Hops[^1].AmountToForward)
                              .Aggregate(LightningMoney.Zero, (sum, part) => sum + part);
        if (!unlimitedFee && fee > maxFee && request.Attach != PayRouteAttachMode.Required)
            throw new ArgumentException(
                $"The routes pay {fee.MilliSatoshi} msat in fees, over the limit of {maxFee.MilliSatoshi} msat "
              + "(:--max-fee-msat raises it).", nameof(request));

        var paymentRoutes = new List<SuppliedRoutePart>(parsedRoutes.Count);
        foreach (var ((supplied, channel, hops), index) in parsedRoutes.Select((r, i) => (r, i)))
        {
            PaymentRoute route;
            try
            {
                route = new PaymentRoute(hops, supplied.FirstHopAmount, supplied.FirstHopCltvExpiry, paymentHash,
                                         paymentSecret, invoiceTarget?.PaymentMetadata, total);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"Route {index + 1}: {e.Message}", nameof(request), e);
            }

            ValidateForwardingPolicies(route, index);
            paymentRoutes.Add(new SuppliedRoutePart(ToCandidate(channel), route));
        }

        try
        {
            ValidateFirstHopLiquidity(paymentRoutes);
        }
        catch (PayRouteLiquidityException e) when (request is { IndependentShards: true, SkipTemporaryFailures: false })
        {
            // LND: the shard's attempt fails at our own channel and, without skip_temp_err, fails the payment in
            // flight pending (NL-1276)
            await FailPayRouteSessionPendingAsync(paymentHash, e.Message, cancellationToken);
            throw;
        }

        // ---- The session: one attempt, exactly these routes, nothing re-planned
        var target = invoiceTarget
                  ?? new PaymentTarget(parsedRoutes[0].Supplied.Hops[^1].NodeId, paymentHash, paymentSecret, total,
                                       minFinalCltvExpiryDelta, []);
        if (target.PayeeNodeId == _secureKeyManager.GetNodePubKey())
            throw new ArgumentException("The payee is this node.", nameof(request));

        // ---- Start a session over exactly these routes, or attach them to the one in flight (NL-1276)
        var call = new PayRouteCall(request, options, target, bolt11, total, maxFee, fee, deliveredSum, keysend,
                                    paymentRoutes);
        var (session, round) = await StartOrAttachPayRouteAsync(call, cancellationToken);

        if (request.IndependentShards)
        {
            // A shard of an LND set answers with its own routes' outcomes (the payment may go on with other shards)
            await WaitAsync(session.WhenPartsResolvedAsync(round), options.Timeout, cancellationToken);
        }
        else
        {
            await WaitAsync(session.Completion.Task, options.Timeout, cancellationToken);
            if (cancellationToken.IsCancellationRequested && request.Attach == PayRouteAttachMode.Never)
                session.StopRequested = true;
        }

        var payment = await GetPaymentAsync(paymentHash, CancellationToken.None)
                   ?? throw new InvalidOperationException($"Payment {paymentHash} was not stored.");

        // A shard set settles atomically: when the payment succeeded, the payee fulfilled every part, also the ones
        // whose fulfill arrived after the session was completed (their part is still InFlight in the snapshot)
        var settled = payment.Status == PaymentStatus.Succeeded;
        if (settled)
            await SettleLeftoverPartRowsAsync(session);
        var outcomes = round.Select((part, index) =>
                                    {
                                        var settledLate = part.Status == PaymentPartStatus.InFlight && settled;
                                        return new RouteOutcome(index,
                                                                settledLate
                                                                    ? PaymentPartState.Succeeded
                                                                    : ToPartState(part.Status),
                                                                part.HtlcId, part.Failure?.Code,
                                                                part.Failure?.SourceIndex, part.Failure?.Reason)
                                        {
                                            OfferedAt = part.OfferedAt,
                                            ResolvedAt = part.ResolvedAt
                                                      ?? (settledLate ? payment.CompletedAt : null)
                                        };
                                    })
                            .ToList();
        return new PayRouteResult(payment, outcomes);
    }

    /// <summary>
    /// Marks the <c>payroute</c> session of <paramref name="paymentHash"/> still in flight, when there is one, failed
    /// pending (<see cref="PaymentSession.PendingFailure"/>, NL-1276).
    /// </summary>
    private async Task FailPayRouteSessionPendingAsync(Hash paymentHash, string reason,
                                                       CancellationToken cancellationToken)
    {
        using (await AcquireHashLockAsync(paymentHash, cancellationToken))
        {
            if (_sessions.TryGetValue(paymentHash, out var session) && session.ManualRoutes)
                session.PendingFailure ??= reason;
        }
    }

    /// <summary>What one <c>payroute</c> call validated, for <see cref="StartOrAttachPayRouteAsync"/>.</summary>
    private sealed record PayRouteCall(PayRouteRequest Request, PayInvoiceOptions Options, PaymentTarget Target,
                                       string? Bolt11, LightningMoney Total, LightningMoney MaxFee, LightningMoney Fee,
                                       LightningMoney Delivered, KeysendFinalRecords? Keysend,
                                       IReadOnlyList<SuppliedRoutePart> Routes);

    /// <summary>
    /// Under the payment hash's lock: attaches the call's routes to the <c>payroute</c> session of the hash still in
    /// flight (<see cref="PayRouteRequest.Attach"/>, NL-1276), or starts a session over them.
    /// </summary>
    /// <returns>The session and the call's own parts, in the routes' order.</returns>
    /// <exception cref="InvalidOperationException">Attaching was required but no <c>payroute</c> payment of the hash
    /// is in flight (it succeeded, failed or never started, or the node restarted since), or a payment of the hash is
    /// in flight that cannot take more routes (a <c>payinvoice</c>, keysend or other session) or already succeeded.
    /// Nothing was sent.</exception>
    /// <exception cref="ArgumentException">The attached routes do not fit the payment in flight (another secret,
    /// total or payee, past the attach window, over the fee limit with the parts in flight, too many parts, or the
    /// parts would deliver less (or, for LND shards, more) than the total). Nothing was sent.</exception>
    private async Task<(PaymentSession Session, IReadOnlyList<PaymentPart> Round)> StartOrAttachPayRouteAsync(
        PayRouteCall call, CancellationToken cancellationToken)
    {
        var request = call.Request;
        var paymentHash = call.Target.PaymentHash;
        using (await AcquireHashLockAsync(paymentHash, cancellationToken))
        {
            if (request.Attach != PayRouteAttachMode.Never && _sessions.TryGetValue(paymentHash, out var existing))
                return (existing, await AttachPayRouteLockedAsync(existing, call));

            if (request.Attach == PayRouteAttachMode.Required)
            {
                var stored = await GetPaymentAsync(paymentHash, CancellationToken.None);
                throw new InvalidOperationException(
                    stored?.Status == PaymentStatus.Succeeded
                        ? $"The payment for payment hash {paymentHash} already succeeded; there is nothing to "
                        + "attach to."
                        : $"No payroute payment for payment hash {paymentHash} is in flight to attach to (it ended, "
                        + "never started, or the node restarted since); start one with payroute without --attach.");
            }

            await ThrowIfPaymentExistsAsync(paymentHash);
            var now = _timeProvider.GetUtcNow();
            DateTimeOffset? deadline = call.Options.Timeout == Timeout.InfiniteTimeSpan
                                           ? null
                                           : now + call.Options.Timeout;
            var session = new PaymentSession(call.Target, call.Bolt11, call.Total, call.MaxFee, call.Routes.Count,
                                             call.Routes.Count, deadline, now)
            {
                SuppliedRoutes = call.Routes,
                Keysend = call.Keysend,
                Labels = call.Options.Labels
            };
            _sessions[paymentHash] = session;
            try
            {
                return (session, await RunManualRoundAsync(session, call.Routes,
                                                           failsPaymentOnFailure: FailsPaymentOnFailure(request)));
            }
            catch
            {
                if (!session.HasPartsInFlight)
                    CompleteSession(session);
                throw;
            }
        }
    }

    /// <summary>
    /// Attaches the call's routes to <paramref name="session"/> (NL-1276, plan §6) after checking they fit it, then
    /// offers them as one more manual round. Call it under the hash's lock: a session still registered then has a
    /// part in flight (one without is decided and removed under the same lock).
    /// </summary>
    private async Task<IReadOnlyList<PaymentPart>> AttachPayRouteLockedAsync(PaymentSession session,
                                                                            PayRouteCall call)
    {
        var request = call.Request;
        var paymentHash = session.PaymentHash;
        if (!session.ManualRoutes)
            throw new InvalidOperationException(
                $"A payment for payment hash {paymentHash} is already {PaymentStatus.InFlight}, sent by the node's "
              + "planner (payinvoice, keysend or an offer); payroute can attach only to a payroute payment.");
        if (session.Keysend is not null || call.Keysend is not null)
            throw new InvalidOperationException(
                $"A keysend payment for payment hash {paymentHash} goes over one route; nothing can be attached to "
              + "it.");
        if (session.PendingFailure is { } pendingFailure)
            throw new InvalidOperationException(
                $"The payment for payment hash {paymentHash} is pending failed (a part failed: {pendingFailure}); "
              + "nothing more can be attached until its parts in flight are resolved and it fails (LND: payment "
              + "pending failed).");

        if (call.Target.PaymentSecret != session.Target.PaymentSecret)
            throw new ArgumentException(
                "The payment secret differs from the one of the payment in flight; every part of a set carries the "
              + "same one.", nameof(call));
        if (call.Total != session.Amount)
            throw new ArgumentException(
                $"The total {call.Total.MilliSatoshi} msat differs from the {session.Amount.MilliSatoshi} msat every "
              + "part of the payment in flight reports.", nameof(call));
        if (call.Target.PayeeNodeId != session.Target.PayeeNodeId)
            throw new ArgumentException(
                $"The routes end at {call.Target.PayeeNodeId}, not at the payee {session.Target.PayeeNodeId} of the "
              + "payment in flight.", nameof(call));
        if (session.Parts.Count + call.Routes.Count > byte.MaxValue + 1)
            throw new ArgumentException(
                $"The payment already has {session.Parts.Count} parts; at most {byte.MaxValue + 1} can be stored.",
                nameof(call));

        var window = _sendOptions.Value.PayRouteAttachWindow;
        var now = _timeProvider.GetUtcNow();
        if (request.Attach == PayRouteAttachMode.Required
         && session.InFlightParts.Select(p => p.OfferedAt).Min() is { } oldest
         && now - oldest > window)
            throw new ArgumentException(
                $"Too late: the oldest part in flight was offered {(now - oldest).TotalSeconds:F0} s ago, past the "
              + $"{window.TotalSeconds:F0} s attach window (the payee fails the parts it holds after its "
              + "mpp_timeout); wait for the payment to end and pay again.", nameof(call));

        var inFlightDelivered = LightningMoney.MilliSatoshis(
            session.InFlightParts.Aggregate(0UL, (sum, p) => sum + p.Route.Amount.MilliSatoshi));
        var delivered = inFlightDelivered + call.Delivered;
        if (request.IndependentShards)
        {
            if (delivered > call.Total)
                throw new ArgumentException(
                    $"The parts in flight deliver {inFlightDelivered.MilliSatoshi} msat; with these routes "
                  + $"{delivered.MilliSatoshi} msat, over the payment's total {call.Total.MilliSatoshi} msat.",
                    nameof(call));
        }
        else if (delivered < call.Total)
        {
            throw new ArgumentException(
                $"The parts in flight deliver {inFlightDelivered.MilliSatoshi} msat; with these routes "
              + $"{delivered.MilliSatoshi} msat, less than the total {call.Total.MilliSatoshi} msat, so the payee "
              + "would never complete the set.", nameof(call));
        }
        else if (inFlightDelivered >= call.Total)
        {
            // Nothing failed to replace: the parts in flight are only slow (LND caps the attempted value at the
            // amount, ErrValueExceedsAmt)
            throw new ArgumentException(
                $"The parts in flight already deliver {inFlightDelivered.MilliSatoshi} msat, the whole total "
              + $"{call.Total.MilliSatoshi} msat; there is nothing to replace (wait for them to resolve).",
                nameof(call));
        }
        else if (call.Routes.Count > 1
              && delivered.MilliSatoshi - call.Routes.Max(r => r.Route.Amount.MilliSatoshi) > call.Total.MilliSatoshi)
        {
            // Even without its largest route the round overpays: some of its routes are not needed
            throw new ArgumentException(
                $"With the parts in flight these routes deliver {delivered.MilliSatoshi} msat, over the total "
              + $"{call.Total.MilliSatoshi} msat even without the largest of them; attach only what is missing.",
                nameof(call));
        }

        var maxFee = call.Options.MaxFee ?? session.MaxFee;
        var fees = LightningMoney.MilliSatoshis(session.FeesInFlightMsat) + call.Fee;
        // LND's sendToRoute applies no fee limit: an LND shard without an explicit one is not checked
        if (!(request.IndependentShards && call.Options.MaxFee is null) && fees > maxFee)
            throw new ArgumentException(
                $"The parts in flight pay {session.FeesInFlightMsat} msat in fees; with these routes "
              + $"{fees.MilliSatoshi} msat, over the limit of {maxFee.MilliSatoshi} msat (--max-fee-msat raises it).",
                nameof(call));

        _logger.LogInformation("PayRoute {PaymentHash}: attaching {Routes} route(s) delivering {Delivered} msat to "
                             + "the {InFlight} part(s) in flight", paymentHash, call.Routes.Count,
                               call.Delivered.MilliSatoshi, session.InFlightParts.Count());
        return await RunManualRoundAsync(session, call.Routes, "attached route", FailsPaymentOnFailure(request));
    }

    /// <summary>Whether any failure of the call's routes fails the payment pending: an LND shard without
    /// <c>skip_temp_err</c> (<see cref="PayRouteRequest.SkipTemporaryFailures"/>).</summary>
    private static bool FailsPaymentOnFailure(PayRouteRequest request) =>
        request is { IndependentShards: true, SkipTemporaryFailures: false };

    /// <summary>
    /// Rejects a supplied route whose hops do not pay the forwarding fees the graph knows (<see cref="ForwardingFee"/>
    /// over each hop's outgoing channel policy, and the amount within the policy's HTLC bounds). Channels the graph
    /// does not know (private ones) are left to the hops themselves: a short-paid hop fails the HTLC back cleanly.
    /// </summary>
    private void ValidateForwardingPolicies(PaymentRoute route, int index)
    {
        var graphSource = _graphPathSource;
        if (graphSource is not { IsAvailable: true })
            return;

        if (graphSource.CreateContext(0) is not { Graph: { } graph })
            return;

        for (var i = 0; i < route.Hops.Count - 1; i++)
        {
            var hop = route.Hops[i];
            if (!graph.TryGetChannel(hop.OutgoingShortChannelId!.Value, out var channel))
                continue;

            var policy = channel.GetPolicy(channel.GetDirectionFrom(hop.NodeId));
            if (policy is null)
                continue;

            var incoming = i == 0 ? route.FirstHopAmount : route.Hops[i - 1].AmountToForward;
            if (!ForwardingFee.PaysSufficientFee(policy.FeeBaseMsat, policy.FeeProportionalMillionths,
                                                 incoming.MilliSatoshi, hop.AmountToForward.MilliSatoshi))
                throw new ArgumentException(
                    $"Route {index + 1}: hop {i + 1} ({hop.NodeId}) forwards {hop.AmountToForward.MilliSatoshi} msat "
                  + $"over {hop.OutgoingShortChannelId} without the channel's fee "
                  + $"({policy.FeeBaseMsat} base + {policy.FeeProportionalMillionths} ppm).", nameof(route));
            if (policy.HtlcMaximumMsat is { } maximum && hop.AmountToForward.MilliSatoshi > maximum)
                throw new ArgumentException(
                    $"Route {index + 1}: hop {i + 1} forwards {hop.AmountToForward.MilliSatoshi} msat over "
                  + $"{hop.OutgoingShortChannelId}, over the channel's htlc_maximum_msat {maximum}.", nameof(route));
            if (hop.AmountToForward.MilliSatoshi < policy.HtlcMinimumMsat)
                throw new ArgumentException(
                    $"Route {index + 1}: hop {i + 1} forwards {hop.AmountToForward.MilliSatoshi} msat over "
                  + $"{hop.OutgoingShortChannelId}, under the channel's htlc_minimum_msat {policy.HtlcMinimumMsat}.",
                    nameof(route));
        }
    }

    /// <summary>
    /// Asks the commitment engine (through <see cref="LocalLiquidityEstimator"/>) whether our first-hop channels can
    /// carry the routes that leave through them, together: balance, reserves, fees, in-flight caps, HTLC counts and
    /// dust — before anything is offered.
    /// </summary>
    private void ValidateFirstHopLiquidity(IReadOnlyList<SuppliedRoutePart> routes)
    {
        foreach (var group in routes.GroupBy(r => r.Channel.ChannelId))
        {
            var channel = _channelMemoryRepository.FindChannels(
                                              c => c.ChannelId == group.Key && c.Commitments is not null)
                                       .Single();
            var commitments = channel.Commitments!;
            var planned = new List<ulong>();
            foreach (var route in group)
            {
                var amount = route.Route.FirstHopAmount.MilliSatoshi;
                if (amount > LocalLiquidityEstimator.MaxSendableMsat(commitments, planned,
                                                                     route.Route.FirstHopCltvExpiry))
                    throw new PayRouteLiquidityException(
                        $"Channel {group.Key} cannot carry {amount} msat more (with the {planned.Count} route(s) "
                      + "already on it): balance, reserve, fee, in-flight, HTLC-count or dust limit reached.",
                        nameof(routes));
                planned.Add(amount);
            }
        }
    }

    /// <summary>
    /// Marks the part rows a succeeded shard set left <c>InFlight</c> (their fulfill arrived after the session was
    /// completed, so no outcome handler updated them) as <c>Succeeded</c> — the payee settled the whole set. Every
    /// part of the session, not only the calling round's: an earlier call may have returned at its own timeout
    /// (NL-1276). The fulfill handler marks the row of such a part too when its fulfill arrives later.
    /// </summary>
    private async Task SettleLeftoverPartRowsAsync(PaymentSession session)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            foreach (var part in session.Parts.ToList().Where(
                         p => p is { Status: PaymentPartStatus.InFlight, HtlcId: not null }))
                await UpdateStoredPartAsync(scope, session.PaymentHash, part.Channel.ChannelId, part.HtlcId!.Value,
                                            PaymentPartState.Succeeded);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not settle the leftover part rows of the succeeded payroute payment "
                              + "{PaymentHash}", session.PaymentHash);
        }
    }

    private static PaymentPartState ToPartState(PaymentPartStatus status) =>
        status switch
        {
            PaymentPartStatus.InFlight => PaymentPartState.InFlight,
            PaymentPartStatus.Succeeded => PaymentPartState.Succeeded,
            _ => PaymentPartState.Failed
        };
}

/// <summary>
/// A <c>payroute</c> refused before anything was offered because a first-hop channel cannot carry its routes
/// (balance, reserve, fee, in-flight, HTLC-count or dust limit). LND reports that case as a
/// <c>temporary_channel_failure</c> at our own node, which callers that probe routes rely on (NL-1242).
/// </summary>
public sealed class PayRouteLiquidityException(string message, string paramName) : ArgumentException(message, paramName);