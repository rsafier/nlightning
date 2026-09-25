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
    /// The preimage to fulfill with, when accepted.
    /// </summary>
    public Secret? Preimage => Invoice?.Preimage;

    private FinalHopResult(InvoiceModel? invoice, LightningMoney? amountReceived, FailureMessage? failure,
                           string? reason)
    {
        Invoice = invoice;
        AmountReceived = amountReceived;
        Failure = failure;
        Reason = reason;
    }

    public static FinalHopResult Accept(InvoiceModel invoice, LightningMoney amountReceived)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(amountReceived);
        return new FinalHopResult(invoice, amountReceived, null, null);
    }

    public static FinalHopResult Fail(FailureMessage failure, string reason)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new FinalHopResult(null, null, failure, reason);
    }
}