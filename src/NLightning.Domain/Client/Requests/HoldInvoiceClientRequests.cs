namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// Creates a hold invoice for a payment hash whose preimage we do not know (<c>ClientCommand.CreateHoldInvoice</c>,
/// NL-995): its paying HTLC set is held once complete, until the operator settles with the outside preimage or cancels.
/// </summary>
public sealed class CreateHoldInvoiceClientRequest
{
    /// <summary>
    /// The payment hash, chosen by the caller (not drawn by us): whoever settles later proves it by hashing to this.
    /// </summary>
    public required Hash PaymentHash { get; init; }

    /// <summary>
    /// The requested amount, or null for an invoice that accepts any amount.
    /// </summary>
    public LightningMoney? Amount { get; init; }

    /// <summary>
    /// BOLT 11 <c>d</c>; may be empty.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// BOLT 11 <c>x</c> in seconds, or null for the node default (<c>Node:Routing:InvoiceExpirySeconds</c>).
    /// </summary>
    public uint? ExpirySeconds { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Settles a held invoice with the preimage from outside (<c>ClientCommand.SettleHoldInvoice</c>, NL-995): it must
/// hash to the invoice's payment hash, and every held part is fulfilled with it.
/// </summary>
public sealed class SettleHoldInvoiceClientRequest
{
    /// <summary>The payment hash of the held invoice to settle.</summary>
    public required Hash PaymentHash { get; init; }

    /// <summary>The preimage of <see cref="PaymentHash"/>, from outside (a Cashu melt, a trusted payer).</summary>
    public required Secret Preimage { get; init; }
}

/// <summary>
/// Cancels a hold invoice (<c>ClientCommand.CancelHoldInvoice</c>, NL-995): a held set's parts are failed back and the
/// invoice is <c>Canceled</c>; an open one is simply canceled.
/// </summary>
public sealed class CancelHoldInvoiceClientRequest
{
    /// <summary>The payment hash of the hold invoice to cancel.</summary>
    public required Hash PaymentHash { get; init; }
}