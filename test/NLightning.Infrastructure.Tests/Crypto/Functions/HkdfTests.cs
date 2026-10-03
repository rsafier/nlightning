namespace NLightning.Infrastructure.Tests.Crypto.Functions;

using Infrastructure.Crypto.Functions;
using Infrastructure.Crypto.Primitives;

public class HkdfTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(33)]
    public void Given_ChainingKeyOfWrongLength_When_ExtractAndExpand2_Then_ThrowsArgumentException(int keyLength)
    {
        // Arrange
        using var hkdf = new Hkdf();
        using var chainingKey = new SecureMemory(keyLength);
        var output = new byte[64];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => hkdf.ExtractAndExpand2(chainingKey, [], output));
    }

    [Fact]
    public void Given_OutputOfWrongLength_When_ExtractAndExpand2_Then_ThrowsArgumentException()
    {
        // Arrange
        using var hkdf = new Hkdf();
        using var chainingKey = new SecureMemory(32);
        var output = new byte[96];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => hkdf.ExtractAndExpand2(chainingKey, [], output));
    }

    [Fact]
    public void Given_ChainingKeyOfWrongLength_When_ExtractAndExpand3_Then_ThrowsArgumentException()
    {
        // Arrange
        using var hkdf = new Hkdf();
        using var chainingKey = new SecureMemory(16);
        var output = new byte[96];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => hkdf.ExtractAndExpand3(chainingKey, [], output));
    }

    [Fact]
    public void Given_OutputOfWrongLength_When_ExtractAndExpand3_Then_ThrowsArgumentException()
    {
        // Arrange
        using var hkdf = new Hkdf();
        using var chainingKey = new SecureMemory(32);
        var output = new byte[128];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => hkdf.ExtractAndExpand3(chainingKey, [], output));
    }

    [Fact]
    public void Given_ValidLengths_When_ExtractAndExpand2_Then_OutputsAreDistinctAndNonZero()
    {
        // Arrange
        using var hkdf = new Hkdf();
        using var chainingKey = new SecureMemory(32);
        var output = new byte[64];

        // Act
        hkdf.ExtractAndExpand2(chainingKey, new byte[32], output);

        // Assert
        Assert.NotEqual(output[..32], output[32..]);
        Assert.Contains(output, b => b != 0);
    }
}