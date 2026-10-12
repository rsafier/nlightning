namespace NLightning.Domain.Tests.Crypto.ValueObjects;

using Domain.Crypto.ValueObjects;

public class CompactSignatureZeroTests
{
    [Fact]
    public void Given_Zero_When_Read_Then_SixtyFourZeroBytesAndIsZero()
    {
        // Act
        var zero = CompactSignature.Zero;

        // Assert (the signature field of a simple taproot funding_created/funding_signed/commitment_signed)
        Assert.Equal(new byte[64], zero.Value);
        Assert.True(zero.IsZero);
        Assert.NotSame(zero.Value, CompactSignature.Zero.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    public void Given_SignatureWithOneNonZeroByte_When_IsZero_Then_False(int index)
    {
        // Arrange
        var bytes = new byte[64];
        bytes[index] = 1;

        // Act / Assert
        Assert.False(new CompactSignature(bytes).IsZero);
    }

    [Fact]
    public void Given_SixtyThreeZeroBytes_When_IsZero_Then_False()
    {
        // Act / Assert (only the 64-byte form is the placeholder)
        Assert.False(new CompactSignature(new byte[63]).IsZero);
    }
}