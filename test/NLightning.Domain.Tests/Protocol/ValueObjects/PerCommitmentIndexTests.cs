using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Protocol.Models;

namespace NLightning.Domain.Tests.Protocol.ValueObjects;

public class PerCommitmentIndexTests
{
    [Theory]
    [InlineData(0UL, 281474976710655UL)]
    [InlineData(1UL, 281474976710654UL)]
    [InlineData(2UL, 281474976710653UL)]
    [InlineData(281474976710655UL, 0UL)]
    public void Given_CommitmentNumber_When_From_Then_IndexIsFirstIndexMinusNumber(ulong number, ulong expectedIndex)
    {
        // Act
        var index = PerCommitmentIndex.From(number);

        // Assert - BOLT 3: indices count down from 2^48-1 while commitment numbers count up from 0
        Assert.Equal(expectedIndex, index);
        Assert.Equal(CryptoConstants.FirstPerCommitmentIndex - number, index);
        Assert.Equal(number, PerCommitmentIndex.ToCommitmentNumber(index));
    }

    [Fact]
    public void Given_NumberAbove48Bits_When_From_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => PerCommitmentIndex.From(1UL << 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerCommitmentIndex.ToCommitmentNumber(1UL << 48));
    }
}