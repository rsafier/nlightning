namespace NLightning.Infrastructure.Tests.Protocol.Validators;

using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;
using Infrastructure.Protocol.Validators;

public class TxSerialIdParityTests
{
    private static readonly LightningMoney s_dustLimit = LightningMoney.Satoshis(354);

    private static Task ValidateAddInputAsync(bool isSenderInitiator, ulong serialId) =>
        TxAddInputValidator.ValidateAsync(isSenderInitiator,
                                          new TxAddInputPayload(ChannelId.Zero, serialId, [0x01], 0, 0xFFFFFFFD),
                                          0, _ => Task.FromResult(true), (_, _) => true, _ => true);

    private static void ValidateAddOutput(bool isSenderInitiator, ulong serialId) =>
        TxAddOutputValidator.Validate(isSenderInitiator,
                                      new TxAddOutputPayload(LightningMoney.Satoshis(10_000), ChannelId.Zero,
                                                             new byte[] { 0x00, 0x14 }, serialId),
                                      0, _ => true, _ => true, s_dustLimit);

    private static void ValidateRemoveInput(bool isSenderInitiator, ulong serialId) =>
        TxRemoveInputValidator.Validate(isSenderInitiator, new TxRemoveInputPayload(ChannelId.Zero, serialId),
                                        _ => true);

    private static void ValidateRemoveOutput(bool isSenderInitiator, ulong serialId) =>
        TxRemoveOutputValidator.Validate(isSenderInitiator, new TxRemoveOutputPayload(ChannelId.Zero, serialId),
                                         _ => true);

    [Theory]
    [InlineData(true, 0UL)]
    [InlineData(true, 2UL)]
    [InlineData(false, 1UL)]
    [InlineData(false, 3UL)]
    public async Task Given_SerialIdWithSenderParity_When_Validating_Then_AllValidatorsAccept(
        bool isSenderInitiator, ulong serialId)
    {
        // Arrange
        // (serial_id parity matches the sender role: initiator even, non-initiator odd)

        // Act
        var addInput = await Record.ExceptionAsync(() => ValidateAddInputAsync(isSenderInitiator, serialId));
        var addOutput = Record.Exception(() => ValidateAddOutput(isSenderInitiator, serialId));
        var removeInput = Record.Exception(() => ValidateRemoveInput(isSenderInitiator, serialId));
        var removeOutput = Record.Exception(() => ValidateRemoveOutput(isSenderInitiator, serialId));

        // Assert
        Assert.Null(addInput);
        Assert.Null(addOutput);
        Assert.Null(removeInput);
        Assert.Null(removeOutput);
    }

    [Theory]
    [InlineData(true, 1UL)]
    [InlineData(false, 0UL)]
    [InlineData(false, 2UL)]
    public async Task Given_SerialIdWithWrongParityForSender_When_Validating_Then_AllValidatorsReject(
        bool isSenderInitiator, ulong serialId)
    {
        // Arrange
        const string expectedMessage = "SerialId has the wrong parity.";

        // Act
        var addInput = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ValidateAddInputAsync(isSenderInitiator, serialId));
        var addOutput = Assert.Throws<InvalidOperationException>(() =>
            ValidateAddOutput(isSenderInitiator, serialId));
        var removeInput = Assert.Throws<InvalidOperationException>(() =>
            ValidateRemoveInput(isSenderInitiator, serialId));
        var removeOutput = Assert.Throws<InvalidOperationException>(() =>
            ValidateRemoveOutput(isSenderInitiator, serialId));

        // Assert
        Assert.Equal(expectedMessage, addInput.Message);
        Assert.Equal(expectedMessage, addOutput.Message);
        Assert.Equal(expectedMessage, removeInput.Message);
        Assert.Equal(expectedMessage, removeOutput.Message);
    }
}