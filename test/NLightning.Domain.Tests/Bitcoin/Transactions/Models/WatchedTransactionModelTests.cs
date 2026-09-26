namespace NLightning.Domain.Tests.Bitcoin.Transactions.Models;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

public class WatchedTransactionModelTests
{
    [Fact]
    public void Given_TxIndexAbove16Bits_When_SettingHeightAndIndex_Then_IndexIsKept()
    {
        // Arrange
        var model = new WatchedTransactionModel(new ChannelId(new byte[32]), new TxId(new byte[32]), 3);

        // Act
        model.SetHeightAndIndex(800_000, ShortChannelId.MaxThreeByteValue);

        // Assert
        Assert.Equal(800_000u, model.FirstSeenAtHeight);
        Assert.Equal(ShortChannelId.MaxThreeByteValue, model.TransactionIndex);
    }

    [Fact]
    public void Given_TxIndexAbove24Bits_When_SettingHeightAndIndex_Then_Throws()
    {
        // Arrange
        var model = new WatchedTransactionModel(new ChannelId(new byte[32]), new TxId(new byte[32]), 3);

        // Act
        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => model.SetHeightAndIndex(1, ShortChannelId.MaxThreeByteValue + 1));
        Assert.Null(model.TransactionIndex);
    }
}