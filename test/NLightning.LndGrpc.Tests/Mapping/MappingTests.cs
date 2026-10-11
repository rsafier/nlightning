namespace NLightning.LndGrpc.Tests.Mapping;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node;
using LndGrpc.Mapping;
using LndGrpc.Services;

/// <summary>The LND field conventions of the server (LND_GRPC_PLAN.md §3).</summary>
public class MappingTests
{
    [Fact]
    public void Given_AnAnchorsCommitmentWeFund_When_LndBalances_Then_HtlcsFeeAndAnchorsAreOutOfOurBalance()
    {
        // Arrange: 2,500 sat/kw, one untrimmed outgoing HTLC of 10,000 sat; BOLT 3 anchors weight 1124 + 172
        var spec = new CommitmentSpec(CommitmentSide.Local, 2_500, 600_000_000, 390_000_000,
                                      [new SpecHtlc(HtlcDirection.Outgoing, 0, 10_000_000, new Hash(new byte[32]), 500)]);

        // Act
        var balances = LightningService.LndBalances.FromSpec(spec, CommitmentFormat.Anchors, 354, true);

        // Assert: fee 2500 * 1296 / 1000 = 3240 sat, plus two 330 sat anchors out of the funder's (our) side
        Assert.Equal(3_240ul, balances.CommitFeeSat);
        Assert.Equal(1_296ul, balances.CommitWeight);
        Assert.Equal(600_000_000ul - 3_900_000ul, balances.LocalMsat);
        Assert.Equal(390_000_000ul, balances.RemoteMsat);
        Assert.Equal(10_000_000ul, balances.OutgoingHtlcMsat);
        Assert.Equal(0ul, balances.IncomingHtlcMsat);
    }

    [Fact]
    public void Given_ACommitmentThePeerFunds_When_LndBalances_Then_ItsSidePaysAndTrimmedHtlcsAreNotWeighed()
    {
        // Arrange: a 300 sat incoming HTLC is under the 354 sat dust limit (anchors: no second-stage fee)
        var spec = new CommitmentSpec(CommitmentSide.Local, 253, 500_000_000, 499_700_000,
                                      [new SpecHtlc(HtlcDirection.Incoming, 3, 300_000, new Hash(new byte[32]), 500)]);

        // Act
        var balances = LightningService.LndBalances.FromSpec(spec, CommitmentFormat.Anchors, 354, false);

        // Assert: 253 * 1124 / 1000 = 284 sat fee + 660 anchors from the remote side
        Assert.Equal(284ul, balances.CommitFeeSat);
        Assert.Equal(500_000_000ul, balances.LocalMsat);
        Assert.Equal(499_700_000ul - 944_000ul, balances.RemoteMsat);
        Assert.Equal(300_000ul, balances.IncomingHtlcMsat);
    }

    [Fact]
    public void Given_AShortChannelId_When_ToChanId_Then_ItIsTheBolt7Uint64()
    {
        Assert.Equal((150ul << 40) | (7ul << 16) | 1, LightningService.ToChanId(new ShortChannelId(150, 7, 1)));
        Assert.Equal(0ul, LightningService.ToChanId(default));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                "yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy")]
    [InlineData("2036f699aff84a71332da5c1e9005559b0434e893b4b0f450b5a01b5db4b8a612f2f6c25503e820f5cff60c6b862e8950d20c0e3a8db783d3df1b18f2592554631",
                "ry5xpgpx9bf8nc3pwzy61ynimgarguwj8pfo6temmey5ms4mtjo16m5cried7yoxmu9sbtiacmwjkdjyadt4ts5a8w69dccxrsjfkttt")]
    [InlineData("1f5b4172ff40972a8dd3e8c8c3df0991676a5329ff2db351263ce6ca28e855fefc66e12e6bee529a8da4552ea1fa40426b17e1b0eb60342a17134307a04d8e07aa",
                "d7pwnhz9enm1idqu7drc8zaj1fusww3j9hs5gwjg8uucwk8ekz9xa3zbf3i6hww4ts1fkmib9jyrr4azhgaqsabwfemtgoa8wbgahb7k")]
    public void Given_ASignature_When_ZBase32_Then_ItMatchesTv42Zbase32(string hex, string zbase32)
    {
        // Act
        var encoded = ZBase32.Encode(Convert.FromHexString(hex));
        var decoded = ZBase32.Decode(zbase32);

        // Assert (the vectors come from github.com/tv42/zbase32, scripts/lnd-grpc/signmessage-vectors)
        Assert.Equal(zbase32, encoded);
        Assert.Equal(hex, Convert.ToHexStringLower(decoded));
    }

    [Fact]
    public void Given_ANonAlphabetCharacter_When_ZBase32Decoded_Then_FormatException()
    {
        Assert.Throws<FormatException>(() => ZBase32.Decode("ybnd0"));
    }

    [Fact]
    public void Given_Features_When_Mapped_Then_LndNamesAndRequiredBitsComeBack()
    {
        // Arrange
        var features = new FeatureSet();
        features.SetFeature(Feature.PaymentSecret, true);
        features.SetFeature(Feature.BasicMpp, false);
        features.SetFeature(99, true);

        // Act
        var map = LndFeatures.ToMap(features).ToDictionary(p => p.Key, p => p.Value);

        // Assert
        Assert.Equal("payment-addr", map[14].Name);
        Assert.True(map[14].IsRequired);
        Assert.Equal("multi-path-payments", map[17].Name);
        Assert.False(map[17].IsRequired);
        Assert.False(map[99].IsKnown);
        Assert.Equal(string.Empty, map[99].Name);
    }

    [Fact]
    public void Given_WireFeatureBytes_When_Mapped_Then_BitsAreReadBigEndian()
    {
        // 0x02 0x00 0x00: bit 17
        var map = LndFeatures.ToMap(new byte[] { 0x02, 0x00, 0x00 }).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("multi-path-payments", Assert.Single(map).Value.Name);
        Assert.Equal(17u, map.Keys.Single());
    }

    [Fact]
    public void Given_LndPagingArguments_When_PageQuery_Then_BoundsAndDirectionFollow()
    {
        // Act
        var forward = LightningService.PageQuery(7, false, 0, 100, 0, 0);
        var reversed = LightningService.PageQuery(7, true, 5, 100, 0, 0);
        var dated = LightningService.PageQuery(0, false, 50_000, 100, 1_000, 2_000);

        // Assert
        Assert.Equal(7ul, forward.After);
        Assert.Null(forward.Before);
        Assert.True(forward.Ascending);
        Assert.Equal(100, forward.Take);
        Assert.Equal(7ul, reversed.Before);
        Assert.False(reversed.Ascending);
        Assert.Equal(5, reversed.Take);
        Assert.True(dated.Contains(1, DateTimeOffset.FromUnixTimeSeconds(1_000)));
        Assert.True(dated.Contains(1, DateTimeOffset.FromUnixTimeSeconds(2_000).AddMilliseconds(999)));
        Assert.False(dated.Contains(1, DateTimeOffset.FromUnixTimeSeconds(2_001)));
        Assert.False(dated.Contains(null, DateTimeOffset.FromUnixTimeSeconds(1_500)));
        Assert.Equal(10_000, dated.Take);
    }
}