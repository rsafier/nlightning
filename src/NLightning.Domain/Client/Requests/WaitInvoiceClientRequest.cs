namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Waits until one of our invoices is no longer <c>Open</c> (<c>ClientCommand.WaitInvoice</c>, 47; Cashu plan C0,
/// NL-991).
/// </summary>
public sealed class WaitInvoiceClientRequest
{
    public WaitInvoiceClientRequest(Hash paymentHash, uint? timeoutSeconds = null)
    {
        PaymentHash = paymentHash;
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The invoice's payment hash.</summary>
    public Hash PaymentHash { get; }

    /// <summary>How long to wait, or null for the default.</summary>
    public uint? TimeoutSeconds { get; }
}