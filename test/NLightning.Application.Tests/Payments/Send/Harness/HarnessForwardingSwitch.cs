using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Send.Harness;

using Application.Payments.FinalHop;
using Application.Payments.Onion;
using Application.Payments.Send.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interfaces;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// A minimal forwarding switch for <see cref="PaymentHarness"/>, standing in for the W2-B <c>HtlcSwitch</c>: it runs
/// the production payment core (<see cref="IncomingOnionProcessor"/>, <see cref="FinalHopProcessor"/>) and the
/// production channel operations, keeps forward circuits in memory, and calls <see cref="IPaymentOutcomeHandler"/>
/// for outgoing HTLCs without a circuit (our own payments), the hook the W2-B switch must call.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessForwardingSwitch(
    IncomingOnionProcessor onionProcessor,
    FinalHopProcessor finalHopProcessor,
    IChannelOperations channelOperations,
    IChannelMemoryRepository channelMemoryRepository,
    IFailureOnionService failureOnionService,
    IPaymentOutcomeHandler paymentOutcomeHandler,
    IBlockchainMonitor blockchainMonitor,
    IServiceScopeFactory serviceScopeFactory) : IHtlcSwitch
{
    private readonly ConcurrentDictionary<(ChannelId, ulong), Circuit> _circuits = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _handledIncoming = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _resolvedOutgoing = new();
    private readonly Dictionary<Hash, List<(ChannelId ChannelId, ulong HtlcId, ulong AmountMsat)>> _parts = [];

    /// <summary>Every event handed to the switch, in order.</summary>
    public ConcurrentQueue<IChannelDomainEvent> Events { get; } = new();

    /// <summary>Outcomes <see cref="IPaymentOutcomeHandler"/> reported as ours (true) or not (false).</summary>
    public ConcurrentQueue<(IChannelDomainEvent Event, bool Handled)> PaymentOutcomes { get; } = new();

    /// <summary>When set, every HTLC to forward is failed with <c>unknown_next_peer</c> instead.</summary>
    public bool FailEveryForward { get; set; }

    /// <summary>
    /// When set, called for every HTLC to forward (with its forward instruction); a non-null failure fails it back
    /// instead (a stand-in for a hop's forwarding policy, e.g. <c>fee_insufficient</c> with a new channel_update).
    /// </summary>
    public Func<HtlcRecord, IncomingOnionForward, FailureMessage?>? ForwardInterceptor { get; set; }

    /// <summary>The forwards this node made or refused, in order: (incoming amount, forward instruction).</summary>
    public ConcurrentQueue<(ulong IncomingAmountMsat, IncomingOnionForward Forward)> Forwards { get; } = new();

    /// <summary>The final-hop HTLCs this node received, in order: (amount, <c>total_msat</c>).</summary>
    public ConcurrentQueue<(ulong AmountMsat, ulong TotalMsat)> Received { get; } = new();

    public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
    {
        Events.Enqueue(channelEvent);
        switch (channelEvent)
        {
            case IncomingHtlcLockedIn lockedIn when _handledIncoming.TryAdd((lockedIn.ChannelId, lockedIn.HtlcId), 0):
                await HandleIncomingAsync(lockedIn.ChannelId, lockedIn.Htlc, cancellationToken);
                break;
            case OutgoingHtlcFulfilled fulfilled:
                if (_circuits.TryGetValue((fulfilled.ChannelId, fulfilled.HtlcId), out var fulfilledCircuit))
                {
                    if (_resolvedOutgoing.TryAdd((fulfilled.ChannelId, fulfilled.HtlcId), 0))
                        await channelOperations.FulfillHtlcAsync(fulfilledCircuit.IncomingChannelId,
                                                                 fulfilledCircuit.IncomingHtlcId,
                                                                 fulfilled.PaymentPreimage, cancellationToken);
                }
                else
                {
                    PaymentOutcomes.Enqueue((fulfilled,
                                             await paymentOutcomeHandler.HandleOutgoingHtlcFulfilledAsync(
                                                 fulfilled, cancellationToken)));
                }

                break;
            case OutgoingHtlcFailed failed:
                if (_circuits.TryGetValue((failed.ChannelId, failed.HtlcId), out var failedCircuit))
                {
                    if (_resolvedOutgoing.TryAdd((failed.ChannelId, failed.HtlcId), 0))
                        await FailUpstreamAsync(failedCircuit, failed.Removal, cancellationToken);
                }
                else
                {
                    PaymentOutcomes.Enqueue((failed,
                                             await paymentOutcomeHandler.HandleOutgoingHtlcFailedAsync(
                                                 failed, cancellationToken)));
                }

                break;
        }
    }

    private async Task HandleIncomingAsync(ChannelId channelId, HtlcRecord htlc, CancellationToken cancellationToken)
    {
        var result = await onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash);
        if (result.SharedSecretOrNull is { } secret)
            await channelOperations.RecordOnionSecretAsync(channelId, htlc.Id, secret, cancellationToken);

        switch (result)
        {
            case IncomingOnionForward forward:
                Forwards.Enqueue((htlc.AmountMsat, forward));
                if (ForwardInterceptor?.Invoke(htlc, forward) is { } intercepted)
                {
                    await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                          failureOnionService.CreateErrorPacket(
                                                              forward.SharedSecret, intercepted), cancellationToken);
                    return;
                }

                var outgoing = channelMemoryRepository
                              .FindChannels(c => c.State == ChannelState.Open
                                              && c.ShortChannelId == forward.OutgoingShortChannelId)
                              .SingleOrDefault();
                if (outgoing is null || FailEveryForward)
                {
                    var failure = FailureMessage.UnknownNextPeer();
                    await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                          failureOnionService.CreateErrorPacket(
                                                              forward.SharedSecret, failure), cancellationToken);
                    return;
                }

                var outgoingId = await channelOperations.OfferHtlcAsync(
                                     outgoing.ChannelId, forward.AmountToForward, htlc.PaymentHash,
                                     forward.OutgoingCltvValue, forward.NextPacket, null,
                                     HtlcOrigin.Forwarded(channelId, htlc.Id), cancellationToken);
                _circuits[(outgoing.ChannelId, outgoingId)] = new Circuit(channelId, htlc.Id, forward.SharedSecret);
                return;

            case IncomingOnionFinal final:
                Received.Enqueue((htlc.AmountMsat, final.Payload.PaymentData?.TotalMsat.MilliSatoshi ?? 0));
                if (final.Payload.PaymentData is { } paymentData
                 && paymentData.TotalMsat.MilliSatoshi != htlc.AmountMsat)
                {
                    await ReceivePartAsync(channelId, htlc, final, paymentData.TotalMsat.MilliSatoshi,
                                           cancellationToken);
                    return;
                }

                using (var scope = serviceScopeFactory.CreateScope())
                {
                    var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
                    var decision = await finalHopProcessor.ProcessAsync(invoices, htlc.PaymentHash,
                                                                        LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                                        htlc.CltvExpiry, final.Payload,
                                                                        blockchainMonitor.LastProcessedBlockHeight);
                    if (!decision.IsAccepted)
                    {
                        await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                              failureOnionService.CreateErrorPacket(
                                                                  final.SharedSecret, decision.Failure!),
                                                              cancellationToken);
                        return;
                    }

                    decision.Invoice!.Accept(decision.AmountReceived!);
                    await invoices.UpdateAsync(decision.Invoice);
                    await channelOperations.FulfillHtlcAsync(channelId, htlc.Id, decision.Preimage!.Value,
                                                             cancellationToken);
                }

                return;

            case IncomingOnionFailed onionFailed:
                await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                      failureOnionService.CreateErrorPacket(
                                                          onionFailed.SharedSecret, onionFailed.Failure),
                                                      cancellationToken);
                return;

            case IncomingOnionMalformed malformed:
                await channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id, malformed.FailureCode,
                                                               malformed.Sha256OfOnion.ToArray(), cancellationToken);
                return;
        }
    }

    /// <summary>
    /// A stand-in for a <c>basic_mpp</c> payee (our production final hop takes one HTLC per payment): holds each part
    /// until the parts of the hash reach <c>total_msat</c>, then fulfills them all with the invoice's preimage.
    /// </summary>
    private async Task ReceivePartAsync(ChannelId channelId, HtlcRecord htlc, IncomingOnionFinal final,
                                        ulong totalMsat, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
        var invoice = await invoices.GetByPaymentHashAsync(htlc.PaymentHash);
        if (invoice is null || invoice.PaymentSecret != final.Payload.PaymentData!.PaymentSecret
                            || invoice.Amount is { } amount && amount.MilliSatoshi > totalMsat)
        {
            var failure = FailureMessage.IncorrectOrUnknownPaymentDetails(
                LightningMoney.MilliSatoshis(htlc.AmountMsat), blockchainMonitor.LastProcessedBlockHeight);
            await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                  failureOnionService.CreateErrorPacket(final.SharedSecret, failure),
                                                  cancellationToken);
            return;
        }

        List<(ChannelId ChannelId, ulong HtlcId, ulong AmountMsat)> complete;
        lock (_parts)
        {
            if (!_parts.TryGetValue(htlc.PaymentHash, out var held))
                _parts[htlc.PaymentHash] = held = [];
            held.Add((channelId, htlc.Id, htlc.AmountMsat));
            if (held.Aggregate(0UL, (sum, p) => sum + p.AmountMsat) < totalMsat)
                return;

            complete = [.. held];
            _parts.Remove(htlc.PaymentHash);
        }

        invoice.Accept(LightningMoney.MilliSatoshis(complete.Aggregate(0UL, (sum, p) => sum + p.AmountMsat)));
        await invoices.UpdateAsync(invoice);
        foreach (var (partChannelId, partHtlcId, _) in complete)
            await channelOperations.FulfillHtlcAsync(partChannelId, partHtlcId, invoice.Preimage, cancellationToken);
    }

    private async Task FailUpstreamAsync(Circuit circuit, HtlcRemoval removal, CancellationToken cancellationToken)
    {
        var reason = removal.Kind == HtlcRemovalKind.FailMalformed
                         ? failureOnionService.CreateErrorPacketFromMalformed(
                             circuit.IncomingSharedSecret, (FailureCode)removal.FailureCode, removal.Sha256OfOnion.Span)
                         : failureOnionService.WrapErrorPacket(circuit.IncomingSharedSecret, removal.Reason.Span);
        await channelOperations.FailHtlcAsync(circuit.IncomingChannelId, circuit.IncomingHtlcId, reason,
                                              cancellationToken);
    }

    private sealed record Circuit(ChannelId IncomingChannelId, ulong IncomingHtlcId, Secret IncomingSharedSecret);
}