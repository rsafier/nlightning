namespace NLightning.Application.Payments.Switch;

using System.Security.Cryptography;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The hold-invoice half of the switch (NL-995, Cashu plan C4): a completed set of a hold invoice (one whose row has
/// no preimage) is <see cref="InvoiceStatus.Held">held</see> — locked in, nothing fulfilled or failed — until the
/// operator settles with the outside preimage (<see cref="IHoldInvoiceService.SettleHoldInvoiceAsync"/>, the NUT-14 /
/// ASP seam) or cancels (<see cref="IHoldInvoiceService.CancelHoldInvoiceAsync"/>), which fails the parts back.
/// </summary>
/// <remarks>
/// The CLTV guard is the deadline monitor's own fail-back: a held part carries no <c>KnownPreimage</c> and its
/// invoice is not <c>Settled</c>, so it is classified <c>UnresolvedFinalHop</c> and failed back
/// <c>HtlcDeadlinePolicy.FulfillSafetyBlocks</c> (18) before its expiry — and the removal of any held part cancels
/// the whole hold (<see cref="HandleIncomingSettledForHoldAsync"/>). After a restart the replayed lock-ins rebuild
/// the set and re-hold it (the row is already <c>Held</c>, so the transition is idempotent).
/// </remarks>
public sealed partial class HtlcSwitch : IHoldInvoiceService
{
    /// <summary>
    /// The branch <see cref="ReceiveAsync"/> takes for a completed set of a hold invoice: hold it instead of
    /// fulfilling. Called under the payment hash lock.
    /// </summary>
    private async Task HoldSetAsync(HtlcSet set, InvoiceModel invoice)
    {
        set.Timer?.Dispose();
        set.Timer = null;

        if (invoice.Status == InvoiceStatus.Open)
        {
            invoice.Hold(set.PartsSum);
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
                await unitOfWork.SaveChangesAsync();
            }

            _paymentEventPublisher?.Publish(new InvoiceHeldEvent(set.PaymentHash, set.PartsSum,
                                                                 _timeProvider.GetUtcNow()));
            _logger.LogInformation(
                "Holding the completed set of hold invoice {PaymentHash}: {Parts} part(s), {AmountMsat} msat, "
              + "waiting for the operator's settle or cancel", set.PaymentHash, set.Parts.Count,
                set.PartsSum.MilliSatoshi);
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            // A restart's replayed lock-ins rebuilt an already-held set
            _logger.LogInformation("Hold invoice {PaymentHash} is held with {Parts} rebuilt part(s)",
                                   set.PaymentHash, set.Parts.Count);
        }
    }

    /// <inheritdoc />
    public async Task<InvoiceModel> SettleHoldInvoiceAsync(Hash paymentHash, Secret preimage,
                                                           CancellationToken cancellationToken = default)
    {
        if (!SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((ReadOnlySpan<byte>)paymentHash))
            throw new ArgumentException("The preimage does not hash to the invoice's payment hash.",
                                        nameof(preimage));

        using var paymentHashLock = await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken);
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash)
                           ?? throw new ArgumentException($"No invoice for payment hash {paymentHash}.",
                                                          nameof(paymentHash));
            if (invoice.Status != InvoiceStatus.Held)
                throw new InvalidOperationException($"The hold invoice is {invoice.Status}, not Held.");

            if (!_htlcSets.TryGetValue(paymentHash, out var set))
                throw new InvalidOperationException(
                    $"The held set of {paymentHash} has no live parts (they were failed back or removed); "
                  + "cancel it instead.");

            set.Prune(IsPartWaiting);
            if (!set.IsComplete)
            {
                // The guard (or a channel failure) took parts while the operator decided: the hold is gone
                await CancelHeldSetAsync(unitOfWork, invoice, set, "its parts were failed back", cancellationToken);
                throw new InvalidOperationException(
                    $"The held set of {paymentHash} lost parts (failed back near their deadline or with their "
                  + "channel); the invoice was canceled.");
            }

            var amount = set.PartsSum;
            invoice.SettleHeld(preimage, _timeProvider.GetUtcNow());
            await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
            var selfPayment = await IsOurOwnPaymentAsync(unitOfWork, invoice);
            var firstChannelId = set.Parts[0].ChannelId;
            PaymentAccountingEvents.TryStage(unitOfWork, () =>
            {
                _channelMemoryRepository.TryGetChannel(firstChannelId, out var channel);
                return PaymentAccountingEvents.InvoiceSettled(invoice, amount, firstChannelId, channel,
                                                              set.Parts.Count, IsOnchain(firstChannelId),
                                                              CurrentHeight, selfPayment);
            }, _logger);
            await unitOfWork.SaveChangesAsync();

            // The commit point was the settle save; fulfill the parts (a refused fulfill replays from its mark)
            foreach (var part in set.Parts.ToList())
            {
                await MarkPartAsync(part.ChannelId, part.HtlcId, preimage, null, cancellationToken);
                try
                {
                    await FulfillFinalAsync(part.ChannelId, part.HtlcId, preimage, part.SharedSecret, null,
                                            cancellationToken);
                }
                catch (CommitmentRefusedException)
                {
                    // Still marked: its replay fulfills it (as a settled set member)
                }
            }

            RemoveHtlcSet(set);
            PublishSettled(paymentHash, amount);
            _logger.LogInformation("Settled hold invoice {PaymentHash} for {AmountMsat} msat over {Parts} part(s)",
                                   paymentHash, amount.MilliSatoshi, set.Parts.Count);
            return invoice;
        }
    }

    /// <inheritdoc />
    public async Task<InvoiceModel> CancelHoldInvoiceAsync(Hash paymentHash,
                                                           CancellationToken cancellationToken = default)
    {

        using var paymentHashLock = await _paymentHashLocks.AcquireAsync(paymentHash, cancellationToken);
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash)
                   ?? throw new ArgumentException($"No invoice for payment hash {paymentHash}.",
                                                  nameof(paymentHash));
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Held))
            throw new InvalidOperationException($"The invoice is {invoice.Status}; only an open or held one can be "
                                              + "canceled.");

        if (invoice.Status == InvoiceStatus.Held && _htlcSets.TryGetValue(paymentHash, out var set))
            await CancelHeldSetAsync(unitOfWork, invoice, set, "the operator canceled it", cancellationToken);
        else
        {
            invoice.Cancel();
            await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
            await unitOfWork.SaveChangesAsync();
        }

        _logger.LogInformation("Canceled hold invoice {PaymentHash}", paymentHash);
        return invoice;
    }

    /// <summary>
    /// A held set's part was removed (the deadline monitor's fail-back near its CLTV, or its channel failed or
    /// closed): the hold can no longer settle, so the invoice is canceled and every remaining part failed back —
    /// the CLTV guard's cascade. Called under the payment hash lock.
    /// </summary>
    private async Task HandleIncomingSettledForHoldAsync(IncomingHtlcSettled settled,
                                                         CancellationToken cancellationToken)
    {
        if (!_htlcSets.TryGetValue(settled.PaymentHash, out var set))
            return;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(settled.PaymentHash);
        if (invoice is not { Status: InvoiceStatus.Held })
            return;

        _logger.LogWarning("A part of the held set of {PaymentHash} was removed ({Kind}): canceling the hold",
                           settled.PaymentHash, settled.Kind);
        await CancelHeldSetAsync(unitOfWork, invoice, set, $"a part was removed ({settled.Kind})",
                                 cancellationToken);
    }

    private async Task CancelHeldSetAsync(IUnitOfWork unitOfWork, InvoiceModel invoice, HtlcSet set, string reason,
                                          CancellationToken cancellationToken)
    {
        var height = CurrentHeight;
        foreach (var part in set.Parts.ToList())
        {
            if (!IsPartWaiting(part))
                continue;

            await FailPartAsync(part, FailureMessage.IncorrectOrUnknownPaymentDetails(part.HtlcAmount, height),
                                cancellationToken);
        }

        invoice.Cancel();
        await unitOfWork.InvoiceDbRepository.UpdateAsync(invoice);
        await unitOfWork.SaveChangesAsync();
        RemoveHtlcSet(set);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Hold invoice {PaymentHash} canceled: {Reason}", invoice.PaymentHash, reason);
    }
}