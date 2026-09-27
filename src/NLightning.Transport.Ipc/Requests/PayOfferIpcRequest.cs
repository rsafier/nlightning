using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Money;

/// <summary>
/// Request for PayOffer and FetchInvoice (ClientCommand 29 and 30, provisional until the B12 integration); FetchInvoice
/// ignores the payment limits (keys 4-6).
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

    public PayOfferClientRequest ToClientRequest() => new(Offer)
    {
        Amount = Amount,
        Quantity = Quantity,
        PayerNote = PayerNote,
        TimeoutSeconds = TimeoutSeconds,
        MaxFee = MaxFee,
        MaxParts = MaxParts
    };
}