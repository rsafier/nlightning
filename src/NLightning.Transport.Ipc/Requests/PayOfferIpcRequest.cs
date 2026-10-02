using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Request for PayOffer and FetchInvoice (ClientCommand 29 and 30); FetchInvoice
/// ignores the payment limits (keys 4-6) and the label and tags (keys 7-8, NL-602 A3-T1).
/// </summary>
[MessagePackObject]
public sealed class PayOfferIpcRequest
{
    /// <summary>The <c>lno1...</c> string.</summary>
    [Key(0)] public required string Offer { get; init; }

    /// <summary>The amount to ask for (required for an offer without one), or null.</summary>
    [Key(1)] public LightningMoney? Amount { get; init; }

    /// <summary>The quantity (required for an offer with <c>offer_quantity_max</c>), or null.</summary>
    [Key(2)] public ulong? Quantity { get; init; }

    /// <summary>The payer note, or null.</summary>
    [Key(3)] public string? PayerNote { get; init; }

    /// <summary>How long the daemon waits for the payment's outcome, in seconds, or null for its default.</summary>
    [Key(4)] public uint? TimeoutSeconds { get; init; }

    /// <summary>The most the payment may pay in fees, or null for the daemon's default.</summary>
    [Key(5)] public LightningMoney? MaxFee { get; init; }

    /// <summary>The most HTLCs in flight at once, or null for the daemon's default.</summary>
    [Key(6)] public uint? MaxParts { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(7)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(8)] public List<string>? Tags { get; init; }

    public PayOfferClientRequest ToClientRequest() => new(Offer)
    {
        Amount = Amount,
        Quantity = Quantity,
        PayerNote = PayerNote,
        TimeoutSeconds = TimeoutSeconds,
        MaxFee = MaxFee,
        MaxParts = MaxParts,
        Label = Label,
        Tags = Tags ?? []
    };
}