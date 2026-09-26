namespace NLightning.Domain.Tests.Onchain;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

public class OutputDescriptorDataTests
{
    private static readonly CompactPubKey s_point =
        Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");

    [Fact]
    public void Given_HtlcOutputWithEverything_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Arrange
        var htlc = new SpecHtlc(HtlcDirection.Outgoing, 7, 2_000_000, new Hash(Enumerable.Repeat((byte)9, 32).ToArray()),
                                500);
        var data = new OutputDescriptorData(2_000, [0x00, 0x20, 1, 2, 3], [0xAA, 0xBB], 1, true, s_point, htlc);

        // Act
        var decoded = OutputDescriptorData.Decode(data.Encode());

        // Assert
        Assert.Equal(data.AmountSat, decoded.AmountSat);
        Assert.Equal(data.ScriptPubKey, decoded.ScriptPubKey);
        Assert.Equal(data.WitnessScript, decoded.WitnessScript);
        Assert.Equal(data.CsvDelay, decoded.CsvDelay);
        Assert.True(decoded.HasAnchors);
        Assert.Equal(s_point, decoded.PerCommitmentPoint);
        Assert.Equal(htlc, decoded.Htlc);
    }

    [Fact]
    public void Given_P2WpkhOutputWithoutPointOrHtlc_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Arrange
        var data = new OutputDescriptorData(10_000, [0x00, 0x14, 5], null, 0, false, null, null);

        // Act
        var decoded = OutputDescriptorData.Decode(data.Encode());

        // Assert
        Assert.Null(decoded.WitnessScript);
        Assert.Null(decoded.PerCommitmentPoint);
        Assert.Null(decoded.Htlc);
        Assert.False(decoded.HasAnchors);
        Assert.Equal(10_000UL, decoded.AmountSat);
    }

    [Fact]
    public void Given_TruncatedOrUnknownBytes_When_TryDecoding_Then_Null()
    {
        // Arrange
        var bytes = new OutputDescriptorData(1, [1, 2, 3], [4], 0, false, s_point, null).Encode();
        var row = new OutputResolutionModel
        {
            TransactionId = new byte[32],
            OutputIndex = 0,
            ChannelId = new byte[32],
            Descriptor = OutputDescriptorKind.DelayedToLocal,
            DescriptorData = bytes[..^1]
        };

        // Act / Assert
        Assert.Null(OutputDescriptorData.TryDecode(row));
        Assert.Null(OutputDescriptorData.TryDecode(row with { DescriptorData = [] }));
        Assert.Throws<FormatException>(() => OutputDescriptorData.Decode([2, 0, 0]));
    }
}