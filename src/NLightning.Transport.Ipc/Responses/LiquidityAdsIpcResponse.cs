using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;

/// <summary>
/// Response for LiquidityAds (ClientCommand 46, NL-850): only the part the request's action asks for is filled in.
/// </summary>
[MessagePackObject]
public sealed class LiquidityAdsIpcResponse
{
    [Key(0)] public required LiquidityAdsAction Action { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Rates"/>: our rates, or null when we do not sell.</summary>
    [Key(1)] public List<FundingRateIpcInfo>? OurRates { get; init; }

    /// <summary>The payment types we accept, by name, or null when we do not sell.</summary>
    [Key(2)] public List<string>? OurPaymentTypes { get; init; }

    /// <summary>The lease we keep a sold channel open for, in blocks.</summary>
    [Key(3)] public uint LeaseBlocks { get; init; }

    /// <summary>The sale negotiations holding wallet inputs now.</summary>
    [Key(4)] public int SalesInProgress { get; init; }

    /// <summary><see cref="LiquidityAdsAction.Sellers"/>: the nodes that advertise rates.</summary>
    [Key(5)] public List<LiquiditySellerIpcInfo> Sellers { get; set; } = [];

    /// <summary><see cref="LiquidityAdsAction.Purchases"/>: the page, newest first.</summary>
    [Key(6)] public List<LiquidityPurchaseIpcInfo> Purchases { get; set; } = [];

    /// <summary>The chain height the lease status is computed at.</summary>
    [Key(7)] public uint CurrentHeight { get; init; }

    public static LiquidityAdsIpcResponse FromClientResponse(LiquidityAdsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new LiquidityAdsIpcResponse
        {
            Action = clientResponse.Action,
            OurRates = clientResponse.OurRates?.Rates.Select(FundingRateIpcInfo.From).ToList(),
            OurPaymentTypes = clientResponse.OurRates is { } rates ? LiquiditySellerIpcInfo.PaymentTypeNames(rates) : null,
            LeaseBlocks = clientResponse.LeaseBlocks,
            SalesInProgress = clientResponse.SalesInProgress,
            Sellers = clientResponse.Sellers.Select(LiquiditySellerIpcInfo.From).ToList(),
            Purchases = clientResponse.Purchases.Select(LiquidityPurchaseIpcInfo.From).ToList(),
            CurrentHeight = clientResponse.CurrentHeight
        };
    }
}

/// <summary>A liquidity ads <c>funding_rate</c> (BOLT PR #1153).</summary>
[MessagePackObject]
public sealed class FundingRateIpcInfo
{
    [Key(0)] public uint MinAmountSat { get; init; }
    [Key(1)] public uint MaxAmountSat { get; init; }

    /// <summary>The weight of the seller's inputs and outputs whose mining fee the buyer refunds.</summary>
    [Key(2)] public ushort FundingWeight { get; init; }

    /// <summary>The proportional fee, in basis points of the contributed amount.</summary>
    [Key(3)] public ushort FeeBasisPoints { get; init; }

    [Key(4)] public uint FeeBaseSat { get; init; }

    /// <summary>The extra flat fee when the purchase opens a new channel.</summary>
    [Key(5)] public uint ChannelCreationFeeSat { get; init; }

    public static FundingRateIpcInfo From(FundingRate rate) =>
        new()
        {
            MinAmountSat = rate.MinAmountSat,
            MaxAmountSat = rate.MaxAmountSat,
            FundingWeight = rate.FundingWeight,
            FeeBasisPoints = rate.FeeBasis,
            FeeBaseSat = rate.FeeBaseSat,
            ChannelCreationFeeSat = rate.ChannelCreationFeeSat
        };

    public FundingRate ToFundingRate() =>
        new(MinAmountSat, MaxAmountSat, FundingWeight, FeeBasisPoints, FeeBaseSat, ChannelCreationFeeSat);
}

/// <summary>A node that sells inbound liquidity.</summary>
[MessagePackObject]
public sealed class LiquiditySellerIpcInfo
{
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>Where the rates were read: its <c>init</c> or its <c>node_announcement</c>.</summary>
    [Key(1)] public LiquiditySellerSource Source { get; init; }

    [Key(2)] public bool IsConnected { get; init; }

    [Key(3)] public string? Alias { get; init; }

    [Key(4)] public List<FundingRateIpcInfo> Rates { get; set; } = [];

    /// <summary>The payment types it accepts, by name (an unknown bit as its number).</summary>
    [Key(5)] public List<string> PaymentTypes { get; set; } = [];

    public static LiquiditySellerIpcInfo From(LiquiditySellerInfo seller)
    {
        ArgumentNullException.ThrowIfNull(seller);
        return new LiquiditySellerIpcInfo
        {
            NodeId = seller.NodeId,
            Source = seller.Source,
            IsConnected = seller.IsConnected,
            Alias = seller.Alias,
            Rates = seller.Rates.Rates.Select(FundingRateIpcInfo.From).ToList(),
            PaymentTypes = PaymentTypeNames(seller.Rates)
        };
    }

    /// <summary>The payment types of <paramref name="rates"/> by name (<c>from_channel_balance</c>, ...).</summary>
    public static List<string> PaymentTypeNames(WillFundRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var names = new List<string>();
        for (ulong bit = 0; bit < (ulong)rates.EncodedPaymentTypes.Length * 8; bit++)
        {
            if (!rates.SupportsBit(bit))
                continue;

            names.Add(bit switch
            {
                (ulong)LiquidityPaymentType.FromChannelBalance => "from_channel_balance",
                (ulong)LiquidityPaymentType.FromFutureHtlc => "from_future_htlc",
                (ulong)LiquidityPaymentType.FromFutureHtlcWithPreimage => "from_future_htlc_with_preimage",
                (ulong)LiquidityPaymentType.FromChannelBalanceForFutureHtlc => "from_channel_balance_for_future_htlc",
                _ => $"unknown_{bit}"
            });
        }

        return names;
    }
}

/// <summary>A liquidity purchase we made or sold, with its lease.</summary>
[MessagePackObject]
public sealed class LiquidityPurchaseIpcInfo
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The funding attempt's txid (hex, the usual display order).</summary>
    [Key(1)] public required string FundingTxId { get; init; }

    [Key(2)] public LiquidityPurchaseRole Role { get; init; }
    [Key(3)] public LiquidityPurchaseKind Kind { get; init; }
    [Key(4)] public LiquidityPurchaseStatus Status { get; init; }

    /// <summary>The other side: the seller when we bought, the buyer when we sold.</summary>
    [Key(5)] public required CompactPubKey PeerNodeId { get; init; }

    [Key(6)] public ulong RequestedSat { get; init; }
    [Key(7)] public ulong ContributedSat { get; init; }
    [Key(8)] public required FundingRateIpcInfo Rate { get; init; }
    [Key(9)] public ulong MiningFeeSat { get; init; }
    [Key(10)] public ulong ServiceFeeSat { get; init; }
    [Key(11)] public uint LeaseBlocks { get; init; }

    /// <summary>The height the funding confirmed at; null until then.</summary>
    [Key(12)] public uint? LeaseStartHeight { get; init; }

    /// <summary>The first height the lease no longer covers; null until the funding confirmed.</summary>
    [Key(13)] public uint? LeaseEndHeight { get; init; }

    [Key(14)] public uint? ClosedAtHeight { get; init; }
    [Key(15)] public bool ClosedEarly { get; init; }
    [Key(16)] public DateTimeOffset CreatedAt { get; init; }

    [IgnoreMember] public ulong TotalFeeSat => MiningFeeSat + ServiceFeeSat;

    public static LiquidityPurchaseIpcInfo From(LiquidityPurchaseModel purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        return new LiquidityPurchaseIpcInfo
        {
            ChannelId = purchase.ChannelId,
            FundingTxId = Convert.ToHexString(((byte[])purchase.FundingTxId).Reverse().ToArray()).ToLowerInvariant(),
            Role = purchase.Role,
            Kind = purchase.Kind,
            Status = purchase.Status,
            PeerNodeId = purchase.PeerNodeId,
            RequestedSat = purchase.RequestedSat,
            ContributedSat = purchase.ContributedSat,
            Rate = FundingRateIpcInfo.From(purchase.Rate),
            MiningFeeSat = purchase.MiningFeeSat,
            ServiceFeeSat = purchase.ServiceFeeSat,
            LeaseBlocks = purchase.LeaseBlocks,
            LeaseStartHeight = purchase.LeaseStartHeight,
            LeaseEndHeight = purchase.LeaseEndHeight,
            ClosedAtHeight = purchase.ClosedAtHeight,
            ClosedEarly = purchase.ClosedEarly,
            CreatedAt = purchase.CreatedAt
        };
    }
}