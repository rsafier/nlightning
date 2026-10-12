using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Tests.Crypto.Functions;

using Infrastructure.Crypto.Functions;

/// <summary>
/// Test our HMAC-SHA256 implementation against the test vectors from
/// <see href="https://tools.ietf.org/html/rfc4231">RFC 4231</see>.
/// </summary>
public class HmacSha256Tests
{
    public static TheoryData<string, string, string, string> Rfc4231Vectors => new()
    {
        // Test Case 1
        {
            "1",
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            Convert.ToHexString("Hi There"u8),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"
        },
        // Test Case 2 (key shorter than the output length)
        {
            "2",
            Convert.ToHexString("Jefe"u8),
            Convert.ToHexString("what do ya want for nothing?"u8),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"
        },
        // Test Case 3 (combined key and data length larger than 64 bytes)
        {
            "3",
            new string('a', 40),
            new string('d', 100),
            "773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe"
        },
        // Test Case 4 (combined key and data length larger than 64 bytes)
        {
            "4",
            "0102030405060708090a0b0c0d0e0f10111213141516171819",
            string.Concat(Enumerable.Repeat("cd", 50)),
            "82558a389a443c0ea4cc819899f2083a85f0faa3e578f8077a2e3ff46729665b"
        },
        // Test Case 6 (key larger than 128 bytes, hashed first)
        {
            "6",
            new string('a', 262),
            Convert.ToHexString("Test Using Larger Than Block-Size Key - Hash Key First"u8),
            "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54"
        },
        // Test Case 7 (key and data larger than 128 bytes)
        {
            "7",
            new string('a', 262),
            Convert.ToHexString(
                "This is a test using a larger than block-size key and a larger than block-size data. The key needs to be hashed before being used by the HMAC algorithm."u8),
            "9b09ffa71b942fcb27635fbcd5b0e944bfdc63644f0713938a7f51535c3a35e2"
        }
    };

    [Theory]
    [MemberData(nameof(Rfc4231Vectors))]
    public void Given_Rfc4231Vector_When_ComputeHash_Then_ResultMatchesExpected(string testCase, string keyHex,
                                                                                 string dataHex, string expectedHex)
    {
        // Arrange
        var key = Convert.FromHexString(keyHex);
        var data = Convert.FromHexString(dataHex);
        var expected = Convert.FromHexString(expectedHex);
        using var hmac = new HmacSha256();
        var result = new byte[32];

        // Act
        hmac.ComputeHash(key, data, result);

        // Assert
        Assert.True(expected.AsSpan().SequenceEqual(result), $"RFC 4231 test case {testCase} failed");
        Assert.Equal(HMACSHA256.HashData(key, data), result);
    }

    [Fact]
    public void Given_SameInstance_When_ComputeHashCalledTwice_Then_ResultsAreIndependent()
    {
        // Arrange
        var longKey = Enumerable.Repeat((byte)0xAA, 131).ToArray();
        var shortKey = "Jefe"u8.ToArray();
        var data = "what do ya want for nothing?"u8.ToArray();
        using var hmac = new HmacSha256();
        var first = new byte[32];
        var second = new byte[32];

        // Act
        hmac.ComputeHash(longKey, data, first);
        hmac.ComputeHash(shortKey, data, second);

        // Assert
        Assert.Equal(HMACSHA256.HashData(longKey, data), first);
        Assert.Equal(HMACSHA256.HashData(shortKey, data), second);
    }

    [Theory]
    [InlineData("rho")]
    [InlineData("mu")]
    [InlineData("um")]
    [InlineData("pad")]
    [InlineData("ammag")]
    [InlineData("ammagext")]
    public void Given_OnionKeyTypeLabel_When_ComputeHash_Then_ResultMatchesBclHmac(string label)
    {
        // Arrange
        var key = Encoding.ASCII.GetBytes(label);
        var sharedSecret = Convert.FromHexString("53eb63ea8a3fec3b3cd433b85cd62a4b145e1dda09391b348c4e1cd36a03ea66");
        using var hmac = new HmacSha256();
        var result = new byte[32];

        // Act
        hmac.ComputeHash(key, sharedSecret, result);

        // Assert
        Assert.Equal(HMACSHA256.HashData(key, sharedSecret), result);
    }

    [Fact]
    public void Given_TwoDataParts_When_ComputeHash_Then_ResultMatchesConcatenatedData()
    {
        // Arrange
        var key = "mu"u8.ToArray();
        var data1 = Enumerable.Range(0, 1300).Select(i => (byte)i).ToArray();
        var data2 = Enumerable.Repeat((byte)0x42, 32).ToArray();
        using var hmac = new HmacSha256();
        var result = new byte[32];

        // Act
        hmac.ComputeHash(key, data1, data2, result);

        // Assert
        Assert.Equal(HMACSHA256.HashData(key, data1.Concat(data2).ToArray()), result);
    }

    [Fact]
    public void Given_EmptyKeyAndData_When_ComputeHash_Then_ResultMatchesBclHmac()
    {
        // Arrange
        using var hmac = new HmacSha256();
        var result = new byte[32];

        // Act
        hmac.ComputeHash(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, result);

        // Assert
        Assert.Equal(HMACSHA256.HashData(Array.Empty<byte>(), Array.Empty<byte>()), result);
    }

    [Fact]
    public void Given_KeyOfExactlyBlockSize_When_ComputeHash_Then_KeyIsNotHashed()
    {
        // Arrange
        var key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var data = "block"u8.ToArray();
        using var hmac = new HmacSha256();
        var result = new byte[32];

        // Act
        hmac.ComputeHash(key, data, result);

        // Assert
        Assert.Equal(HMACSHA256.HashData(key, data), result);
    }

    [Fact]
    public void Given_OutputOverlappingData_When_ComputeHash_Then_ResultIsCorrect()
    {
        // Arrange
        var key = "rho"u8.ToArray();
        var buffer = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expected = HMACSHA256.HashData(key, buffer);
        using var hmac = new HmacSha256();

        // Act
        hmac.ComputeHash(key, buffer, buffer);

        // Assert
        Assert.Equal(expected, buffer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Given_WrongOutputLength_When_ComputeHash_Then_ThrowsArgumentException(int outputLength)
    {
        // Arrange
        using var hmac = new HmacSha256();
        var output = new byte[outputLength];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => hmac.ComputeHash("key"u8, "data"u8, output));
    }

    [Fact]
    public void Given_DisposedInstance_When_ComputeHash_Then_ThrowsObjectDisposedException()
    {
        // Arrange
        var hmac = new HmacSha256();
        hmac.Dispose();
        var output = new byte[32];

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => hmac.ComputeHash("key"u8, "data"u8, output));
    }
}