using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Request for PayInvoice (ClientCommand 10).
/// </summary>
[MessagePackObject]
public sealed class PayInvoiceIpcRequest
{
    /// <summary>
    /// The BOLT 11 invoice to pay.
    /// </summary>
    [Key(0)] public required string Bolt11 { get; init; }

    /// <summary>
    /// The amount to pay when the invoice has none; null when it has one.
    /// </summary>
    [Key(1)] public LightningMoney? Amount { get; init; }

    /// <summary>
    /// How long the daemon waits for the outcome, in seconds, or null for its default.
    /// </summary>
    [Key(2)] public uint? TimeoutSeconds { get; init; }

    /// <summary>
    /// The most the payment may pay in routing fees, or null for the daemon's default (NL-270).
    /// </summary>
    [Key(3)] public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The most HTLCs the payment may have in flight at once (1 never splits), or null for the daemon's default.
    /// </summary>
    [Key(4)] public uint? MaxParts { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(5)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(6)] public List<string>? Tags { get; init; }

    public PayInvoiceClientRequest ToClientRequest()
    {
        return new PayInvoiceClientRequest(Bolt11)
        {
            Amount = Amount,
            TimeoutSeconds = TimeoutSeconds,
            MaxFee = MaxFee,
            MaxParts = MaxParts,
            Label = Label,
            Tags = Tags ?? []
        };
    }
}