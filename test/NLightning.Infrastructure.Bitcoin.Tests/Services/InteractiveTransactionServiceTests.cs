namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;

public class InteractiveTransactionServiceTests
{
    private static readonly LightningMoney s_dustLimit = LightningMoney.Satoshis(354);

    private static TxAddInputPayload CreateInput(ulong serialId, byte prevTxByte = 0x01) =>
        new(ChannelId.Zero, serialId, [prevTxByte], 0, 0xFFFFFFFD);

    private static TxAddOutputPayload CreateOutput(ulong serialId) =>
        new(LightningMoney.Satoshis(10_000), ChannelId.Zero, new byte[] { 0x00, 0x14 }, serialId);

    [Fact]
    public async Task Given_LocalInitiator_When_PeerAddsInputWithEvenSerialId_Then_Throws()
    {
        // Arrange
        var service = new InteractiveTransactionService(s_dustLimit, isInitiator: true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddInputAsync(CreateInput(0)));
        Assert.Equal("SerialId has the wrong parity.", exception.Message);
    }

    [Fact]
    public async Task Given_LocalInitiator_When_PeerAddsInputWithOddSerialId_Then_Accepts()
    {
        // Arrange
        var service = new InteractiveTransactionService(s_dustLimit, isInitiator: true);

        // Act
        var exception = await Record.ExceptionAsync(() => service.AddInputAsync(CreateInput(1)));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_LocalNonInitiator_When_PeerAddsOutputWithOddSerialId_Then_Throws()
    {
        // Arrange
        var service = new InteractiveTransactionService(s_dustLimit, isInitiator: false);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => service.AddOutput(CreateOutput(1)));
        Assert.Equal("SerialId has the wrong parity.", exception.Message);
    }

    [Fact]
    public void Given_LocalNonInitiator_When_PeerAddsOutputWithEvenSerialId_Then_Accepts()
    {
        // Arrange
        var service = new InteractiveTransactionService(s_dustLimit, isInitiator: false);

        // Act
        var exception = Record.Exception(() => service.AddOutput(CreateOutput(2)));

        // Assert
        Assert.Null(exception);
    }
}