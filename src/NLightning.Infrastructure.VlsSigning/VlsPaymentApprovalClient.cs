using System.Text.Json.Nodes;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// Trusted operator-side payment approval. Do not register this client in the node's service provider or
/// supply its approval credential to the node. Approval must precede the ordinary node payment command.
/// </summary>
public sealed class VlsPaymentApprovalClient(string approvalSocketPath, string approvalTokenFile)
{
    private readonly VlsGatewayTransport _transport = new(approvalSocketPath, approvalTokenFile);

    /// <summary>Authorize an amount-bearing, signed BOLT 11 invoice using VLS's invoice validation.</summary>
    public void AuthorizeInvoice(Guid requestId, string invoice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoice);
        RequireAdmission(_transport.Invoke(requestId, new JsonObject
        {
            ["op"] = "authorize_invoice",
            ["invoice"] = invoice
        }));
    }

    /// <summary>
    /// Authorize the operator-selected keysend destination, hash, and payment amount. Routing fees remain subject
    /// to the gateway's separate fee policy. VLS does not
    /// authenticate ownership of the keysend destination; the trusted operator must verify it.
    /// </summary>
    public void AuthorizeKeysend(Guid requestId, string payee, string paymentHash, ulong amountMsat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payee);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentHash);
        if (amountMsat == 0)
            throw new ArgumentOutOfRangeException(nameof(amountMsat));

        RequireAdmission(_transport.Invoke(requestId, new JsonObject
        {
            ["op"] = "authorize_keysend",
            ["payee"] = payee,
            ["hash"] = paymentHash,
            ["amount_msat"] = amountMsat
        }));
    }

    private static void RequireAdmission(JsonObject result)
    {
        if (result["added"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("VLS did not admit the payment. Do not submit it to the node.");
    }
}