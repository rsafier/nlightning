namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;

/// <summary>
/// Liquidity ads (NL-850) test data shared by the IPC, handler and printer tests.
/// </summary>
internal static class LiquidityAdsTestData
{
    public const string PeerHex = "03ca9b880627d2d4e3b33164f66946349f820d26aa9572fe0e525e534850cbd413";

    public static readonly CompactPubKey Peer = new(Convert.FromHexString(PeerHex));

    public static readonly FundingRate Rate = new(100_000, 1_000_000, 500, 100, 10, 1_000);

    public static readonly TxId FundingTxId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    /// <summary>A pending purchase: 400k requested, 410k contributed, mining 1,250 sat, service 5,010 sat.</summary>
    public static LiquidityPurchaseModel Purchase(ChannelId channelId,
                                                  LiquidityPurchaseRole role = LiquidityPurchaseRole.Buyer,
                                                  LiquidityPurchaseKind kind = LiquidityPurchaseKind.ChannelOpen) =>
        new(channelId, FundingTxId, role, kind, 400_000, 410_000, Rate, LiquidityPaymentType.FromChannelBalance,
            1_250, 5_010, new CompactSignature(new byte[64]), [0x00, 0x20, 0x01], Peer, 4_032,
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A purchase restored in <paramref name="status"/> (Active from block 100, or Closed at 2,000).</summary>
    public static LiquidityPurchaseModel Restored(ChannelId channelId, LiquidityPurchaseStatus status,
                                                  LiquidityPurchaseRole role = LiquidityPurchaseRole.Seller) =>
        LiquidityPurchaseModel.Restore(7, channelId, FundingTxId, role, LiquidityPurchaseKind.Splice, 400_000, 410_000,
                                       Rate, LiquidityPaymentType.FromChannelBalance, 1_250, 5_010,
                                       new CompactSignature(new byte[64]), [0x00, 0x20, 0x01], Peer, 4_032,
                                       new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), status,
                                       status is LiquidityPurchaseStatus.Active or LiquidityPurchaseStatus.Closed
                                           ? 100
                                           : null,
                                       status == LiquidityPurchaseStatus.Closed ? 2_000 : null,
                                       status == LiquidityPurchaseStatus.Closed);
}