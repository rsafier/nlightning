using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Models;

/// <summary>
/// The engine ↔ transaction builder seam (NL-230): <see cref="CommitmentTxSpec.FromCommitmentSpec"/>,
/// <see cref="CommitmentParams.FromChannel"/> and <see cref="CommitmentTxSignatures.ToCommitmentSignatures"/>.
/// </summary>
public class CommitmentSeamTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
    };

    [Theory]
    [InlineData(CommitmentSide.Local)]
    [InlineData(CommitmentSide.Remote)]
    public void Given_EngineSpec_When_Adapted_Then_LocalViewBalancesFeerateAndHtlcsCarryOver(CommitmentSide holder)
    {
        // Arrange
        var hashA = new Hash(Enumerable.Repeat((byte)0xAA, 32).ToArray());
        var hashB = new Hash(Enumerable.Repeat((byte)0xBB, 32).ToArray());
        var spec = new CommitmentSpec(holder, 2_500, 7_000_123, 2_000_456,
                                      [
                                          new SpecHtlc(HtlcDirection.Incoming, 4, 500_001, hashB, 701),
                                          new SpecHtlc(HtlcDirection.Outgoing, 9, 499_420, hashA, 700)
                                      ]);

        // Act
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);

        // Assert - both are net, local-node views, whichever side holds the commitment
        Assert.Equal(7_000_123UL, txSpec.ToLocalMsat);
        Assert.Equal(2_000_456UL, txSpec.ToRemoteMsat);
        Assert.Equal(2_500UL, txSpec.FeeRatePerKw);
        Assert.Collection(txSpec.Htlcs,
                          h =>
                          {
                              Assert.Equal(HtlcDirection.Incoming, h.Direction);
                              Assert.Equal(4UL, h.Id);
                              Assert.Equal(500_001UL, h.Amount.MilliSatoshi);
                              Assert.Equal(hashB, h.PaymentHash);
                              Assert.Equal(701U, h.CltvExpiry);
                          },
                          h =>
                          {
                              Assert.Equal(HtlcDirection.Outgoing, h.Direction);
                              Assert.Equal(9UL, h.Id);
                              Assert.Equal(499_420UL, h.Amount.MilliSatoshi);
                              Assert.Equal(hashA, h.PaymentHash);
                              Assert.Equal(700U, h.CltvExpiry);
                          });
    }

    [Fact]
    public void Given_Channel_When_BuildingEngineParams_Then_EachSideKeepsItsAnnouncedValues()
    {
        // Arrange - every value differs between the sides
        var local = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_001), 30, LightningMoney.MilliSatoshis(900_000_000),
                                     144);
        var remote = new ChannelParty(LightningMoney.Satoshis(600), LightningMoney.Satoshis(12_000),
                                      LightningMoney.MilliSatoshis(2_002), 483,
                                      LightningMoney.MilliSatoshis(800_000_000), 100);
        var channel = CreateChannel(new ChannelParams(local, remote, LightningMoney.Satoshis(253), 3, true,
                                                      FeatureSupport.No), isInitiator: false);

        // Act
        var p = CommitmentParams.FromChannel(channel, 5_000_000);

        // Assert
        Assert.False(p.LocalIsFunder);
        Assert.Equal(1_000_000UL, p.FundingSatoshis);
        Assert.True(p.OptionAnchors);
        Assert.Equal(new CommitmentParty(546, 10_000, 1_001, 30, 900_000_000), p.Local);
        Assert.Equal(new CommitmentParty(600, 12_000, 2_002, 483, 800_000_000), p.Remote);
        Assert.Equal(12_000_000UL, p.LocalReserveMsat);
        Assert.Equal(10_000_000UL, p.RemoteReserveMsat);
        Assert.Equal(5_000_000UL, p.MaxDustHtlcExposureMsat);
    }

    [Fact]
    public void Given_ChannelWithoutFundingOutput_When_BuildingEngineParams_Then_Throws()
    {
        // Arrange
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Zero, LightningMoney.Zero, 30,
                                     LightningMoney.Zero, 144);
        var channel = CreateChannel(new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false,
                                                      FeatureSupport.No), isInitiator: true, withFunding: false);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => CommitmentParams.FromChannel(channel));
    }

    [Fact]
    public void Given_TxSignatures_When_ConvertedForTheEngine_Then_SignaturesKeptInOrder()
    {
        // Arrange
        var first = new CompactSignature(Enumerable.Repeat((byte)1, 64).ToArray());
        var second = new CompactSignature(Enumerable.Repeat((byte)2, 64).ToArray());
        var third = new CompactSignature(Enumerable.Repeat((byte)3, 64).ToArray());
        var txSignatures = new CommitmentTxSignatures(TxId.One, first, [second, third]);

        // Act
        var signatures = txSignatures.ToCommitmentSignatures();

        // Assert
        Assert.Equal(first, signatures.Signature);
        Assert.Equal([second, third], signatures.HtlcSignatures);
    }

    private static ChannelModel CreateChannel(ChannelParams channelParams, bool isInitiator, bool withFunding = true)
    {
        var fundingOutput = withFunding
                                ? new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_pubKey, s_pubKey,
                                                        TxId.One, 0)
                                : null;
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        return new ChannelModel(channelParams, ChannelId.Zero, new CommitmentNumber(s_pubKey, s_pubKey,
                                                                                     new FakeSha256()),
                                fundingOutput, isInitiator, null, null, LightningMoney.Satoshis(1_000_000), keySet, 0,
                                0, LightningMoney.Zero, keySet, 0, s_pubKey, 0, ChannelState.Open, ChannelVersion.V1);
    }
}