namespace NLightning.Domain.Client.Requests;

using Money;

/// <summary>
/// Creates an invoice we can be paid with (<c>ClientCommand.CreateInvoice</c>).
/// </summary>
public sealed class CreateInvoiceClientRequest
{
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
}