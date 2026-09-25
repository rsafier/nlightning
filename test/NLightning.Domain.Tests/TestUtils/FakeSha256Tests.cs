using System.Text;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.TestUtils;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Models;

public class FakeSha256Tests
{
    [Fact]
    public void Given_BareFakeSha256_When_HashingAbc_Then_ReturnsRealSha256Digest()
    {
        // Arrange
        using var sha256 = new FakeSha256();
        var hash = new byte[32];

        // Act
        sha256.AppendData(Encoding.ASCII.GetBytes("abc"));
        sha256.GetHashAndReset(hash);

        // Assert - FIPS 180-2 "abc" test vector
        Assert.Equal(Convert.FromHexString("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"), hash);
    }

    [Fact]
    public void Given_BareFakeSha256_When_HashingTwice_Then_StateIsReset()
    {
        // Arrange
        using var sha256 = new FakeSha256();
        var first = new byte[32];
        var second = new byte[32];

        // Act
        sha256.AppendData(Encoding.ASCII.GetBytes("abc"));
        sha256.GetHashAndReset(first);
        sha256.AppendData(Encoding.ASCII.GetBytes("abc"));
        sha256.GetHashAndReset(second);

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void Given_BareFakeSha256_When_CalculatingBolt3ObscuringFactor_Then_MatchesSpecValue()
    {
        // Arrange - BOLT 3 Appendix C payment basepoints
        CompactPubKey localPaymentBasepoint =
            Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");
        CompactPubKey remotePaymentBasepoint =
            Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

        // Act
        var commitmentNumber = new CommitmentNumber(localPaymentBasepoint, remotePaymentBasepoint, new FakeSha256());

        // Assert
        Assert.Equal(0x2bb038521914UL, commitmentNumber.ObscuringFactor);
    }
}