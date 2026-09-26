namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers.Remote;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;

public class RemoteOutputDataTests
{
    private static readonly CompactPubKey s_point =
        new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    [Fact]
    public void Given_EveryField_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Arrange
        var htlc = new SpecHtlc(HtlcDirection.Outgoing, 7, 20_000_000, new Hash(Enumerable.Repeat((byte)9, 32).ToArray()),
                                600);
        var spend = new RemoteRecordedSpend(new TxId(Enumerable.Repeat((byte)3, 32).ToArray()), 502, false,
                                            HtlcSpendPath.HtlcSuccessTransaction,
                                            Enumerable.Repeat((byte)4, 32).ToArray());
        var data = new RemoteOutputData(20_000, 1, true, [0x00, 0x20, 1, 2], [0x51, 0x52], htlc, s_point, true, spend);

        // Act
        var decoded = RemoteOutputData.Decode(data.Encode());

        // Assert
        Assert.Equal(data.AmountSat, decoded.AmountSat);
        Assert.Equal(data.CsvDelay, decoded.CsvDelay);
        Assert.True(decoded.HasAnchors);
        Assert.Equal(data.ScriptPubKey, decoded.ScriptPubKey);
        Assert.Equal(data.WitnessScript, decoded.WitnessScript);
        Assert.Equal(htlc, decoded.Htlc);
        Assert.Equal(s_point, decoded.RemotePerCommitmentPoint);
        Assert.True(decoded.UpstreamRaised);
        Assert.NotNull(decoded.Spend);
        Assert.Equal(spend.SpendingTxId, decoded.Spend.SpendingTxId);
        Assert.Equal(spend.Height, decoded.Spend.Height);
        Assert.Equal(spend.Path, decoded.Spend.Path);
        Assert.Equal(spend.Preimage, decoded.Spend.Preimage);
    }

    [Fact]
    public void Given_P2wpkhToRemoteWithoutOptionalFields_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Arrange
        var data = new RemoteOutputData(800_000, 0, false, [0x00, 0x14, 5, 6], null, null, null);

        // Act
        var decoded = RemoteOutputData.Decode(data.Encode());

        // Assert
        Assert.Null(decoded.WitnessScript);
        Assert.Null(decoded.Htlc);
        Assert.Null(decoded.RemotePerCommitmentPoint);
        Assert.Null(decoded.Spend);
        Assert.False(decoded.UpstreamRaised);
        Assert.Equal(800_000UL, decoded.AmountSat);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 2, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0 })]
    public void Given_TruncatedOrUnknownData_When_Decoded_Then_FormatException(byte[] bytes)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => RemoteOutputData.Decode(bytes));
    }

    [Fact]
    public void Given_TrailingBytes_When_Decoded_Then_FormatException()
    {
        // Arrange
        var bytes = new RemoteOutputData(1, 0, false, [0x51], null, null, null).Encode();

        // Act / Assert
        Assert.Throws<FormatException>(() => RemoteOutputData.Decode([.. bytes, 0]));
    }
}