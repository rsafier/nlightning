using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Request for CreateOffer (ClientCommand 26).
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

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(6)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(7)] public List<string>? Tags { get; init; }

    public CreateOfferClientRequest ToClientRequest() => new()
    {
        Amount = Amount,
        Description = Description,
        Issuer = Issuer,
        QuantityMax = QuantityMax,
        AbsoluteExpiry = AbsoluteExpiry,
        ForcePaths = ForcePaths,
        Label = Label,
        Tags = Tags ?? []
    };
}

/// <summary>
/// Request for ListOffers (ClientCommand 27): a page of our offers, newest first.
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
/// Request for DisableOffer (ClientCommand 28).
/// </summary>
[MessagePackObject]
public sealed class DisableOfferIpcRequest
{
    [Key(0)] public required Hash OfferId { get; init; }

    public DisableOfferClientRequest ToClientRequest() => new() { OfferId = OfferId };
}