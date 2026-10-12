using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Request for CreateHoldInvoice (ClientCommand 49, NL-995).
/// </summary>
[MessagePackObject]
public sealed class CreateHoldInvoiceIpcRequest
{
    /// <summary>
    /// The payment hash, chosen by the caller; whoever settles later proves it by hashing to this.
    /// </summary>
    [Key(0)] public required Hash PaymentHash { get; init; }

    /// <summary>
    /// The requested amount, or null for an invoice that accepts any amount.
    /// </summary>
    [Key(1)] public LightningMoney? Amount { get; init; }

    /// <summary>
    /// BOLT 11 <c>d</c>; may be empty.
    /// </summary>
    [Key(2)] public string Description { get; set; } = string.Empty;

    /// <summary>
    /// BOLT 11 <c>x</c> in seconds, or null for the node default.
    /// </summary>
    [Key(3)] public uint? ExpirySeconds { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(4)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(5)] public List<string>? Tags { get; init; }

    public CreateHoldInvoiceClientRequest ToClientRequest()
    {
        return new CreateHoldInvoiceClientRequest
        {
            PaymentHash = PaymentHash,
            Amount = Amount,
            Description = Description ?? string.Empty,
            ExpirySeconds = ExpirySeconds,
            Label = Label,
            Tags = Tags ?? []
        };
    }
}

/// <summary>
/// Request for SettleHoldInvoice (ClientCommand 50, NL-995).
/// </summary>
[MessagePackObject]
public sealed class SettleHoldInvoiceIpcRequest
{
    /// <summary>The payment hash of the held invoice to settle.</summary>
    [Key(0)] public required Hash PaymentHash { get; init; }

    /// <summary>The preimage of <see cref="PaymentHash"/>, from outside (a Cashu melt, a trusted payer).</summary>
    [Key(1)] public required Secret Preimage { get; init; }

    public SettleHoldInvoiceClientRequest ToClientRequest() => new() { PaymentHash = PaymentHash, Preimage = Preimage };
}

/// <summary>
/// Request for CancelHoldInvoice (ClientCommand 51, NL-995).
/// </summary>
[MessagePackObject]
public sealed class CancelHoldInvoiceIpcRequest
{
    /// <summary>The payment hash of the hold invoice to cancel.</summary>
    [Key(0)] public required Hash PaymentHash { get; init; }

    public CancelHoldInvoiceClientRequest ToClientRequest() => new() { PaymentHash = PaymentHash };
}