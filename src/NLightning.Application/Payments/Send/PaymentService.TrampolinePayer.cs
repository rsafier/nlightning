using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Send;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;
using Routing;

/// <summary>
/// The payer side of trampoline routing (NL-875, BOLTs PR 836 "Trampoline Payments", plan <c>TRAMPOLINE_PLAN.md</c>
/// TR4): which payments go through a trampoline node and the session that sends them.
/// </summary>
/// <remarks>
/// <para>Choice of the trampoline node: <see cref="PayInvoiceOptions.TrampolineNode"/> when the call names one, else
/// <see cref="PaymentSendOptions.Trampoline"/>: <see cref="TrampolinePaymentMode.Always"/> picks one whenever the
/// recipient supports trampoline, <see cref="TrampolinePaymentMode.Auto"/> only when the planner finds no route of its
/// own, <see cref="TrampolinePaymentMode.Never"/> (the default) never. A picked node is a peer with a usable channel
/// whose <c>init</c> features set <c>trampoline_routing</c> (the one we can send the most to first), else a node of
/// the gossip graph whose announcement sets it.</para>
/// <para>Recipients: a BOLT 11 invoice without <c>trampoline_routing</c> is refused when the call names a trampoline
/// node (the PR: "MUST NOT use trampoline routing to pay that invoice") and paid normally under a mode; an invoice that
/// requires it (bit 56) is refused unless a trampoline node is used. When the payee is the trampoline node itself the
/// payment is a plain one. BOLT 12 (and bLIP 39) recipients with the bit are paid through their blinded hops as
/// trampoline hops, the others through <c>recipient_blinded_paths</c>.</para>
/// <para>Budget (decision D-TR6): the trampoline node's policy learnt from an earlier
/// <c>trampoline_fee_or_expiry_insufficient</c>, else <see cref="PaymentSendOptions.TrampolineFeeBaseMsat"/>,
/// <see cref="PaymentSendOptions.TrampolineFeeProportionalMillionths"/> and
/// <see cref="PaymentSendOptions.TrampolineCltvExpiryDelta"/>; the trampoline fee and the outer routes' fees together
/// stay within the fee limit.</para>
/// </remarks>
public sealed partial class PaymentService
{
    /// <summary>The policy a trampoline node told us (memory), or null.</summary>
    internal TrampolinePolicy? GetCachedTrampolinePolicy(CompactPubKey trampolineNode) =>
        _trampolinePolicies.TryGetValue(trampolineNode, out var policy) ? policy : null;

    /// <summary>
    /// The trampoline node a BOLT 11 payment goes through, or null for a plain payment (see the class remarks).
    /// </summary>
    /// <exception cref="ArgumentException">The call names a trampoline node the invoice does not allow, or the invoice
    /// requires trampoline and no trampoline node is used.</exception>
    private async Task<CompactPubKey?> ResolveTrampolineNodeAsync(PayInvoiceOptions options, PaymentTarget target,
                                                                  LightningMoney amount, FeatureSet? recipientFeatures,
                                                                  CancellationToken cancellationToken)
    {
        var payee = target.PayeeNodeId;
        var supports = recipientFeatures?.IsFeatureSet(Feature.OptionTrampolineRouting) ?? false;
        var requires = recipientFeatures?.IsFeatureSet(Feature.OptionTrampolineRouting, true) ?? false;

        CompactPubKey? chosen;
        if (options.TrampolineNode is { } named)
        {
            if (named == _secureKeyManager.GetNodePubKey())
                throw new ArgumentException("The trampoline node cannot be this node.", nameof(options));
            if (named == payee)
                return null;
            if (!supports)
                throw new ArgumentException("The invoice does not support trampoline routing (feature bit 57): it "
                                          + "cannot be paid through a trampoline node.", nameof(options));

            chosen = named;
        }
        else
        {
            chosen = _sendOptions.Value.Trampoline switch
            {
                _ when !supports => null,
                TrampolinePaymentMode.Always => await SelectTrampolineNodeAsync(payee, cancellationToken),
                TrampolinePaymentMode.Auto when !await HasOwnRouteAsync(target, amount, cancellationToken) =>
                    await SelectTrampolineNodeAsync(payee, cancellationToken),
                _ => null
            };
            if (chosen == payee)
                return null;
        }

        if (chosen is null && requires)
            throw new ArgumentException("The invoice requires trampoline routing (feature bit 56) and no trampoline "
                                      + "node is used: name one (--trampoline) or set Node:Payments:Trampoline.",
                                        nameof(options));

        return chosen;
    }

    /// <summary>Whether the planner finds a route of its own for the whole amount (a first round, nothing sent).
    /// </summary>
    private async Task<bool> HasOwnRouteAsync(PaymentTarget target, LightningMoney amount,
                                              CancellationToken cancellationToken)
    {
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var channels = await GetUsableChannelsAsync(cancellationToken);
        var sendOptions = _sendOptions.Value;
        var request = new PaymentPlanRequest(target, amount.MilliSatoshi, amount.MilliSatoshi,
                                             sendOptions.GetMaxFee(amount).MilliSatoshi,
                                             Math.Clamp(sendOptions.MaxParts, 1, PaymentSendOptions.MaxPartsLimit),
                                             height, _secureKeyManager.GetNodePubKey(),
                                             channels.Select(ToCandidate).ToList(),
                                             CreateLiquidityProbe(channels, height), new RouteConstraints(),
                                             sendOptions.MinPartMsat, null, _graphPathSource?.CreateContext(0));
        return _planner.TryPlan(request, out _, out _);
    }

    /// <summary>
    /// A trampoline node for a payment to <paramref name="payee"/>: a peer with a usable channel whose features set
    /// <c>trampoline_routing</c> (the one our channels can send the most to first), else a graph node announcing it;
    /// null when none is known.
    /// </summary>
    internal async Task<CompactPubKey?> SelectTrampolineNodeAsync(CompactPubKey payee,
                                                                  CancellationToken cancellationToken)
    {
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var channels = await GetUsableChannelsAsync(cancellationToken);
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var probe = CreateLiquidityProbe(channels, height);
        var peerManager = ResolvePeerManager();
        if (peerManager is not null)
        {
            foreach (var peer in channels.GroupBy(c => c.RemoteNodeId)
                                         .OrderByDescending(g => g.Sum(c => (decimal)probe(c.ChannelId, [])))
                                         .Select(g => g.Key))
            {
                if (peer != payee && peer != ourNodeId && PeerAdvertisesTrampoline(peerManager, peer))
                    return peer;
            }
        }

        if (_graphPathSource?.GetGraph() is { } graph)
        {
            foreach (var node in graph.Nodes)
            {
                if (node.NodeId == ourNodeId || node.NodeId == payee || node.Features.IsEmpty)
                    continue;

                var features = FeatureSet.DeserializeFromBytes(node.Features.ToArray());
                if (features.IsFeatureSet(Feature.OptionTrampolineRouting))
                    return node.NodeId;
            }
        }

        return null;
    }

    private bool PeerAdvertisesTrampoline(IPeerManager peerManager, CompactPubKey peerId)
    {
        try
        {
            return peerManager.GetPeer(peerId)?.Features.IsFeatureSet(Feature.OptionTrampolineRouting) ?? false;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            _logger.LogDebug(e, "Could not read the features of peer {Peer}", peerId);
            return false;
        }
    }

    /// <summary>
    /// Whether the outer leg to <paramref name="trampolineNode"/> may be split (NL-924): <c>basic_mpp</c> in its
    /// <c>init</c> features when it is a connected peer, else in its <c>node_announcement</c> in the graph; when neither
    /// is known, true (PR 836: a trampoline node collects every part of the outer onion before it relays).
    /// </summary>
    internal bool TrampolineAcceptsMpp(CompactPubKey trampolineNode)
    {
        if (ResolvePeerManager() is { } peerManager)
        {
            try
            {
                if (peerManager.GetPeer(trampolineNode) is { } peer)
                    return peer.Features.IsFeatureSet(Feature.BasicMpp);
            }
            catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
            {
                _logger.LogDebug(e, "Could not read the features of peer {Peer}", trampolineNode);
            }
        }

        if (_graphPathSource?.GetGraph() is { } graph && graph.TryGetNode(trampolineNode, out var node)
                                                     && !node.Features.IsEmpty)
            return FeatureSet.DeserializeFromBytes(node.Features.ToArray()).IsFeatureSet(Feature.BasicMpp);

        return true;
    }

    private IPeerManager? ResolvePeerManager()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            return scope.ServiceProvider.GetService<IPeerManager>();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(e, "Could not resolve the peer manager");
            return null;
        }
    }

    /// <summary>
    /// The session of a payment through <paramref name="trampolineNode"/> to <paramref name="recipient"/>: the rounds
    /// route to the trampoline node (MPP allowed, no shadow offset), the stored row names
    /// <paramref name="payeeNodeId"/>, and the first trampoline attempt is numbered after the attempts already stored
    /// for the hash.
    /// </summary>
    private async Task<PaymentSession> CreateTrampolineSessionAsync(
        CompactPubKey trampolineNode, TrampolineRecipient recipient, Hash paymentHash, CompactPubKey payeeNodeId,
        string? invoice, LightningMoney maxFee, int maxParts, DateTimeOffset? deadline, DateTimeOffset now,
        PayInvoiceOptions options, Bolt12PaymentDetails? bolt12)
    {
        if (_trampolineOnionFactory is null || _trampolineFailureOnionService is null)
            throw new InvalidOperationException("No trampoline onion service is registered.");

        var sendOptions = _sendOptions.Value;
        var policy = GetCachedTrampolinePolicy(trampolineNode)
                  ?? new TrampolinePolicy(sendOptions.TrampolineFeeBaseMsat,
                                          sendOptions.TrampolineFeeProportionalMillionths,
                                          sendOptions.TrampolineCltvExpiryDelta);
        var state = new TrampolinePayerState(trampolineNode, recipient, policy,
                                             await GetNextTrampolineAttemptAsync(paymentHash));
        var target = new PaymentTarget(trampolineNode, paymentHash, new Secret(RandomNumberGenerator.GetBytes(32)),
                                       recipient.Amount, 0, [], SupportsMpp: TrampolineAcceptsMpp(trampolineNode));
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Paying {PaymentHash} to {Payee} through the trampoline node {Trampoline}",
                                   paymentHash, payeeNodeId, trampolineNode);

        return new PaymentSession(target, invoice, recipient.Amount, maxFee, maxParts,
                                  Math.Max(1, sendOptions.MaxAttempts), deadline, now)
        {
            Trampoline = state,
            RecordedPayeeNodeId = payeeNodeId,
            ShadowCltvOffset = 0,
            OutgoingChannelId = options.OutgoingChannelId,
            Bolt12 = bolt12,
            Labels = options.Labels
        };
    }

    /// <summary>The trampoline recipient of a blinded payment (see the class remarks).</summary>
    private static TrampolineRecipient CreateBlindedTrampolineRecipient(PayBlindedRequest request,
                                                                        CompactPubKey trampolineNode,
                                                                        CompactPubKey ourNodeId)
    {
        if (request.RecipientFeatures?.IsFeatureSet(Feature.OptionTrampolineRouting) ?? false)
        {
            // The recipient supports trampoline: its blinded hops become trampoline hops (the cheapest usable path
            // that does not start at the trampoline node or at us)
            var path = request.Paths.Where(p => p.Path.FirstNodeId != trampolineNode && p.Path.FirstNodeId != ourNodeId
                                                                                     && BlindedRouteComposer
                                                                                       .CheckUsable(p, request.Amount)
                                                                                     is null)
                              .OrderBy(p => p.PayInfo.ComputeFeeMsat(request.Amount.MilliSatoshi))
                              .FirstOrDefault();
            if (path is not null)
                return new BlindedTrampolineRecipient(request.Amount, path);
        }

        return new BlindedPathsTrampolineRecipient(request.Amount,
                                                   request.Paths.Select(WireBlindedPaymentPath.FromBlindedPaymentPath)
                                                          .ToList(), request.RecipientFeatures);
    }

    /// <summary>The attempt number after the last one stored for the hash (0 when none).</summary>
    private async Task<int> GetNextTrampolineAttemptAsync(Hash paymentHash)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var hops = await GetTrampolineHopsAsync(scope, paymentHash);
        return hops.Count == 0 ? 0 : hops.Max(h => h.Attempt) + 1;
    }

    /// <summary>
    /// Whether a blinded payment would find a route of its own (a first round, nothing sent), for
    /// <see cref="TrampolinePaymentMode.Auto"/>.
    /// </summary>
    private async Task<bool> HasOwnBlindedRouteAsync(PaymentSession session, IReadOnlyList<BlindedPaymentPath> paths,
                                                     CancellationToken cancellationToken)
    {
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var channels = await GetUsableChannelsAsync(cancellationToken);
        return TryPlanBlinded(session, paths, session.Amount.MilliSatoshi, session.MaxFee.MilliSatoshi,
                              session.MaxParts, height, channels, _graphPathSource?.CreateContext(0), out _, out _);
    }

    /// <summary>
    /// The trampoline node a blinded payment goes through, or null (see the class remarks; a blinded recipient
    /// without <c>trampoline_routing</c> is still paid through one, with <c>recipient_blinded_paths</c>).
    /// </summary>
    private async Task<CompactPubKey?> ResolveBlindedTrampolineNodeAsync(PayInvoiceOptions options,
                                                                         PaymentSession plain,
                                                                         IReadOnlyList<BlindedPaymentPath> paths,
                                                                         CancellationToken cancellationToken)
    {
        if (options.TrampolineNode is { } named)
        {
            if (named == _secureKeyManager.GetNodePubKey())
                throw new ArgumentException("The trampoline node cannot be this node.", nameof(options));
            return named;
        }

        return _sendOptions.Value.Trampoline switch
        {
            TrampolinePaymentMode.Always => await SelectTrampolineNodeAsync(plain.PayeeNodeId, cancellationToken),
            TrampolinePaymentMode.Auto when !await HasOwnBlindedRouteAsync(plain, paths, cancellationToken) =>
                await SelectTrampolineNodeAsync(plain.PayeeNodeId, cancellationToken),
            _ => null
        };
    }
}