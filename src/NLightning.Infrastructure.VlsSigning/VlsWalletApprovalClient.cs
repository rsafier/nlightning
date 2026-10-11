using System.Text.Json.Nodes;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>
/// Trusted operator-side withdrawal approval (NL-1335): adds a destination address to VLS's node allowlist, the only
/// non-wallet output VLS's on-chain policy signs for. Like <see cref="VlsPaymentApprovalClient"/>, never register this
/// client in the node's service provider or give its approval credential to the node.
/// </summary>
public sealed class VlsWalletApprovalClient(string approvalSocketPath, string approvalTokenFile)
{
    private readonly VlsGatewayTransport _transport = new(approvalSocketPath, approvalTokenFile);

    /// <summary>Allowlists a regtest address for withdrawals; VLS persists it with its policy state.</summary>
    public void AllowlistAddress(Guid requestId, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var result = _transport.Invoke(requestId, new JsonObject
        {
            ["op"] = "allowlist_address",
            ["address"] = address
        });
        if (result["added"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("VLS did not allowlist the address. Do not withdraw to it.");
    }
}