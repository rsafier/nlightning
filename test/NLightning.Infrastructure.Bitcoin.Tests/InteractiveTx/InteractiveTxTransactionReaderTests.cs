using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.InteractiveTx;

using Infrastructure.Bitcoin.InteractiveTx;

public class InteractiveTxTransactionReaderTests
{
    [Theory]
    [InlineData("00")] // no item
    [InlineData("0100")] // one empty item
    [InlineData("0201010203fd")] // two items
    public void Given_AWitnessSerialization_When_ReadingAndWriting_Then_ItRoundTrips(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var read = InteractiveTxTransactionReader.TryReadWitness(bytes, out var witness);
        var written = InteractiveTxTransactionReader.WriteWitness(witness!);

        // Assert
        Assert.True(read);
        Assert.Equal(bytes, written);
    }

    [Fact]
    public void Given_AnItemOf253Bytes_When_Writing_Then_ItsLengthIsAThreeByteCompactSize()
    {
        // Arrange
        var item = Enumerable.Repeat((byte)0xAB, 253).ToArray();

        // Act
        var written = InteractiveTxTransactionReader.WriteWitness(new WitScript(Op.GetPushOp(item)));
        var read = InteractiveTxTransactionReader.TryReadWitness(written, out var witness);

        // Assert
        Assert.Equal([0x01, 0xFD, 0xFD, 0x00], written[..4]);
        Assert.True(read);
        Assert.Equal(item, witness!.Pushes.Single());
    }

    [Theory]
    [InlineData("")] // no count
    [InlineData("01")] // item missing
    [InlineData("010201")] // item truncated
    [InlineData("01010100")] // trailing byte
    [InlineData("01fd0100" + "00")] // non-canonical length
    [InlineData("fe00000100")] // absurd count
    public void Given_AMalformedWitness_When_Reading_Then_ItIsRefused(string hex)
    {
        // Act
        var read = InteractiveTxTransactionReader.TryReadWitness(Convert.FromHexString(hex), out var witness);

        // Assert
        Assert.False(read);
        Assert.Null(witness);
    }
}