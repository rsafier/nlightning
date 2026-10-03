// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.LiquidityAds;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A liquidity purchase we made or sold (<c>LiquidityPurchaseModel</c>, NL-850 LA3, migration
/// <c>AddLiquidityPurchases</c>), keyed by a database-assigned id; one row per (channel, funding attempt).
/// </summary>
/// <remarks>
/// No foreign key to <c>Channels</c>: a purchase is a record of what was paid, kept after its channel is forgotten,
/// and may be saved before the channel row of a dual-funded open exists.
/// </remarks>
public class LiquidityPurchaseEntity
{
    /// <summary>The primary key, assigned by the database.</summary>
    public long Id { get; set; }

    public required ChannelId ChannelId { get; set; }

    /// <summary>The funding attempt (open, RBF attempt or splice) the purchase rides on.</summary>
    public required TxId FundingTxId { get; set; }

    /// <summary><c>LiquidityPurchaseRole</c> (1 buyer, 2 seller).</summary>
    public required byte Role { get; set; }

    /// <summary><c>LiquidityPurchaseKind</c> (1 open, 2 open RBF, 3 splice, 4 splice RBF).</summary>
    public required byte Kind { get; set; }

    public required long RequestedSat { get; set; }
    public required long ContributedSat { get; set; }

    // The funding_rate, one column per field
    public required uint RateMinAmountSat { get; set; }
    public required uint RateMaxAmountSat { get; set; }
    public required ushort RateFundingWeight { get; set; }
    public required ushort RateFeeBasis { get; set; }
    public required uint RateFeeBaseSat { get; set; }
    public required uint RateChannelCreationFeeSat { get; set; }

    /// <summary><c>LiquidityPaymentType</c> (0 from_channel_balance; 128-130 Eclair's on-the-fly funding).</summary>
    public required byte PaymentType { get; set; }

    public required long MiningFeeSat { get; set; }
    public required long ServiceFeeSat { get; set; }

    /// <summary>The seller's 64-byte <c>will_fund</c> signature.</summary>
    public required byte[] Signature { get; set; }

    /// <summary>The funding output's script the signature covers.</summary>
    public required byte[] FundingScript { get; set; }

    public required CompactPubKey PeerNodeId { get; set; }

    public required uint LeaseBlocks { get; set; }

    /// <summary><c>LiquidityPurchaseStatus</c> (1 pending, 2 active, 3 replaced, 4 closed).</summary>
    public required byte Status { get; set; }

    public uint? LeaseStartHeight { get; set; }
    public uint? ClosedAtHeight { get; set; }
    public bool ClosedEarly { get; set; }

    /// <summary>When the purchase was negotiated (stored as UTC ticks).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    // Default constructor for EF Core
    internal LiquidityPurchaseEntity()
    {
    }
}