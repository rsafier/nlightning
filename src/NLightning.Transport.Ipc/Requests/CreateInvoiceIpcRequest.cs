using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Request for CreateInvoice (ClientCommand 9).
/// </summary>
[MessagePackObject]
public sealed class CreateInvoiceIpcRequest
{
    /// <summary>
    /// The requested amount, or null for an invoice that accepts any amount.
    /// </summary>
    [Key(0)] public LightningMoney? Amount { get; init; }

    /// <summary>
    /// BOLT 11 <c>d</c>; may be empty.
    /// </summary>
    [Key(1)] public string Description { get; set; } = string.Empty;

    /// <summary>
    /// BOLT 11 <c>x</c> in seconds, or null for the node default.
    /// </summary>
    [Key(2)] public uint? ExpirySeconds { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(3)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(4)] public List<string>? Tags { get; init; }

    public CreateInvoiceClientRequest ToClientRequest()
    {
        return new CreateInvoiceClientRequest
        {
            Amount = Amount,
            Description = Description ?? string.Empty,
            ExpirySeconds = ExpirySeconds,
            Label = Label,
            Tags = Tags ?? []
        };
    }
}