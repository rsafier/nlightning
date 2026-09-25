using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Models;
using NLightning.Infrastructure.Crypto.Hashes;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Protocol.ValueObjects;

public class CommitmentNumberTests
{
    private const ulong InitialCommitmentNumber = 42;
    private const ulong ExpectedObscuringFactor = 0x2bb038521914UL;
    private const ulong ExpectedObscuredValue = InitialCommitmentNumber ^ ExpectedObscuringFactor;

    private readonly CompactPubKey _openerPaymentBasepoint =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private readonly CompactPubKey _accepterPaymentBasepoint =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    [Fact]
    public void Given_ValidParameters_When_ConstructingCommitmentNumber_Then_PropertiesAreSetCorrectly()
    {
        // Given
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        // When
        var commitmentNumber =
            new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object,
                                 InitialCommitmentNumber);

        // Then
        Assert.Equal(InitialCommitmentNumber, commitmentNumber.Value);
        Assert.NotEqual(0UL, commitmentNumber.ObscuringFactor);
    }

    [Fact]
    public void Given_CommitmentNumber_When_Increment_Then_ReturnsNextValueWithoutMutatingOriginal()
    {
        // Given
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        const ulong expectedCommitmentNumber = InitialCommitmentNumber + 1;
        var commitmentNumber =
            new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object,
                                 InitialCommitmentNumber);

        // When
        var nextCommitmentNumber = commitmentNumber.Increment();

        // Then
        Assert.NotSame(commitmentNumber, nextCommitmentNumber);
        Assert.Equal(InitialCommitmentNumber, commitmentNumber.Value);
        Assert.Equal(expectedCommitmentNumber, nextCommitmentNumber.Value);
        Assert.Equal(commitmentNumber.ObscuringFactor, nextCommitmentNumber.ObscuringFactor);
    }

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
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        var commitmentNumber = new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object);

        // When
        var obscuringFactor = commitmentNumber.ObscuringFactor;

        // Then - per BOLT3 test vectors, the obscuring factor should be 0x2bb038521914
        Assert.Equal(ExpectedObscuringFactor, obscuringFactor);
    }

    [Fact]
    public void Given_CommitmentNumber_When_CalculatingObscuredValue_Then_ReturnsXORedValue()
    {
        // Given
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        var commitmentNumber =
            new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object,
                                 InitialCommitmentNumber);

        // When
        var obscuredValue = commitmentNumber.ObscuredValue;

        // Then
        Assert.Equal(ExpectedObscuredValue, obscuredValue);
    }

    [Fact]
    public void Given_CommitmentNumber_When_CalculateLockTime_Then_ReturnsCorrectValue()
    {
        // Given
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        const uint expectedLocktime = (uint)((0x20 << 24) | (ExpectedObscuredValue & 0xFFFFFF));
        var commitmentNumber =
            new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object,
                                 InitialCommitmentNumber);

        // When
        var lockTime = commitmentNumber.CalculateLockTime();

        // Then - formula is (0x20 << 24) | (obscured & 0xFFFFFF)
        Assert.Equal(expectedLocktime, lockTime.ValueOrHeight);
    }

    [Fact]
    public void Given_CommitmentNumber_When_CalculateSequence_Then_ReturnsCorrectValue()
    {
        // Given
        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        const uint expectedSequence = (uint)((0x80U << 24) | ((ExpectedObscuredValue >> 24) & 0xFFFFFF));
        var commitmentNumber =
            new CommitmentNumber(_openerPaymentBasepoint, _accepterPaymentBasepoint, sha256Mock.Object,
                                 InitialCommitmentNumber);

        // When
        var sequence = commitmentNumber.CalculateSequence();

        // Then - formula is (0x80 << 24) | ((obscured >> 24) & 0xFFFFFF)
        Assert.Equal(expectedSequence, sequence.Value);
    }
}