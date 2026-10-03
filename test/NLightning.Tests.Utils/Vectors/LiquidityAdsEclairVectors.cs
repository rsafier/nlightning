using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Vectors;

/// <summary>
/// Liquidity ads (BOLT PR #1153) vectors from Eclair 0.14.3's own tests (NL-850, plan LA1):
/// <c>eclair-core/src/test/scala/fr/acinq/eclair/wire/protocol/LiquidityAdsSpec.scala</c> ("validate liquidity ads
/// funding attempt") and <c>LightningMessageCodecsSpec.scala</c> ("encode/decode init message", the tx_init_rbf /
/// tx_ack_rbf / splice_init / splice_ack cases, "encode/decode liquidity ads", "decode unknown liquidity ads payment
/// types"). Hex values are the TLV 1339 values (after <c>fd053b</c> and the length) unless named otherwise.
/// </summary>
[ExcludeFromCodeCoverage]
public static class LiquidityAdsEclairVectors
{
    /// <summary>The node key of the signature vectors (Eclair's <c>nodeKey</c>).</summary>
    public const string NodeKey = "57ac961f1b80ebfb610037bf9c96c6333699bde42257919a53974811c34649e3";

    /// <summary>Its public key.</summary>
    public const string NodeId = "03ca9b880627d2d4e3b33164f66946349f820d26aa9572fe0e525e534850cbd413";

    // LiquidityAdsSpec: FundingRate(100_000 sat, 1_000_000 sat, 500, 100, 10 sat, 1000 sat), FromChannelBalance only,
    // a request for 500_000 sat, funding script below, validateRequest at 1000 sat/kw, isChannelCreation = true
    public const string SpecFundingScript = "00202395c9c52c02ca069f1d56a3c6124bf8b152a617328c76e6b31f83ace370c2ff";

    public const string SpecSignature =
        "a53106bd20027b0215480ff0b06b2bf9324bb257c2a0e74c2604ec347493f90d3a975d56a68b21a6cc48d6763d96f70e1d630dd1720cf6b7314d4304050fe265";

    /// <summary>init TLV 1339 with one rate (100k..500k, 550, 100, 5000, 1000) and from_channel_balance.</summary>
    public const string InitRatesOne = "0001000186a00007a1200226006400001388000003e8000101";

    /// <summary>
    /// init TLV 1339 captured live from Eclair 0.14.3 in Docker (2026-10-03, the <c>Explicit</c>
    /// <c>EclairLiquidityAdsTests</c> capture, seller configured as <c>EclairFixture.SellerRates</c>): one rate
    /// (10k..5M, 400, 100, 500, 1000) and from_channel_balance. Same layout as <see cref="InitRatesOne"/>: Eclair's
    /// running node encodes its configured rates exactly as its unit tests do.
    /// </summary>
    public const string LiveInitRates = "000100002710004c4b4001900064000001f4000003e8000101";

    /// <summary>init TLV 1339 with two rates and the payment types 0, 128, 129, 130 and an unknown 211 (a 27-byte bitfield).</summary>
    public const string InitRatesTwo =
        "0002000186a00007a1200226006400001388000003e80007a120004c4b40044c004b00000000000005dc"
      + "001b080000000000000000000700000000000000000000000000000001";

    /// <summary>node_announcement TLV 1339: the two rates of <see cref="InitRatesTwo"/> and from_channel_balance.</summary>
    public const string NodeAnnouncementRates =
        "0002000186a00007a1200226006400001388000003e80007a120004c4b40044c004b00000000000005dc000101";

    /// <summary>
    /// The whole node_announcement of LightningMessageCodecsSpec "encode/decode liquidity ads" (message type 257
    /// included): no features, timestamp 0x661cebc9, <see cref="NodeId"/>, color 2a7557, alias "LN-Liquidity", no
    /// addresses, then the TLV stream <c>fd053b 2d</c> <see cref="NodeAnnouncementRates"/>; signed by
    /// <see cref="NodeKey"/> over everything after the signature.
    /// </summary>
    public const string NodeAnnouncementWire =
        "0101"
      + "22ec2e2a6e02f54d949e332cbce571d123ae20dda98d0340ac7e64f60f11d413659a2a9645adea8f886bb5dd40cc589bd3e0f4f8b2ab333d323b74b7762b4ca1"
      + "0000"
      + "661cebc9"
      + NodeId
      + "2a7557"
      + "4c4e2d4c69717569646974790000000000000000000000000000000000000000"
      + "0000"
      + "fd053b2d" + NodeAnnouncementRates;

    /// <summary>A rates value with payment types 0, 75 and 211 (unknown types are kept).</summary>
    public const string RatesWithUnknownTypes =
        "0001000186a00007a120022600640000138800000000001b080000000000000000000000000000000008000000000000000001";

    /// <summary>tx_init_rbf request: 50_000 sat at (25_000, 250_000, 750, 150, 50, 500), from_channel_balance.</summary>
    public const string TxInitRbfRequest = "000000000000c350000061a80003d09002ee009600000032000001f40000";

    /// <summary>tx_ack_rbf answer: that rate, script <c>deadbeef</c>, an all-zero signature.</summary>
    public const string TxAckRbfWillFund =
        "000061a80003d09002ee009600000032000001f40004deadbeef"
      + "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>splice_init request: 100_000 sat at (100_000, 100_000, 400, 150, 0, 0), from_channel_balance.</summary>
    public const string SpliceInitRequest = "00000000000186a0000186a0000186a00190009600000000000000000000";

    /// <summary>open_channel2 request: 750_000 sat at (500k, 5M, 1100, 75, 0, 1500), from_channel_balance.</summary>
    public const string OpenRequest = "00000000000b71b00007a120004c4b40044c004b00000000000005dc0000";

    /// <summary>
    /// accept_channel2 answer to <see cref="OpenRequest"/>: the 2-of-2 P2WSH of the open's and accept's funding keys,
    /// signed by <see cref="NodeKey"/>.
    /// </summary>
    public const string AcceptWillFund =
        "0007a120004c4b40044c004b00000000000005dc002200202ec38203f4cf37a3b377d9a55c7ae0153c643046dbdbe2ffccfb11b74420103c"
      + "c57cf393f6bd534472ec08cbfbbc7268501b32f563a21cdf02a99127c4f25168249acd6509f96b2e93843c3b838ee4808c75d0a15ff71ba886fda980b8ca954f";

    /// <summary>open_channel2 request paid from future HTLCs (type 128, two payment hashes): decoded, never accepted.</summary>
    public const string OpenRequestFromFutureHtlc =
        "000000000007a120000186a00007a1200226006400001388000003e8"
      + "804080417c0c91deb72606958425ea1552a045a55a250e91870231b486dcb2106734d662b36d54c6d1c2a0227cdc114d12c578c25ab6ec664eebaa440d7e493eba47";
}