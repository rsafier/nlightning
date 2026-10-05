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
using Domain.Payments.Models;
using Domain.Payments.Policies;
using Domain.Persistence.Interfaces;
using Routing;

/// <summary>
/// The <c>payroute</c> entry (<see cref="IPaymentService.PayRouteAsync"/>, NL-1145): validation of the caller's
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

        var multipart = parsedRoutes.Count > 1 || delivered.Any(amount => amount < total);
        if (multipart && !supportsMpp)
            throw new ArgumentException(
                "The payment is multi-part but the invoice does not offer basic_mpp.", nameof(request));
        var deliveredSum = delivered.Aggregate(LightningMoney.Zero, (sum, amount) => sum + amount);
        if (deliveredSum < total)
            throw new ArgumentException(
                $"The routes deliver {deliveredSum.MilliSatoshi} msat together, less than the total "
              + $"{total.MilliSatoshi} msat; the payee would never complete the set.", nameof(request));
        if (deliveredSum > total && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "PayRoute {PaymentHash}: the routes deliver {Delivered} msat together, over the total {Total} msat "
              + "(the payee accepts the surplus)", paymentHash, deliveredSum.MilliSatoshi, total.MilliSatoshi);

        // ---- Fees and, route by route, the PaymentRoute itself (its constructor re-checks the invariants)
        var maxFee = options.MaxFee ?? _sendOptions.Value.GetMaxFee(total);
        var fee = parsedRoutes.Select(r => r.Supplied.FirstHopAmount - r.Supplied.Hops[^1].AmountToForward)
                              .Aggregate(LightningMoney.Zero, (sum, part) => sum + part);
        if (fee > maxFee)
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

        ValidateFirstHopLiquidity(paymentRoutes);

        // ---- The session: one attempt, exactly these routes, nothing re-planned
        var target = invoiceTarget
                  ?? new PaymentTarget(parsedRoutes[0].Supplied.Hops[^1].NodeId, paymentHash, paymentSecret, total,
                                       minFinalCltvExpiryDelta, []);
        if (target.PayeeNodeId == _secureKeyManager.GetNodePubKey())
            throw new ArgumentException("The payee is this node.", nameof(request));

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? deadline = options.Timeout == Timeout.InfiniteTimeSpan ? null : now + options.Timeout;
        var session = new PaymentSession(target, bolt11, total, maxFee, request.Routes.Count, request.Routes.Count,
                                         deadline, now)
        {
            SuppliedRoutes = paymentRoutes,
            Labels = options.Labels
        };
        await RunSessionAsync(session, options.Timeout, cancellationToken);

        var payment = await GetPaymentAsync(paymentHash, CancellationToken.None)
                   ?? throw new InvalidOperationException($"Payment {paymentHash} was not stored.");

        // A shard set settles atomically: when the payment succeeded, the payee fulfilled every part, also the ones
        // whose fulfill arrived after the session was completed (their part is still InFlight in the snapshot)
        var settled = payment.Status == PaymentStatus.Succeeded;
        if (settled)
            await SettleLeftoverPartRowsAsync(session);
        var outcomes = session.Parts.Select(
                                     (part, index) => new RouteOutcome(
                                         index,
                                         part.Status == PaymentPartStatus.InFlight && settled
                                             ? PaymentPartState.Succeeded
                                             : ToPartState(part.Status),
                                         part.HtlcId, part.Failure?.Code, part.Failure?.SourceIndex,
                                         part.Failure?.Reason))
                                 .ToList();
        return new PayRouteResult(payment, outcomes);
    }

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
                    throw new ArgumentException(
                        $"Channel {group.Key} cannot carry {amount} msat more (with the {planned.Count} route(s) "
                      + "already on it): balance, reserve, fee, in-flight, HTLC-count or dust limit reached.",
                        nameof(routes));
                planned.Add(amount);
            }
        }
    }

    /// <summary>
    /// Marks the part rows a succeeded shard set left <c>InFlight</c> (their fulfill arrived after the session was
    /// completed, so no outcome handler updated them) as <c>Succeeded</c> — the payee settled the whole set.
    /// </summary>
    private async Task SettleLeftoverPartRowsAsync(PaymentSession session)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            foreach (var part in session.Parts.Where(
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