namespace NLightning.Application.Payments.FinalHop;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Models;

/// <summary>
/// What <see cref="FinalHopProcessor"/> decided for an HTLC that pays us: accept it (fulfill with
/// <see cref="Preimage"/>) or fail it with <see cref="Failure"/>.
/// </summary>
/// <remarks>
/// The processor never mutates or persists the invoice. On accept the caller does
/// <c>Invoice.Accept(AmountReceived)</c> + <c>IInvoiceDbRepository.UpdateAsync</c> in the same unit of work as the
/// channel transition that fulfills the HTLC, and settles the invoice when the fulfill is irrevocable. On failure it
/// returns <c>IFailureOnionService.CreateErrorPacket(sharedSecret, Failure)</c> in <c>update_fail_htlc</c>.
/// </remarks>
public sealed record FinalHopResult
{
    /// <summary>
    /// The invoice being paid, when accepted.
    /// </summary>
    public InvoiceModel? Invoice { get; }

    /// <summary>
    /// The amount the HTLC carries (<c>amount_msat</c>), when accepted.
    /// </summary>
    public LightningMoney? AmountReceived { get; }

    /// <summary>
    /// The failure to return to the origin, or null when accepted.
    /// </summary>
    public FailureMessage? Failure { get; }

    /// <summary>
    /// A local explanation of the failure, for logs only (never sent: the origin must not learn which check failed).
    /// </summary>
    public string? Reason { get; }

    public bool IsAccepted => Failure is null;

    /// <summary>
    /// The onion's <c>amt_to_forward</c> (this part's contribution to the HTLC set), when accepted.
    /// </summary>
    public LightningMoney? PartAmount { get; }

    /// <summary>
    /// The onion's <c>total_msat</c> (what the whole HTLC set pays), when accepted.
    /// </summary>
    public LightningMoney? TotalMsat { get; }

    /// <summary>
    /// True when the HTLC is one part of a multi-part payment (<c>total_msat</c> &gt; <c>amt_to_forward</c>).
    /// </summary>
    public bool IsMultiPart => PartAmount is not null && TotalMsat is not null && TotalMsat != PartAmount;

    /// <summary>
    /// True when the invoice is already <c>Settled</c> and this HTLC is fulfilled without touching it (possibly a part
    /// of the set whose first fulfill settled the invoice; BOLT 4 requires the whole set to be fulfilled).
    /// </summary>
    public bool InvoiceAlreadySettled { get; }

    /// <summary>
    /// The preimage to fulfill with, when accepted.
    /// </summary>
    public Secret? Preimage => Invoice?.Preimage;

    private FinalHopResult(InvoiceModel? invoice, LightningMoney? amountReceived, FailureMessage? failure,
                           string? reason, LightningMoney? partAmount = null, LightningMoney? totalMsat = null,
                           bool invoiceAlreadySettled = false)
    {
        Invoice = invoice;
        AmountReceived = amountReceived;
        Failure = failure;
        Reason = reason;
        PartAmount = partAmount;
        TotalMsat = totalMsat;
        InvoiceAlreadySettled = invoiceAlreadySettled;
    }

    /// <param name="invoice">The invoice being paid.</param>
    /// <param name="amountReceived">The HTLC's <c>amount_msat</c>.</param>
    /// <param name="partAmount">The onion's <c>amt_to_forward</c>; defaults to <paramref name="amountReceived"/>.</param>
    /// <param name="totalMsat">The onion's <c>total_msat</c>; defaults to <paramref name="partAmount"/> (a single-part
    /// payment).</param>
    /// <param name="invoiceAlreadySettled">See <see cref="InvoiceAlreadySettled"/>.</param>
    public static FinalHopResult Accept(InvoiceModel invoice, LightningMoney amountReceived,
                                        LightningMoney? partAmount = null, LightningMoney? totalMsat = null,
                                        bool invoiceAlreadySettled = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(amountReceived);
        var part = partAmount ?? amountReceived;
        return new FinalHopResult(invoice, amountReceived, null, null, part, totalMsat ?? part,
                                  invoiceAlreadySettled);
    }

    public static FinalHopResult Fail(FailureMessage failure, string reason)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new FinalHopResult(null, null, failure, reason);
    }
}