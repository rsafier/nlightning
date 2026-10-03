using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for WaitInvoice (ClientCommand 46, Cashu plan C0, NL-812).
/// </summary>
[MessagePackObject]
public sealed class WaitInvoiceIpcRequest
{
    /// <summary>The invoice's payment hash.</summary>
    [Key(0)] public required Hash PaymentHash { get; init; }

    /// <summary>How long to wait, or null for the node's default.</summary>
    [Key(1)] public uint? TimeoutSeconds { get; init; }

    public WaitInvoiceClientRequest ToClientRequest() => new(PaymentHash, TimeoutSeconds);
}