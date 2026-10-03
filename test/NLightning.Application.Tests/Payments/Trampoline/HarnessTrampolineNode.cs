using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Send;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;
using Send.Harness;

/// <summary>
/// A trampoline stand-in for one <see cref="PaymentHarnessNode"/> (NL-875, TR4 proofs; the production relay engine is
/// lane TR3 and the target lane TR2): it reads every incoming HTLC whose outer final payload carries a
/// <c>trampoline_onion_packet</c>, peels the trampoline layer with the node key and either relays the payment through
/// the node's own <see cref="ITrampolineLegSender"/> (the production <see cref="PaymentService"/>) or receives it as
/// the final trampoline recipient against the node's invoices. It is the leg's <see cref="ITrampolineLegObserver"/>:
/// a successful leg fulfills every incoming part, a failed one fails them, re-wrapping a downstream error with both of
/// the node's layers. With <see cref="UseAttribution"/> its failures carry <c>attribution_data</c> for the outer layer,
/// built as the production relay and target build it (<see cref="TrampolineErrorPackets"/>, NL-898).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessTrampolineNode : ITrampolineLegObserver
{
    private readonly PaymentHarnessNode _node;
    private readonly ISphinxService _sphinx;
    private readonly IHopPayloadSerializer _serializer;
    private readonly ITrampolineOnionService _trampolineOnion;
    private readonly ITrampolineFailureOnionService _trampolineFailure;
    private readonly IFailureOnionService _failureOnion;
    private readonly IAttributionDataService _attribution;
    private readonly IChannelOperations _operations;
    private readonly Dictionary<Hash, List<IncomingPart>> _sets = [];
    private readonly Lock _lock = new();

    public HarnessTrampolineNode(PaymentHarnessNode node)
    {
        _node = node;
        _sphinx = node.Services.GetRequiredService<ISphinxService>();
        _serializer = node.Services.GetRequiredService<IHopPayloadSerializer>();
        _trampolineOnion = node.Services.GetRequiredService<ITrampolineOnionService>();
        _trampolineFailure = node.Services.GetRequiredService<ITrampolineFailureOnionService>();
        _failureOnion = node.Services.GetRequiredService<IFailureOnionService>();
        _attribution = node.Services.GetRequiredService<IAttributionDataService>();
        _operations = node.Operations;
        node.Switch.IncomingInterceptor = HandleAsync;
        PaymentService.LegObserver = this;
    }

    /// <summary>The node's payment service, the leg sender.</summary>
    public PaymentService PaymentService => (PaymentService)_node.PaymentService;

    /// <summary>The policy the node relays at (its fee and CLTV delta).</summary>
    public TrampolinePolicy Policy { get; set; } = new(1_000, 1_000, 144);

    /// <summary>When set, called with the relay's attempt number (0 first) for a complete set it would relay; a
    /// non-null failure fails every part with it instead (created with both of the node's layers).</summary>
    public Func<int, FailureMessage?>? RefuseRelay { get; set; }

    /// <summary>When set, the final recipient fails every part with it instead of fulfilling.</summary>
    public FailureMessage? RecipientFailure { get; set; }

    /// <summary>When set, the final recipient fails every part with it on its outer layer only (an error the previous
    /// trampoline node, not the origin, can read).</summary>
    public FailureMessage? OuterFailure { get; set; }

    /// <summary>When set, every trampoline failure this node sends (its own, or a downstream one re-wrapped) carries
    /// <c>attribution_data</c> for the outer layer with its hold time (<see cref="TrampolineErrorPackets"/>).</summary>
    public bool UseAttribution { get; set; }

    /// <summary>Changes an attributed failure after it was built (a node that garbles its attribution).</summary>
    public Func<AttributedErrorPacket, AttributedErrorPacket>? TamperAttribution { get; set; }

    /// <summary>The hold times this node put in its attributed failures.</summary>
    public ConcurrentQueue<uint> ReportedHoldTimes { get; } = new();

    /// <summary>The relay's CLTV delta for the leg's first-hop cap; the default is the policy's.</summary>
    public uint? LegCltvMargin { get; set; }

    /// <summary>The legs this node started, in order.</summary>
    public ConcurrentQueue<TrampolineLegRequest> Legs { get; } = new();

    /// <summary>Every part this node received with a trampoline onion: (outer amount, outer CLTV, outer payload,
    /// trampoline payload, final).</summary>
    public ConcurrentQueue<ReceivedPart> Received { get; } = new();

    /// <summary>The leg ends the observer was told of.</summary>
    public ConcurrentQueue<LegOutcome> Outcomes { get; } = new();

    private int _relayAttempts;

    private async Task<bool> HandleAsync(ChannelId channelId, HtlcRecord htlc, CancellationToken cancellationToken)
    {
        var packet = new OnionPacket(htlc.OnionRoutingPacket.Span);
        PeeledOnion outer;
        try
        {
            outer = _sphinx.PeelAsLocalNode(packet, htlc.PaymentHash);
        }
        catch (Exception)
        {
            return false;
        }

        if (!outer.IsFinal)
            return false;

        var outerPayload = await _serializer.DeserializeAsync(outer.Payload);
        if (outerPayload.TrampolineOnionPacket is not { } trampolinePacket)
            return false;

        var inner = _trampolineOnion.Peel(trampolinePacket.ToBytes(), htlc.PaymentHash, outerPayload.CurrentPathKey);
        var innerPayload = await _serializer.DeserializeAsync(inner.Payload);
        var part = new IncomingPart(channelId, htlc.Id, htlc.AmountMsat, htlc.CltvExpiry, outer.SharedSecret,
                                    inner.SharedSecret, outerPayload.PaymentData?.TotalMsat.MilliSatoshi
                                                     ?? htlc.AmountMsat, innerPayload, inner.NextPacket);
        // A last trampoline layer with recipient_blinded_paths is a relay to a recipient without trampoline support
        var isRecipient = inner.IsFinal && innerPayload.RecipientBlindedPaths is null;
        Received.Enqueue(new ReceivedPart(htlc.AmountMsat, htlc.CltvExpiry, outerPayload, innerPayload,
                                          isRecipient));

        List<IncomingPart> complete;
        lock (_lock)
        {
            if (!_sets.TryGetValue(htlc.PaymentHash, out var set))
                _sets[htlc.PaymentHash] = set = [];
            set.Add(part);
            var target = isRecipient
                             ? innerPayload.PaymentData?.TotalMsat.MilliSatoshi ?? part.OuterTotalMsat
                             : part.OuterTotalMsat;
            if (set.Aggregate(0UL, (sum, p) => sum + p.AmountMsat) < target)
                return true;

            complete = [.. set];
            _sets.Remove(htlc.PaymentHash);
        }

        if (isRecipient)
            await ReceiveAsync(htlc.PaymentHash, complete, cancellationToken);
        else
            await RelayAsync(htlc.PaymentHash, complete, cancellationToken);
        return true;
    }

    private async Task ReceiveAsync(Hash paymentHash, List<IncomingPart> parts, CancellationToken cancellationToken)
    {
        if (OuterFailure is { } outerFailure)
        {
            foreach (var part in parts)
                await _operations.FailHtlcAsync(part.ChannelId, part.HtlcId,
                                                _failureOnion.CreateErrorPacket(part.OuterSecret, outerFailure),
                                                cancellationToken);
            return;
        }

        var invoice = await _node.Invoices.GetByPaymentHashAsync(paymentHash);
        var failure = RecipientFailure;
        if (invoice is null || parts[0].Inner.PaymentData?.PaymentSecret != invoice.PaymentSecret)
            failure ??= FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(parts[0].AmountMsat),
                                                                        PaymentHarness.BlockHeight);
        if (parts.Any(p => p.CltvExpiry < p.Inner.OutgoingCltvValue))
            failure ??= FailureMessage.FinalIncorrectCltvExpiry(parts[0].CltvExpiry);

        if (failure is not null)
        {
            foreach (var part in parts)
                await FailTrampolinePartAsync(part, failure, null, cancellationToken);
            return;
        }

        invoice!.Accept(LightningMoney.MilliSatoshis(parts.Aggregate(0UL, (sum, p) => sum + p.AmountMsat)));
        await _node.Invoices.UpdateAsync(invoice);
        foreach (var part in parts)
            await _operations.FulfillHtlcAsync(part.ChannelId, part.HtlcId, invoice.Preimage, cancellationToken);
    }

    private async Task RelayAsync(Hash paymentHash, List<IncomingPart> parts, CancellationToken cancellationToken)
    {
        var attempt = _relayAttempts++;
        var inner = parts[0].Inner;
        if (inner.AmtToForward is null || inner.OutgoingCltvValue is null)
        {
            // A blinded trampoline hop: the stand-in does not relay those (the relay engine is lane TR3)
            await FailPartsAsync(parts, FailureMessage.TemporaryTrampolineFailure(), cancellationToken);
            return;
        }

        var amountOut = inner.AmtToForward!;
        var cltvOut = inner.OutgoingCltvValue!.Value;
        var amountIn = parts.Aggregate(0UL, (sum, p) => sum + p.AmountMsat);
        var minCltvIn = parts.Min(p => p.CltvExpiry);
        var fee = Policy.FeeMsat(amountOut.MilliSatoshi);

        var refusal = RefuseRelay?.Invoke(attempt);
        if (refusal is null && (amountIn < amountOut.MilliSatoshi + fee || minCltvIn < cltvOut + Policy.CltvExpiryDelta))
            refusal = FailureMessage.TrampolineFeeOrExpiryInsufficient(Policy.FeeBaseMsat,
                                                                       Policy.FeeProportionalMillionths,
                                                                       Policy.CltvExpiryDelta);
        if (refusal is not null)
        {
            await FailPartsAsync(parts, refusal, cancellationToken);
            return;
        }

        lock (_lock)
            _relays[paymentHash] = parts;

        var request = new TrampolineLegRequest(paymentHash, amountOut, cltvOut,
                                               minCltvIn - (LegCltvMargin ?? Policy.CltvExpiryDelta),
                                               LightningMoney.MilliSatoshis(amountIn - amountOut.MilliSatoshi - fee),
                                               inner.OutgoingNodeId, inner.OutgoingNodeId is null
                                                                         ? null
                                                                         : parts[0].NextPacket!.Value.ToBytes(),
                                               null, inner.RecipientBlindedPaths, inner.RecipientFeatures?.GetFeatureSet(),
                                               true, _node.Clock.GetUtcNow().AddMinutes(5));
        Legs.Enqueue(request);
        await PaymentService.StartAsync(request, cancellationToken);
    }

    private readonly Dictionary<Hash, List<IncomingPart>> _relays = [];

    private async Task FailPartsAsync(List<IncomingPart> parts, FailureMessage failure,
                                      CancellationToken cancellationToken)
    {
        foreach (var part in parts)
            await FailTrampolinePartAsync(part, failure, null, cancellationToken);
    }

    /// <summary>Fails a part with both of the node's layers: <paramref name="failure"/> created here, or
    /// <paramref name="downstream"/> (an unwrapped downstream trampoline error) re-wrapped.</summary>
    private async Task FailTrampolinePartAsync(IncomingPart part, FailureMessage? failure, byte[]? downstream,
                                               CancellationToken cancellationToken)
    {
        if (UseAttribution)
        {
            var holdTime = await _operations.GetHoldTimeAsync(part.ChannelId, part.HtlcId, cancellationToken);
            ReportedHoldTimes.Enqueue(holdTime);
            var packet = failure is not null
                             ? TrampolineErrorPackets.CreateAttributed(_failureOnion, _attribution,
                                                                       part.TrampolineSecret, part.OuterSecret,
                                                                       failure, holdTime)
                             : TrampolineErrorPackets.WrapAttributed(_failureOnion, _attribution,
                                                                     part.TrampolineSecret, part.OuterSecret,
                                                                     downstream!, holdTime);
            await _operations.FailHtlcAsync(part.ChannelId, part.HtlcId, TamperAttribution?.Invoke(packet) ?? packet,
                                            cancellationToken);
            return;
        }

        var reason = failure is not null
                         ? _trampolineFailure.CreateTrampolineErrorPacket(part.TrampolineSecret, part.OuterSecret,
                                                                          failure)
                         : _trampolineFailure.WrapTrampolineErrorPacket(part.TrampolineSecret, part.OuterSecret,
                                                                        downstream!);
        await _operations.FailHtlcAsync(part.ChannelId, part.HtlcId, reason, cancellationToken);
    }

    public async Task OnLegSucceededAsync(Hash paymentHash, Secret preimage, LightningMoney totalSent,
                                          CancellationToken cancellationToken)
    {
        Outcomes.Enqueue(new LegOutcome(paymentHash, preimage, null));
        List<IncomingPart>? parts;
        lock (_lock)
            _relays.Remove(paymentHash, out parts);
        foreach (var part in parts ?? [])
            await _operations.FulfillHtlcAsync(part.ChannelId, part.HtlcId, preimage, cancellationToken);
    }

    public async Task OnLegFailedAsync(Hash paymentHash, TrampolineLegFailure failure,
                                       CancellationToken cancellationToken)
    {
        Outcomes.Enqueue(new LegOutcome(paymentHash, null, failure));
        List<IncomingPart>? parts;
        lock (_lock)
            _relays.Remove(paymentHash, out parts);
        foreach (var part in parts ?? [])
        {
            if (failure is
                {
                    Kind: TrampolineLegFailureKind.DownstreamTrampolineError,
                    DownstreamPacketToRewrap: { } downstream
                })
                await FailTrampolinePartAsync(part, null, downstream, cancellationToken);
            else
                await FailTrampolinePartAsync(part, FailureMessage.TemporaryTrampolineFailure(), null,
                                              cancellationToken);
        }
    }

    private sealed record IncomingPart(
        ChannelId ChannelId,
        ulong HtlcId,
        ulong AmountMsat,
        uint CltvExpiry,
        Secret OuterSecret,
        Secret TrampolineSecret,
        ulong OuterTotalMsat,
        HopPayload Inner,
        OnionPacket? NextPacket);
}

/// <summary>A part a <see cref="HarnessTrampolineNode"/> received.</summary>
internal sealed record ReceivedPart(
    ulong AmountMsat,
    uint CltvExpiry,
    HopPayload Outer,
    HopPayload Inner,
    bool IsFinal);

/// <summary>A leg end a <see cref="HarnessTrampolineNode"/> was told of.</summary>
internal sealed record LegOutcome(Hash PaymentHash, Secret? Preimage, TrampolineLegFailure? Failure);