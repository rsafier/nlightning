using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.FinalHop;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Decides whether an HTLC whose onion ends at us pays one of our invoices (ONION M4-T3, BOLT 4 final-node rules),
/// alone or as one part of a multi-part payment (<c>basic_mpp</c>, ABCD W6-B).
/// </summary>
/// <remarks>
/// <para>Checks, first failure wins:</para>
/// <list type="number">
///   <item>HTLC <c>amount_msat</c> &lt; onion <c>amt_to_forward</c> → <c>final_incorrect_htlc_amount</c> (19) with
///   the HTLC amount.</item>
///   <item>HTLC <c>cltv_expiry</c> &lt; onion <c>outgoing_cltv_value</c> → <c>final_incorrect_cltv_expiry</c> (18)
///   with the HTLC <c>cltv_expiry</c>.</item>
///   <item>Everything about the invoice → <c>incorrect_or_unknown_payment_details</c> (PERM|15) with
///   (HTLC <c>amount_msat</c>, current height), so a probe cannot tell the cases apart: no <c>payment_data</c>;
///   unknown <c>payment_hash</c>; invoice canceled, expired, or already accepted/settled (BOLT 4 lets us treat a paid
///   hash as unknown; the one exception is below); <c>payment_secret</c> mismatch; <c>total_msat</c> !=
///   <c>amt_to_forward</c> when multi-part payments are off (BOLT 4: a node without <c>basic_mpp</c> MUST fail it);
///   amount paid (<c>total_msat</c>, BOLT 4 "Basic Multi-Part Payments") below the invoice amount or more than twice
///   it; <c>cltv_expiry</c> &lt; current height + the invoice's <c>min_final_cltv_expiry_delta</c>.</item>
/// </list>
/// <para>The first two checks run before the invoice lookup, as LND and CLN do: they detect a penultimate hop that
/// tampered with the HTLC and reveal nothing about our invoices. BOLT 4 lists 15 before 18/19 in its failure-code
/// list; since the HTLC-vs-onion errors carry no invoice information, reporting them first leaks nothing.</para>
/// <para>Multi-part (<c>acceptMultiPart</c>): an accepted HTLC is one part of the payment's HTLC set
/// (<see cref="FinalHopResult.TotalMsat"/>, <see cref="FinalHopResult.PartAmount"/>); the caller (HTLC switch) holds
/// the parts until their <c>amt_to_forward</c> reach <c>total_msat</c>.</para>
/// <para>Committed set members (<c>committedSetMember</c>, NL-322/NL-323): before the switch settles an invoice it
/// persists the preimage on every part of the complete set whose removal is not persisted with the settle
/// (<c>HtlcRecord.KnownPreimage</c> of the incoming HTLC). Such a part must be fulfilled (off chain, or claimed on
/// chain) whatever happens next (BOLT 4: "if it fulfills any HTLCs in the HTLC set: MUST fulfill the entire HTLC
/// set"), so for it the checks that depend on the time of its replay (invoice expiry, <c>cltv_expiry</c> against the
/// current height) are skipped, and a <c>Settled</c> invoice is accepted with
/// <see cref="FinalHopResult.InvoiceAlreadySettled"/> (with or without <c>basic_mpp</c>). Any other HTLC for a
/// <c>Settled</c> invoice (a duplicate, or a late part of an already paid set) is failed with
/// <c>incorrect_or_unknown_payment_details</c>, as LND does (NL-323; BOLT 4 lets a node accept a paid hash, MAY, but
/// only the payer would lose).</para>
/// <para>Stateless: <see cref="Evaluate"/> is pure; <see cref="ProcessAsync"/> only reads the invoice through the
/// caller's repository (its unit of work). Neither mutates the invoice.</para>
/// <para>Concurrency: the invoice check here is a read, not a check-and-mark. The caller (HTLC switch) runs it under a
/// per-payment-hash lock and settles the invoice in the same unit of work that stages the fulfill, re-checking that
/// it is still <c>Open</c> there.</para>
/// </remarks>
public sealed class FinalHopProcessor
{
    private readonly ILogger<FinalHopProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public FinalHopProcessor(ILogger<FinalHopProcessor> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Looks the invoice up through <paramref name="invoices"/> and evaluates the HTLC.
    /// </summary>
    /// <param name="invoices">The caller's invoice repository (its unit of work).</param>
    /// <param name="paymentHash">The HTLC's <c>payment_hash</c>.</param>
    /// <param name="htlcAmount">The HTLC's <c>amount_msat</c>.</param>
    /// <param name="htlcCltvExpiry">The HTLC's <c>cltv_expiry</c>.</param>
    /// <param name="payload">The validated final hop payload (<c>IncomingOnionFinal.Payload</c>).</param>
    /// <param name="currentBlockHeight">Our best block height.</param>
    /// <param name="acceptMultiPart">Whether we support <c>basic_mpp</c>: an HTLC may then be one part of the
    /// payment (<c>total_msat</c> &gt; <c>amt_to_forward</c>).</param>
    /// <param name="committedSetMember">The HTLC carries the invoice's preimage in its record: it is a part of a set the
    /// switch already committed to fulfill (see the class remarks).</param>
    public async Task<FinalHopResult> ProcessAsync(IInvoiceDbRepository invoices, Hash paymentHash,
                                                   LightningMoney htlcAmount, uint htlcCltvExpiry,
                                                   HopPayload payload, uint currentBlockHeight,
                                                   bool acceptMultiPart = false, bool committedSetMember = false)
    {
        ArgumentNullException.ThrowIfNull(invoices);

        var onionFailure = CheckHtlcAgainstOnion(htlcAmount, htlcCltvExpiry, payload);
        if (onionFailure is not null)
            return Log(paymentHash, onionFailure);

        var invoice = await invoices.GetByPaymentHashAsync(paymentHash);
        return Evaluate(invoice, paymentHash, htlcAmount, htlcCltvExpiry, payload, currentBlockHeight,
                        acceptMultiPart, committedSetMember);
    }

    /// <summary>
    /// Evaluates the HTLC against <paramref name="invoice"/> (null when we never issued one for the hash).
    /// </summary>
    /// <inheritdoc cref="ProcessAsync" path="/param"/>
    public FinalHopResult Evaluate(InvoiceModel? invoice, Hash paymentHash, LightningMoney htlcAmount,
                                   uint htlcCltvExpiry, HopPayload payload, uint currentBlockHeight,
                                   bool acceptMultiPart = false, bool committedSetMember = false)
    {
        ArgumentNullException.ThrowIfNull(htlcAmount);
        ArgumentNullException.ThrowIfNull(payload);

        var result = CheckHtlcAgainstOnion(htlcAmount, htlcCltvExpiry, payload)
                  ?? CheckInvoice(invoice, paymentHash, htlcAmount, htlcCltvExpiry, payload, currentBlockHeight,
                                 acceptMultiPart, committedSetMember);

        return Log(paymentHash, result);
    }

    private static FinalHopResult? CheckHtlcAgainstOnion(LightningMoney htlcAmount, uint htlcCltvExpiry,
                                                         HopPayload payload)
    {
        if (payload.AmtToForward is { } amtToForward && htlcAmount < amtToForward)
            return FinalHopResult.Fail(FailureMessage.FinalIncorrectHtlcAmount(htlcAmount),
                                       $"HTLC amount {htlcAmount.MilliSatoshi} msat is below amt_to_forward "
                                     + $"{amtToForward.MilliSatoshi} msat.");

        if (payload.OutgoingCltvValue is { } outgoingCltvValue && htlcCltvExpiry < outgoingCltvValue)
            return FinalHopResult.Fail(FailureMessage.FinalIncorrectCltvExpiry(htlcCltvExpiry),
                                       $"HTLC cltv_expiry {htlcCltvExpiry} is below outgoing_cltv_value "
                                     + $"{outgoingCltvValue}.");

        return null;
    }

    private FinalHopResult CheckInvoice(InvoiceModel? invoice, Hash paymentHash, LightningMoney htlcAmount,
                                        uint htlcCltvExpiry, HopPayload payload, uint currentBlockHeight,
                                        bool acceptMultiPart, bool committedSetMember)
    {
        FinalHopResult Unknown(string reason) =>
            FinalHopResult.Fail(FailureMessage.IncorrectOrUnknownPaymentDetails(htlcAmount, currentBlockHeight),
                                reason);

        if (payload.PaymentData is not { } paymentData)
            return Unknown("The final payload has no payment_data.");

        if (payload.AmtToForward is not { } amtToForward)
            return Unknown("The final payload has no amt_to_forward.");

        if (invoice is null || invoice.PaymentHash != paymentHash)
            return Unknown("Unknown payment hash.");

        var totalMsat = paymentData.TotalMsat;
        var isMultiPart = totalMsat.MilliSatoshi != amtToForward.MilliSatoshi;
        var alreadySettled = false;
        switch (invoice.Status)
        {
            case InvoiceStatus.Canceled:
                return Unknown("The invoice is canceled.");
            case InvoiceStatus.Settled when committedSetMember:
                // A part of the set whose settle went through while its own removal did not (checked below)
                alreadySettled = true;
                break;
            case InvoiceStatus.Settled:
                // NL-323: not a part of the settled set (a duplicate, or a late part)
                return Unknown("The invoice is already Settled and this HTLC is not a part of its set.");
            case InvoiceStatus.Accepted:
                return Unknown($"The invoice is already {invoice.Status}.");
        }

        // A committed part is fulfilled whatever the time of its replay
        if (!committedSetMember && invoice.IsExpired(_timeProvider.GetUtcNow()))
            return Unknown("The invoice is expired.");

        if (!paymentData.PaymentSecret.Equals(invoice.PaymentSecret))
            return Unknown("The payment_secret does not match.");

        // Without basic_mpp, total_msat must be exactly amt_to_forward (BOLT 4)
        if (isMultiPart && !acceptMultiPart)
            return Unknown($"total_msat {totalMsat.MilliSatoshi} differs from amt_to_forward "
                         + $"{amtToForward.MilliSatoshi} (multi-part payments are not supported).");

        if (alreadySettled && (invoice.AmountReceived is not { } received || totalMsat > received))
            return Unknown($"The invoice is already Settled for {invoice.AmountReceived?.MilliSatoshi} msat, which "
                         + $"does not cover this part's total_msat {totalMsat.MilliSatoshi}.");

        if (invoice.Amount is { } expected)
        {
            if (totalMsat < expected)
                return Unknown($"Amount paid {totalMsat.MilliSatoshi} msat is below the invoice amount "
                             + $"{expected.MilliSatoshi} msat.");

            if ((UInt128)totalMsat.MilliSatoshi > (UInt128)expected.MilliSatoshi * 2)
                return Unknown($"Amount paid {totalMsat.MilliSatoshi} msat is more than twice the invoice amount "
                             + $"{expected.MilliSatoshi} msat.");
        }

        // A committed part must be fulfilled whatever the height of its replay (BOLT 4 MUST)
        if (!committedSetMember && (ulong)htlcCltvExpiry < (ulong)currentBlockHeight + invoice.MinFinalCltvExpiry)
            return Unknown($"cltv_expiry {htlcCltvExpiry} is below height {currentBlockHeight} + "
                         + $"min_final_cltv_expiry_delta {invoice.MinFinalCltvExpiry}.");

        return FinalHopResult.Accept(invoice, htlcAmount, amtToForward, totalMsat, alreadySettled);
    }

    private FinalHopResult Log(Hash paymentHash, FinalHopResult result)
    {
        if (result.IsAccepted)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Final hop accepts the HTLC for payment hash {PaymentHash} ({AmountMsat} msat)",
                                       paymentHash, result.AmountReceived!.MilliSatoshi);
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Final hop fails the HTLC for payment hash {PaymentHash} with {FailureCode}: {Reason}",
                                   paymentHash, result.Failure!.Code, result.Reason);
        }

        return result;
    }
}