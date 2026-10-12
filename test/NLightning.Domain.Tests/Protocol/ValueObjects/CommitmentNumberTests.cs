using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Models;
using NLightning.Infrastructure.Crypto.Hashes;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Protocol.ValueObjects;

public class CommitmentNumberTests
{
    private const ulong Bolt3CommitmentNumber = 42;
    private const ulong ExpectedObscuringFactor = 0x2bb038521914UL;
    private const ulong ExpectedObscuredValue = Bolt3CommitmentNumber ^ ExpectedObscuringFactor;

    private readonly CompactPubKey _openerPaymentBasepoint =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private readonly CompactPubKey _accepterPaymentBasepoint =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    [Fact]
    public void Given_Bolt3Basepoints_When_ConstructingWithOpenerFirst_Then_ObscuringFactorMatchesSpec()
    {
        // Given
        var sha256 = new Sha256();

        // When
        var commitmentNumber = new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256);

        // Then - BOLT 3 Appendix C: SHA256(opener || accepter) gives obscuring factor 0x2bb038521914
        Assert.Equal(ExpectedObscuringFactor, commitmentNumber.ObscuringFactor);
    }

    [Fact]
    public void Given_BOLT3TestVectors_When_CalculatingObscuringFactor_Then_MatchesExpectedValue()
    {
        // Given
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var obscuringFactor = commitmentNumber.ObscuringFactor;

        // Then - per BOLT3 test vectors, the obscuring factor should be 0x2bb038521914
        Assert.Equal(ExpectedObscuringFactor, obscuringFactor);
    }

    [Fact]
    public void Given_CommitmentNumber_When_Obscure_Then_ReturnsXORedValue()
    {
        // Given
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var obscuredValue = commitmentNumber.Obscure(Bolt3CommitmentNumber);

        // Then
        Assert.Equal(ExpectedObscuredValue, obscuredValue);
    }

    [Fact]
    public void Given_Bolt3CommitmentNumber42_When_CalculatingLockTimeAndSequence_Then_MatchAppendixC()
    {
        // Given - BOLT 3 Appendix C commitment transactions: nLocktime 0x2052193e, nSequence 0x802bb038
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var lockTime = commitmentNumber.LockTime(Bolt3CommitmentNumber);
        var sequence = commitmentNumber.Sequence(Bolt3CommitmentNumber);

        // Then
        Assert.Equal(0x2052193eU, lockTime.ValueOrHeight);
        Assert.Equal(0x802bb038U, sequence.Value);
    }

    [Fact]
    public void Given_CommitmentNumber_When_CalculateLockTime_Then_ReturnsCorrectValue()
    {
        // Given
        const uint expectedLocktime = (uint)((0x20 << 24) | (ExpectedObscuredValue & 0xFFFFFF));
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var lockTime = commitmentNumber.LockTime(Bolt3CommitmentNumber);

        // Then - formula is (0x20 << 24) | (obscured & 0xFFFFFF)
        Assert.Equal(expectedLocktime, lockTime.ValueOrHeight);
    }

    [Fact]
    public void Given_CommitmentNumber_When_CalculateSequence_Then_ReturnsCorrectValue()
    {
        // Given
        const uint expectedSequence = (uint)((0x80U << 24) | ((ExpectedObscuredValue >> 24) & 0xFFFFFF));
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var sequence = commitmentNumber.Sequence(Bolt3CommitmentNumber);

        // Then - formula is (0x80 << 24) | ((obscured >> 24) & 0xFFFFFF)
        Assert.Equal(expectedSequence, sequence.Value);
    }

    [Fact]
    public void Given_OneHelper_When_ObscuringDifferentNumbers_Then_EachNumberGetsItsOwnLockTime()
    {
        // Given - the local and remote commitments share one helper but not one number (NL-188)
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When
        var localLockTime = commitmentNumber.LockTime(1);
        var remoteLockTime = commitmentNumber.LockTime(2);

        // Then
        Assert.Equal((0x20U << 24) | (uint)((1 ^ ExpectedObscuringFactor) & 0xFFFFFF), localLockTime.ValueOrHeight);
        Assert.Equal((0x20U << 24) | (uint)((2 ^ ExpectedObscuringFactor) & 0xFFFFFF), remoteLockTime.ValueOrHeight);
    }

    [Fact]
    public void Given_NumberAbove48Bits_When_Obscure_Then_Throws()
    {
        // Given
        var commitmentNumber = CreateBolt3CommitmentNumber();

        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => commitmentNumber.Obscure(CommitmentNumber.MaxValue + 1));
        Assert.Equal(CommitmentNumber.MaxValue ^ ExpectedObscuringFactor,
                     commitmentNumber.Obscure(CommitmentNumber.MaxValue));
    }

    private CommitmentNumber CreateBolt3CommitmentNumber()
    {
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        return new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object);
    }
}