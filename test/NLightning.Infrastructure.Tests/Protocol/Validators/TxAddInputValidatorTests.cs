namespace NLightning.Infrastructure.Tests.Protocol.Validators;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Payloads;
using Infrastructure.Protocol.Validators;

public class TxAddInputValidatorTests
{
    private static TxAddInputPayload CreateInput(ulong serialId) =>
        new(ChannelId.Zero, serialId, [0x01, 0x02, 0x03], 0, 0xFFFFFFFD);

    [Fact]
    public async Task Given_PrevTxCheckFailsAsynchronously_When_ValidatingAsync_Then_ExceptionIsObservedByCaller()
    {
        // Arrange
        var input = CreateInput(0);

        async Task<bool> IsValidPrevTx(byte[] _)
        {
            await Task.Yield();
            return false;
        }

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TxAddInputValidator.ValidateAsync(true, input, 0, IsValidPrevTx, (_, _) => true, _ => true));

        // Assert
        Assert.Equal("PrevTx is not a valid transaction.", exception.Message);
    }

    [Fact]
    public async Task Given_PrevTxCheckThrows_When_ValidatingAsync_Then_ExceptionPropagatesToCaller()
    {
        // Arrange
        var input = CreateInput(0);

        async Task<bool> IsValidPrevTx(byte[] _)
        {
            await Task.Yield();
            throw new TimeoutException("bitcoind unreachable");
        }

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            TxAddInputValidator.ValidateAsync(true, input, 0, IsValidPrevTx, (_, _) => true, _ => true));
    }

    [Fact]
    public async Task Given_ValidInput_When_ValidatingAsync_Then_DoesNotThrow()
    {
        // Arrange
        var input = CreateInput(0);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            TxAddInputValidator.ValidateAsync(true, input, 0, _ => Task.FromResult(true), (_, _) => true,
                                              _ => true));

        // Assert
        Assert.Null(exception);
    }
}