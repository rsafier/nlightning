using System.Security.Cryptography;

namespace NLightning.Application.Payments.Keysend;

using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Receiving a spontaneous (keysend) payment: the invoice record the final hop checks an HTLC against when no invoice
/// was issued for its payment hash but its onion carries <c>keysend_preimage</c> (5482373484).
/// </summary>
/// <remarks>
/// <para>The HTLC switch asks <see cref="TryCreateInvoice"/> under the payment hash lock, before
/// <c>FinalHopProcessor.Evaluate</c>, only when no invoice exists for the hash. The record it returns is
/// <c>InvoiceKind.Keysend</c> (no BOLT 11 string, any amount, the payer's preimage, the custom records the payer
/// attached, <see cref="KeysendOptions.MinFinalCltvExpiryDelta"/> as <c>min_final_cltv_expiry_delta</c>); the switch
/// saves it only once the processor accepted the HTLC, then settles it like any invoice, so an HTLC that is failed
/// leaves no record behind. A refusal leaves no record and the processor fails the HTLC as for an unknown hash
/// (<c>incorrect_or_unknown_payment_details</c>), so a probe learns nothing from it.</para>
/// <para>Refused: keysend turned off (<see cref="KeysendOptions.Accept"/>), a blinded final hop (keysend has no
/// blinded form), a preimage that is not 32 bytes or whose SHA256 is not the HTLC's payment hash. The processor then
/// applies the amount and CLTV rules: single part only (<c>payment_data.total_msat</c>, when sent, must equal
/// <c>amt_to_forward</c>), any amount, the record's CLTV delta.</para>
/// </remarks>
public sealed class KeysendReceiver
{
    /// <summary>The length of a keysend preimage.</summary>
    public const int PreimageLength = 32;

    private readonly KeysendOptions _options;
    private readonly TimeProvider _timeProvider;

    public KeysendReceiver(KeysendOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new KeysendOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether <paramref name="payload"/> is a keysend final payload (a <c>keysend_preimage</c> outside a blinded
    /// route).
    /// </summary>
    public static bool IsKeysend(HopPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.KeysendPreimage is not null && !payload.IsBlinded;
    }

    /// <summary>
    /// The keysend invoice record for an HTLC of <paramref name="paymentHash"/> whose final payload is
    /// <paramref name="payload"/>, or null with <paramref name="reason"/> when it is refused. Nothing is persisted.
    /// </summary>
    public InvoiceModel? TryCreateInvoice(Hash paymentHash, HopPayload payload, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!_options.Accept)
        {
            reason = "Keysend payments are not accepted (Node:Keysend:Accept is false).";
            return null;
        }

        if (!IsKeysend(payload))
        {
            reason = "The final payload is not a keysend payload.";
            return null;
        }

        var preimage = payload.KeysendPreimage!.Value.Span;
        if (preimage.Length != PreimageLength)
        {
            reason = $"The keysend preimage is {preimage.Length} bytes, not {PreimageLength}.";
            return null;
        }

        if (!SHA256.HashData(preimage).AsSpan().SequenceEqual((ReadOnlySpan<byte>)paymentHash))
        {
            reason = "SHA256 of the keysend preimage is not the payment hash.";
            return null;
        }

        // No invoice, so no payment_secret: keep the one the payer sent in payment_data, if any (not checked)
        var paymentSecret = payload.PaymentData?.PaymentSecret ?? new Secret(new byte[32]);
        reason = null;
        return new InvoiceModel(paymentHash, new Secret(preimage.ToArray()), paymentSecret, null, null, null,
                                _timeProvider.GetUtcNow(), _options.RecordExpirySeconds,
                                _options.MinFinalCltvExpiryDelta,
                                keysend: new KeysendDetails(payload.CustomRecords));
    }
}