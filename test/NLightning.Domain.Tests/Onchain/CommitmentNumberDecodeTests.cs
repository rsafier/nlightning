namespace NLightning.Domain.Tests.Onchain;

using Domain.Protocol.Models;

/// <summary>
/// BOLT 5 plan O2-T3: <see cref="CommitmentNumber.Decode"/> reverses the BOLT 3 obscuring of locktime and sequence.
/// </summary>
public class CommitmentNumberDecodeTests
{
    [Fact]
    public void Given_AppendixCCommitmentFields_When_Decoding_Then_Number42()
    {
        // Arrange: Appendix C obscured number is 0x2bb038521914 ^ 42 = 0x2bb03852193e
        var helper = OnchainTestData.AppendixCCommitmentNumberHelper();

        // Act
        var number = helper.Decode(OnchainTestData.AppendixCLockTime, OnchainTestData.AppendixCSequence);

        // Assert
        Assert.Equal(OnchainTestData.AppendixCObscuringFactor, helper.ObscuringFactor);
        Assert.Equal(OnchainTestData.AppendixCCommitmentNumber, number);
    }

    [Fact]
    public void Given_AppendixCCommitmentFields_When_ExtractingObscured_Then_FactorXor42()
    {
        // Act
        var ok = CommitmentNumber.TryGetObscured(OnchainTestData.AppendixCLockTime, OnchainTestData.AppendixCSequence,
                                                 out var obscured);

        // Assert
        Assert.True(ok);
        Assert.Equal(0x2bb038521914UL ^ 42, obscured);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(0xFFFFFFUL)]
    [InlineData(0x1000000UL)]
    [InlineData(CommitmentNumber.MaxValue)]
    public void Given_EncodedNumber_When_Decoding_Then_RoundTrips(ulong number)
    {
        // Arrange
        var helper = OnchainTestData.AppendixCCommitmentNumberHelper();

        // Act
        var decoded = helper.Decode(helper.LockTime(number), helper.Sequence(number));

        // Assert
        Assert.Equal(number, decoded);
    }

    [Theory]
    [InlineData(0x0052193eU, 0x802bb038U)] // locktime prefix 0x00 (a block height)
    [InlineData(0x2152193eU, 0x802bb038U)] // locktime prefix 0x21
    [InlineData(0x2052193eU, 0xFFFFFFFFU)] // final sequence (a closing transaction)
    [InlineData(0x2052193eU, 0x002bb038U)] // sequence prefix 0x00
    [InlineData(0U, 0xFFFFFFFDU)]
    public void Given_NonCommitmentFields_When_Decoding_Then_Null(uint lockTime, uint sequence)
    {
        // Arrange
        var helper = OnchainTestData.AppendixCCommitmentNumberHelper();

        // Act
        var decoded = helper.Decode(lockTime, sequence);

        // Assert
        Assert.Null(decoded);
        Assert.False(CommitmentNumber.TryGetObscured(lockTime, sequence, out _));
    }
}