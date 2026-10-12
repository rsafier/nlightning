namespace NLightning.Domain.Tests.Onchain;

using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;

/// <summary>
/// BOLT 5 §Penalty Transactions Weight Calculation / BOLT 3 Appendix A: the witness estimate per spend kind (73-byte
/// signatures) and the transaction weight. The Appendix C script runs are in
/// <c>Infrastructure.Bitcoin.Tests/Builders/PenaltyTransactionBuilderTests</c>.
/// </summary>
public class SweepWeightsTests
{
    private static readonly byte[] s_script133 = new byte[133];
    private static readonly byte[] s_script139 = new byte[139];

    [Fact]
    public void Given_Bolt5Constants_When_Summed_Then_InputWeightsMatchTheSpec()
    {
        // Assert
        Assert.Equal(324, SweepWeights.ToLocalPenaltyInputWeight);
        Assert.Equal(407, SweepWeights.OfferedHtlcPenaltyInputWeight);
        Assert.Equal(413, SweepWeights.AcceptedHtlcPenaltyInputWeight);
        Assert.Equal(214, SweepWeights.PenaltyTransactionOverheadWeight);
    }

    [Theory]
    [InlineData(SweepSpendKind.RevokedHtlc, 133, 243)] // offered_htlc_penalty_witness
    [InlineData(SweepSpendKind.RevokedHtlc, 139, 249)] // accepted_htlc_penalty_witness (3-byte cltv)
    [InlineData(SweepSpendKind.RevokedDelayedOutput, 77, 155)] // <sig> 1 <script>: 1 + 74 + 2 + 78
    [InlineData(SweepSpendKind.DelayedOutput, 77, 154)] // <sig> <> <script>
    [InlineData(SweepSpendKind.HtlcTimeoutClaim, 139, 216)] // <sig> <> <script>
    [InlineData(SweepSpendKind.HtlcPreimageClaim, 133, 242)] // <sig> <32> <script>
    [InlineData(SweepSpendKind.PaymentToRemote, 37, 113)] // anchors: <sig> <script>
    public void Given_SpendKind_When_Estimating_Then_WitnessSizeWithMaxSignature(SweepSpendKind kind, int scriptLength,
                                                                                 int expected)
    {
        // Arrange
        var input = new SweepInput(new byte[32], 0, 1_000, kind, new byte[scriptLength]);

        // Act / Assert
        Assert.Equal(expected, SweepWeights.EstimateWitnessSize(input));
    }

    [Fact]
    public void Given_P2WpkhToRemote_When_Estimating_Then_SigAndPubKey()
    {
        // Arrange
        var input = new SweepInput(new byte[32], 0, 1_000, SweepSpendKind.PaymentToRemote, null);

        // Act / Assert: 1 + (1 + 73) + (1 + 33); BOLT 5's 108 counts a 72-byte signature
        Assert.Equal(109, SweepWeights.EstimateWitnessSize(input));
    }

    [Fact]
    public void Given_ScriptOver252Bytes_When_Estimating_Then_ThreeByteLengthPrefix()
    {
        // Arrange
        var input = new SweepInput(new byte[32], 0, 1_000, SweepSpendKind.RevokedHtlc, new byte[300]);

        // Act / Assert
        Assert.Equal(1 + 74 + 34 + 3 + 300, SweepWeights.EstimateWitnessSize(input));
    }

    [Fact]
    public void Given_PenaltyWithTwoHtlcs_When_EstimatingTransaction_Then_Bolt5Formula()
    {
        // Arrange: one P2WSH output (34-byte script) as BOLT 5 assumes
        var secret = new Secret(new byte[32]);
        SweepInput[] inputs =
        [
            new(new byte[32], 0, 1_000, SweepSpendKind.RevokedHtlc, s_script133, PerCommitmentSecret: secret),
            new(new byte[32], 1, 1_000, SweepSpendKind.RevokedHtlc, s_script139, PerCommitmentSecret: secret)
        ];

        // Act
        var weight = SweepWeights.EstimateTransactionWeight(inputs, [34]);

        // Assert: 4 * 53 + 2 + 407 + 413
        Assert.Equal(4 * 53 + 2 + 407 + 413, weight);
    }

    [Theory]
    [InlineData(253u, 1_000L, 253UL)]
    [InlineData(253u, 999L, 252UL)]
    [InlineData(1_000u, 0L, 0UL)]
    public void Given_RateAndWeight_When_ComputingFee_Then_RoundedDown(uint rate, long weight, ulong expected)
    {
        // Act / Assert
        Assert.Equal(expected, SweepWeights.FeeSat(rate, weight));
    }

    [Fact]
    public void Given_Weight_When_ComputingVirtualSize_Then_RoundedUp()
    {
        // Act / Assert
        Assert.Equal(250, SweepWeights.VirtualSize(997));
        Assert.Equal(250, SweepWeights.VirtualSize(1_000));
    }

    [Theory]
    [InlineData(SweepSpendKind.DelayedOutput, SweepKeyKind.DelayedPayment, false)]
    [InlineData(SweepSpendKind.PaymentToRemote, SweepKeyKind.Payment, false)]
    [InlineData(SweepSpendKind.HtlcTimeoutClaim, SweepKeyKind.HtlcRemotePoint, false)]
    [InlineData(SweepSpendKind.HtlcPreimageClaim, SweepKeyKind.HtlcRemotePoint, false)]
    [InlineData(SweepSpendKind.RevokedDelayedOutput, SweepKeyKind.Revocation, true)]
    [InlineData(SweepSpendKind.RevokedHtlc, SweepKeyKind.Revocation, true)]
    public void Given_SpendKind_When_AskingKey_Then_OneOfTheFourKinds(SweepSpendKind spend, SweepKeyKind key,
                                                                    bool penalty)
    {
        // Act / Assert
        Assert.Equal(key, spend.GetKeyKind());
        Assert.Equal(penalty, spend.IsPenalty());
    }

    [Fact]
    public void Given_UnknownSpendKind_When_AskingKey_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SweepSpendKind)99).GetKeyKind());
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepWeights.EstimateWitnessSize(
                                                       new SweepInput(new byte[32], 0, 1, (SweepSpendKind)99, null)));
    }
}