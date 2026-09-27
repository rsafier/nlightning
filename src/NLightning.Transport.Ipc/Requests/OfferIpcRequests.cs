using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Request for CreateOffer (provisional ClientCommand 26, <c>OfferClientCommands.CreateOffer</c>).
/// </summary>
[MessagePackObject]
public sealed class CreateOfferIpcRequest
{
    /// <summary><c>offer_amount</c>, or null for any amount.</summary>
    [Key(0)] public LightningMoney? Amount { get; init; }

    /// <summary><c>offer_description</c>; required with an amount.</summary>
    [Key(1)] public string? Description { get; init; }

    /// <summary><c>offer_issuer</c>.</summary>
    [Key(2)] public string? Issuer { get; init; }

    /// <summary><c>offer_quantity_max</c>: null for a single item, 0 for unlimited.</summary>
    [Key(3)] public ulong? QuantityMax { get; init; }

    /// <summary><c>offer_absolute_expiry</c>, seconds since 1970.</summary>
    [Key(4)] public ulong? AbsoluteExpiry { get; init; }

    /// <summary>Add <c>offer_paths</c> even with a public channel.</summary>
    [Key(5)] public bool ForcePaths { get; init; }

    public CreateOfferClientRequest ToClientRequest() => new()
    {
        Amount = Amount,
        Description = Description,
        Issuer = Issuer,
        QuantityMax = QuantityMax,
        AbsoluteExpiry = AbsoluteExpiry,
        ForcePaths = ForcePaths
    };
}

/// <summary>
/// Request for ListOffers (provisional ClientCommand 27): a page of our offers, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListOffersIpcRequest
{
    /// <summary>Only offers that still answer invoice_requests.</summary>
    [Key(0)] public bool ActiveOnly { get; init; }

    /// <summary>How many of the newest offers to skip.</summary>
    [Key(1)] public int Skip { get; init; }

    /// <summary>The most offers to return.</summary>
    [Key(2)] public int Take { get; set; } = 100;

    public ListOffersClientRequest ToClientRequest() => new() { ActiveOnly = ActiveOnly, Skip = Skip, Take = Take };
}

/// <summary>
/// Request for DisableOffer (provisional ClientCommand 28).
/// </summary>
[MessagePackObject]
public sealed class DisableOfferIpcRequest
{
    [Key(0)] public required Hash OfferId { get; init; }

    public DisableOfferClientRequest ToClientRequest() => new() { OfferId = OfferId };
}