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

    /// <summary>Every event handed to the switch, in order.</summary>
    public ConcurrentQueue<IChannelDomainEvent> Events { get; } = new();

    /// <summary>Outcomes <see cref="IPaymentOutcomeHandler"/> reported as ours (true) or not (false).</summary>
    public ConcurrentQueue<(IChannelDomainEvent Event, bool Handled)> PaymentOutcomes { get; } = new();

    /// <summary>When set, every HTLC to forward is failed with <c>unknown_next_peer</c> instead.</summary>
    public bool FailEveryForward { get; set; }

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
        var result = await onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash,
                                                       new OnionReplayOwner(channelId, htlc.Id, htlc.CltvExpiry));
        if (result.SharedSecretOrNull is { } secret)
            await channelOperations.RecordOnionSecretAsync(channelId, htlc.Id, secret, cancellationToken);

        switch (result)
        {
            case IncomingOnionForward forward:
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