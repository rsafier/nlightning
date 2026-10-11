namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Validators;

public class MalformedHtlcValidatorTests
{
    private static readonly byte[] s_sentOnionSha256 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData((ushort)FailureCode.InvalidOnionVersion)]
    [InlineData((ushort)FailureCode.InvalidOnionHmac)]
    [InlineData((ushort)FailureCode.InvalidOnionKey)]
    [InlineData((ushort)FailureCode.InvalidOnionBlinding)]
    [InlineData((ushort)0x8001)]
    public void Given_BadOnionCodeAndMatchingHash_When_Validating_Then_Valid(ushort failureCode)
    {
        // Act
        var result = MalformedHtlcValidator.Validate(failureCode, s_sentOnionSha256, s_sentOnionSha256);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.Valid, result);
    }

    [Theory]
    [InlineData((ushort)FailureCode.TemporaryNodeFailure)]
    [InlineData((ushort)FailureCode.InvalidOnionPayload)]
    [InlineData((ushort)0x4005)]
    [InlineData((ushort)0)]
    public void Given_CodeWithoutBadOnionBit_When_Validating_Then_BadOnionBitNotSet(ushort failureCode)
    {
        // Act: the hash also mismatches; the protocol violation wins
        var result = MalformedHtlcValidator.Validate(failureCode, new byte[32], s_sentOnionSha256);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.BadOnionBitNotSet, result);
    }

    [Fact]
    public void Given_DifferentHash_When_Validating_Then_Sha256OfOnionMismatch()
    {
        // Arrange
        var sha256OfOnion = (byte[])s_sentOnionSha256.Clone();
        sha256OfOnion[31] ^= 1;

        // Act
        var result = MalformedHtlcValidator.Validate((ushort)FailureCode.InvalidOnionHmac, sha256OfOnion,
                                                     s_sentOnionSha256);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.Sha256OfOnionMismatch, result);
    }

    [Theory]
    [InlineData((ushort)FailureCode.InvalidOnionBlinding)]
    [InlineData((ushort)FailureCode.InvalidOnionHmac)]
    public void Given_BadOnionCodeAndAllZeroHash_When_Validating_Then_Valid(ushort failureCode)
    {
        // Act: BOLT 2 only allows the alternate path for a mismatching hash that is not all zero
        var result = MalformedHtlcValidator.Validate(failureCode, new byte[32], s_sentOnionSha256);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.Valid, result);
    }

    [Fact]
    public void Given_CodeWithoutBadOnionBitAndAllZeroHash_When_Validating_Then_BadOnionBitNotSet()
    {
        // Act
        var result = MalformedHtlcValidator.Validate((ushort)FailureCode.TemporaryNodeFailure, new byte[32],
                                                     s_sentOnionSha256);

        // Assert
        Assert.Equal(MalformedHtlcCheckResult.BadOnionBitNotSet, result);
    }
}