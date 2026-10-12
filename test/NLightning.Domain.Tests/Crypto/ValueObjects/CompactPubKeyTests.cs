namespace NLightning.Domain.Tests.Crypto.ValueObjects;

using Domain.Crypto.ValueObjects;

public class CompactPubKeyTests
{
    private static byte[] ValidKeyBytes()
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = 0x01;
        return bytes;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_ConditionalWithBareNull_When_Evaluated_Then_YieldsNullableKeyWithoutThrowing(bool isNull)
    {
        // Arrange
        var bytes = ValidKeyBytes();

        // Act
        CompactPubKey? key = isNull ? null : new CompactPubKey(bytes);

        // Assert
        Assert.Equal(isNull, key is null);
    }

    [Fact]
    public void Given_ByteArray_When_ImplicitlyConverted_Then_KeyHasSameBytes()
    {
        // Arrange
        var bytes = ValidKeyBytes();

        // Act
        CompactPubKey key = bytes;

        // Assert
        Assert.Equal(bytes, (byte[])key);
        Assert.Equal(new CompactPubKey(ValidKeyBytes()), key);
    }

    [Fact]
    public void Given_NullByteArray_When_Constructing_Then_ThrowsArgumentNullException()
    {
        // Act / Assert
        Assert.Throws<ArgumentNullException>(() => new CompactPubKey(null!));
    }

    [Fact]
    public void Given_NullByteArrayVariable_When_ImplicitlyConverted_Then_ThrowsArgumentException()
    {
        // Arrange
        byte[]? bytes = null;

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
        {
            CompactPubKey _ = bytes;
        });
    }

    [Fact]
    public void Given_DefaultKey_When_Hashing_Then_DoesNotThrow()
    {
        // Act / Assert
        Assert.Equal(0, default(CompactPubKey).GetHashCode());
        Assert.True(default(CompactPubKey) == default);
    }
}