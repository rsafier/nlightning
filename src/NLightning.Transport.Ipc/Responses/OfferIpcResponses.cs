using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;

/// <summary>
/// One of our BOLT 12 offers, in the offer responses.
/// </summary>
[MessagePackObject]
public sealed class OfferInfoIpcResponse
{
    [Key(0)] public required Hash OfferId { get; init; }

    /// <summary>The <c>lno1...</c> string.</summary>
    [Key(1)] public required string Bolt12 { get; init; }

    [Key(2)] public string? Description { get; init; }

    /// <summary>The amount per item, or null for any amount.</summary>
    [Key(3)] public LightningMoney? Amount { get; init; }

    [Key(4)] public string? Issuer { get; init; }

    /// <summary>null for a single item, 0 for unlimited.</summary>
    [Key(5)] public ulong? QuantityMax { get; init; }

    [Key(6)] public DateTimeOffset? AbsoluteExpiry { get; init; }

    [Key(7)] public bool HasPaths { get; init; }

    [Key(8)] public required OfferStatus Status { get; init; }

    /// <summary>Whether it still answered invoice_requests when the daemon built the response.</summary>
    [Key(9)] public bool IsActive { get; init; }

    [Key(10)] public required DateTimeOffset CreatedAt { get; init; }

    [Key(11)] public DateTimeOffset? DisabledAt { get; init; }

    [Key(12)] public int? PaidInvoices { get; init; }

    [Key(13)] public int? UnpaidInvoices { get; init; }

    public static OfferInfoIpcResponse FromClientResponse(OfferInfoClientResponse offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new OfferInfoIpcResponse
        {
            OfferId = offer.OfferId,
            Bolt12 = offer.Bolt12,
            Description = offer.Description,
            Amount = offer.Amount,
            Issuer = offer.Issuer,
            QuantityMax = offer.QuantityMax,
            AbsoluteExpiry = offer.AbsoluteExpiry,
            HasPaths = offer.HasPaths,
            Status = offer.Status,
            IsActive = offer.IsActive,
            CreatedAt = offer.CreatedAt,
            DisabledAt = offer.DisabledAt,
            PaidInvoices = offer.PaidInvoices,
            UnpaidInvoices = offer.UnpaidInvoices
        };
    }
}

/// <summary>
/// Response for CreateOffer (ClientCommand 26).
/// </summary>
[MessagePackObject]
public sealed class CreateOfferIpcResponse
{
    [Key(0)] public required OfferInfoIpcResponse Offer { get; init; }

    public static CreateOfferIpcResponse FromClientResponse(CreateOfferClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new CreateOfferIpcResponse { Offer = OfferInfoIpcResponse.FromClientResponse(response.Offer) };
    }
}

/// <summary>
/// Response for ListOffers (ClientCommand 27): our offers, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListOffersIpcResponse
{
    [Key(0)] public required List<OfferInfoIpcResponse> Offers { get; init; }

    public static ListOffersIpcResponse FromClientResponse(ListOffersClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ListOffersIpcResponse
        {
            Offers = response.Offers.Select(OfferInfoIpcResponse.FromClientResponse).ToList()
        };
    }
}

/// <summary>
/// Response for DisableOffer (ClientCommand 28).
/// </summary>
[MessagePackObject]
public sealed class DisableOfferIpcResponse
{
    [Key(0)] public required OfferInfoIpcResponse Offer { get; init; }

    /// <summary>False when the offer was already disabled or expired.</summary>
    [Key(1)] public bool Changed { get; init; }

    public static DisableOfferIpcResponse FromClientResponse(DisableOfferClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new DisableOfferIpcResponse
        {
            Offer = OfferInfoIpcResponse.FromClientResponse(response.Offer),
            Changed = response.Changed
        };
    }
}