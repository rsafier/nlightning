using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.FinalHop;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Decides whether an HTLC whose onion ends at us pays one of our invoices (ONION M4-T3, BOLT 4 final-node rules,
/// no multi-part payments).
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
///   hash as unknown); <c>payment_secret</c> mismatch; <c>total_msat</c> != <c>amt_to_forward</c> (we do not
///   support <c>basic_mpp</c>, so BOLT 4 requires the HTLC to be failed); amount paid below the invoice amount or
///   more than twice it; <c>cltv_expiry</c> &lt; current height + the invoice's <c>min_final_cltv_expiry_delta</c>.</item>
/// </list>
/// <para>The first two checks run before the invoice lookup, as LND and CLN do: they detect a penultimate hop that
/// tampered with the HTLC and reveal nothing about our invoices. BOLT 4 lists 15 before 18/19 in its failure-code
/// list; since the HTLC-vs-onion errors carry no invoice information, reporting them first leaks nothing.</para>
/// <para>Stateless: <see cref="Evaluate"/> is pure; <see cref="ProcessAsync"/> only reads the invoice through the
/// caller's repository (its unit of work). Neither mutates the invoice.</para>
/// <para>Replays: an HTLC that already accepted its invoice fails here as "already paid" if processed again; the
/// caller (HTLC switch) must process each incoming HTLC once, and after a restart act on the HTLC's persisted
/// state (a fulfill already staged) instead of re-running the final hop.</para>
/// <para>Concurrency: the invoice check here is a read, not a check-and-mark. Two HTLCs for the same
/// <c>payment_hash</c> evaluated concurrently (a payer retry, or a malicious payer) would both see the invoice Open
/// and both be accepted. The caller (W2-B HTLC switch) MUST therefore, under a per-payment-hash lock and inside the
/// same unit of work that stages the fulfill: re-read the invoice, run this processor, call
/// <c>invoice.Accept(result.AmountReceived)</c> + <c>UpdateAsync</c>, and save before releasing the lock (or use an
/// equivalent compare-and-set on the invoice status), so a second concurrent HTLC sees Accepted and is failed with
/// <c>incorrect_or_unknown_payment_details</c> (PERM|15).</para>
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
    public async Task<FinalHopResult> ProcessAsync(IInvoiceDbRepository invoices, Hash paymentHash,
                                                   LightningMoney htlcAmount, uint htlcCltvExpiry,
                                                   HopPayload payload, uint currentBlockHeight)
    {
        ArgumentNullException.ThrowIfNull(invoices);

        var onionFailure = CheckHtlcAgainstOnion(htlcAmount, htlcCltvExpiry, payload);
        if (onionFailure is not null)
            return Log(paymentHash, onionFailure);

        var invoice = await invoices.GetByPaymentHashAsync(paymentHash);
        return Evaluate(invoice, paymentHash, htlcAmount, htlcCltvExpiry, payload, currentBlockHeight);
    }

    /// <summary>
    /// Evaluates the HTLC against <paramref name="invoice"/> (null when we never issued one for the hash).
    /// </summary>
    /// <inheritdoc cref="ProcessAsync" path="/param"/>
    public FinalHopResult Evaluate(InvoiceModel? invoice, Hash paymentHash, LightningMoney htlcAmount,
                                   uint htlcCltvExpiry, HopPayload payload, uint currentBlockHeight)
    {
        ArgumentNullException.ThrowIfNull(htlcAmount);
        ArgumentNullException.ThrowIfNull(payload);

        var result = CheckHtlcAgainstOnion(htlcAmount, htlcCltvExpiry, payload)
                  ?? CheckInvoice(invoice, paymentHash, htlcAmount, htlcCltvExpiry, payload, currentBlockHeight);

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
                                        uint htlcCltvExpiry, HopPayload payload, uint currentBlockHeight)
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

        switch (invoice.Status)
        {
            case InvoiceStatus.Canceled:
                return Unknown("The invoice is canceled.");
            case InvoiceStatus.Accepted or InvoiceStatus.Settled:
                return Unknown($"The invoice is already {invoice.Status}.");
        }

        if (invoice.IsExpired(_timeProvider.GetUtcNow()))
            return Unknown("The invoice is expired.");

        if (!paymentData.PaymentSecret.Equals(invoice.PaymentSecret))
            return Unknown("The payment_secret does not match.");

        // We do not support basic_mpp: total_msat must be exactly amt_to_forward
        var totalMsat = paymentData.TotalMsat;
        if (totalMsat.MilliSatoshi != amtToForward.MilliSatoshi)
            return Unknown($"total_msat {totalMsat.MilliSatoshi} differs from amt_to_forward "
                         + $"{amtToForward.MilliSatoshi} (multi-part payments are not supported).");

        if (invoice.Amount is { } expected)
        {
            if (totalMsat < expected)
                return Unknown($"Amount paid {totalMsat.MilliSatoshi} msat is below the invoice amount "
                             + $"{expected.MilliSatoshi} msat.");

            if ((UInt128)totalMsat.MilliSatoshi > (UInt128)expected.MilliSatoshi * 2)
                return Unknown($"Amount paid {totalMsat.MilliSatoshi} msat is more than twice the invoice amount "
                             + $"{expected.MilliSatoshi} msat.");
        }

        if ((ulong)htlcCltvExpiry < (ulong)currentBlockHeight + invoice.MinFinalCltvExpiry)
            return Unknown($"cltv_expiry {htlcCltvExpiry} is below height {currentBlockHeight} + "
                         + $"min_final_cltv_expiry_delta {invoice.MinFinalCltvExpiry}.");

        return FinalHopResult.Accept(invoice, htlcAmount);
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