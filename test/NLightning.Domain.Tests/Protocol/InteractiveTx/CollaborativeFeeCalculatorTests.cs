namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using static InteractiveTxTestData;

/// <summary>
/// <see cref="CollaborativeFeeCalculator"/> (IT1-T2, IT-S-03) and the IT-R-04 fee checks it feeds, against BOLT 3
/// Appendix F "Funding Transaction Construction" (the only collaborative-fee vector of the spec) and boundary tables at
/// the agreed feerate.
/// </summary>
public class CollaborativeFeeCalculatorTests
{
    #region BOLT 3 Appendix F

    private const uint AppendixFeerate = 253;

    // Opener's input 0 (P2WSH, the hash-lock witness) and accepter's input 2 (P2WPKH), both 250,000,000 sat outputs of
    // the Appendix's parent transaction.
    private static readonly BitcoinScript s_openerInputScript =
        new(Convert.FromHexString("0020fd89acf65485df89797d9ba7ba7a33624ac4452f00db08107f34257d33e5b946"));

    private static readonly BitcoinScript s_accepterInputScript =
        new(Convert.FromHexString("0014fbb4db9d85fba5e301f4399e3038928e44e37d32"));

    private static readonly BitcoinScript s_openerChangeScript =
        new(Convert.FromHexString("00141ca1cca8855bad6bc1ea5436edd8cff10b7e448b"));

    private static readonly BitcoinScript s_accepterChangeScript =
        new(Convert.FromHexString("001444cb0c39f93ecc372b5851725bd29d865d333b10"));

    private static readonly BitcoinScript s_appendixFundingScript =
        new(Convert.FromHexString("0020297b92c238163e820b82486084634b4846b86a3c658d87b9384192e6bea98ec5"));

    private static readonly TxId s_parentTxId =
        new(Convert.FromHexString("b932b0669cd0394d0d5bcc27e01ab8c511f1662a6799925b346c0cf18fca0343"));

    /// <summary>
    /// The Appendix F negotiation as seen by one side: opener serial ids 20 (input), 30 (change), 44 (funding output);
    /// accepter 11 (input), 33 (change); feerate 253, each side puts 200,000,000 sat into the 400,000,000 funding output.
    /// </summary>
    private static (List<InteractiveTxInput> Inputs, List<InteractiveTxOutput> Outputs, SharedFundingSpec Spec)
        AppendixF(bool localIsOpener, long openerChange = 49_999_845, long accepterChange = 49_999_900)
    {
        var opener = localIsOpener ? InteractiveTxParty.Local : InteractiveTxParty.Remote;
        var accepter = localIsOpener ? InteractiveTxParty.Remote : InteractiveTxParty.Local;
        var inputs = new List<InteractiveTxInput>
        {
            new(11, accepter, s_parentTxId, 2, 0xFFFFFFFD, LightningMoney.Satoshis(250_000_000), s_accepterInputScript,
                [0x02], false),
            new(20, opener, s_parentTxId, 0, 0xFFFFFFFD, LightningMoney.Satoshis(250_000_000), s_openerInputScript,
                [0x02], false)
        };
        var outputs = new List<InteractiveTxOutput>
        {
            new(30, opener, LightningMoney.Satoshis(openerChange), s_openerChangeScript, false),
            new(33, accepter, LightningMoney.Satoshis(accepterChange), s_accepterChangeScript, false),
            new(44, opener, LightningMoney.Satoshis(400_000_000), s_appendixFundingScript, true)
        };
        var spec = new SharedFundingSpec(null, s_appendixFundingScript, LightningMoney.Satoshis(400_000_000),
                                         LightningMoney.Zero, LightningMoney.Zero,
                                         LightningMoney.Satoshis(200_000_000), LightningMoney.Satoshis(200_000_000));
        return (inputs, outputs, spec);
    }

    [Fact]
    public void Given_AppendixF_When_ComputingTheOpenersWeightAndFee_Then_609WuAnd155Sat()
    {
        // Arrange
        // (BOLT 3 Appendix F: "initiator_weight = (1 + 1 + 4 + 4) * 4 + 2 + (32 + 4 + 1 + 4) * 1 * 4 + 43 * 4 + 31 * 4
        //  + max(1 * 107, 71) = 609", "initiator_fees = 609 * 253 / 1000 = 155 sats")
        var (inputs, outputs, spec) = AppendixF(true);

        // Act
        var weight = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Local, true,
                                                                      spec);
        var fee = CollaborativeFeeCalculator.FeeForWeight(weight, AppendixFeerate);
        var paid = CollaborativeFeeCalculator.GetPaidFeeMsat(inputs, outputs, InteractiveTxParty.Local, spec);

        // Assert
        Assert.Equal(609, weight);
        Assert.Equal(155, fee.Satoshi);
        Assert.Equal(155_000, paid); // change = 250,000,000 - 200,000,000 - 155 = 49,999,845
    }

    [Fact]
    public void Given_AppendixF_When_ComputingTheAcceptersWeightAndFee_Then_395WuAnd100Sat()
    {
        // Arrange
        // (BOLT 3 Appendix F: "contributor_weight = (32 + 4 + 1 + 4) * 1 * 4 + 31 * 4 + max(1 * 107, 99) = 395",
        //  "contributor_fees = 100 sats", change 49,999,900)
        var (inputs, outputs, spec) = AppendixF(false);

        // Act
        var weight = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Local, false,
                                                                      spec);
        var fee = CollaborativeFeeCalculator.FeeForWeight(weight, AppendixFeerate);
        var paid = CollaborativeFeeCalculator.GetPaidFeeMsat(inputs, outputs, InteractiveTxParty.Local, spec);

        // Assert
        Assert.Equal(395, weight);
        Assert.Equal(100, fee.Satoshi);
        Assert.Equal(100_000, paid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AppendixF_When_EitherSideChecksTxComplete_Then_Accepted(bool localIsOpener)
    {
        // Arrange
        var (inputs, outputs, spec) = AppendixF(localIsOpener);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, AppendixFeerate, localIsOpener, []);

        // Assert
        Assert.Null(violation);
    }

    [Theory]
    [InlineData(49_999_845, null)] // pays 155 sat: the Appendix's own fee
    [InlineData(49_999_846, null)] // pays 154 sat = floor(609 x 253 / 1000): exactly the agreed feerate
    [InlineData(49_999_847, "IT-R-04")] // pays 153 sat: below it
    public void Given_OpenerChange_When_TheAccepterChecksTxComplete_Then_BoundaryAtTheAgreedFeerate(long openerChange,
        string? expected)
    {
        // Arrange
        // (BOLT 2 tx_complete: "the peer's paid feerate does not meet or exceed the agreed feerate (based on the
        // minimum fee)")
        var (inputs, outputs, spec) = AppendixF(false, openerChange);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, AppendixFeerate, false, []);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Theory]
    [InlineData(49_999_900, null)] // pays 100 sat
    [InlineData(49_999_901, null)] // pays 99 sat = floor(395 x 253 / 1000)
    [InlineData(49_999_902, "IT-R-04")] // pays 98 sat
    public void Given_AccepterChange_When_TheOpenerChecksTxComplete_Then_BoundaryAtTheAgreedFeerate(
        long accepterChange, string? expected)
    {
        // Arrange
        var (inputs, outputs, spec) = AppendixF(true, accepterChange: accepterChange);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, AppendixFeerate, true, []);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Fact]
    public void Given_OpenerPaysItsOwnItemsButNotTheCommonFields_When_TheAccepterChecks_Then_CommonFieldsViolation()
    {
        // Arrange: without the common fields the opener owes 567 wu = 143 sat, with them 609 wu = 154 sat; it pays 150
        // (BOLT 2 tx_complete: "if is the non-initiator: the initiator's fees do not cover the common fields")
        var (inputs, outputs, spec) = AppendixF(false, 50_000_000 - 150);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, AppendixFeerate, false, []);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal("IT-R-04", violation.RequirementId);
        Assert.Contains("common fields", violation.Reason);
    }

    [Fact]
    public void Given_AccepterPaysTooLittle_When_TheOpenerChecks_Then_NoCommonFieldsClaim()
    {
        // Arrange: the accepter (non-initiator) never owes the common fields
        var (inputs, outputs, spec) = AppendixF(true, accepterChange: 50_000_000 - 50);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, AppendixFeerate, true, []);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal("IT-R-04", violation.RequirementId);
        Assert.DoesNotContain("common fields", violation.Reason);
    }

    #endregion

    #region Weights

    [Fact]
    public void Given_Constants_When_Read_Then_TheyAreTheBolt3Values()
    {
        // Assert
        Assert.Equal(42, CollaborativeFeeCalculator.CommonFieldsWeight);
        Assert.Equal(164, CollaborativeFeeCalculator.InputBaseWeight);
        Assert.Equal(107, CollaborativeFeeCalculator.MinimumWitnessWeight);
    }

    [Theory]
    [InlineData("0014" + "3333333333333333333333333333333333333333", 124)] // P2WPKH: (8 + 1 + 22) x 4 (Appendix F "31 * 4")
    [InlineData("0020" + "4444444444444444444444444444444444444444444444444444444444444444", 172)] // P2WSH ("43 * 4")
    [InlineData("5120" + "5555555555555555555555555555555555555555555555555555555555555555", 172)] // P2TR
    [InlineData("76a914" + "1111111111111111111111111111111111111111" + "88ac", 136)] // P2PKH
    public void Given_OutputScript_When_ComputingItsWeight_Then_ValuePrefixAndScriptTimesFour(string scriptHex,
                                                                                              long expected)
    {
        // Act
        var weight = CollaborativeFeeCalculator.OutputWeight(new BitcoinScript(Convert.FromHexString(scriptHex)));

        // Assert
        Assert.Equal(expected, weight);
    }

    [Fact]
    public void Given_OutputScriptOf253Bytes_When_ComputingItsWeight_Then_TheLengthPrefixTakesThreeBytes()
    {
        // Arrange
        var script = new BitcoinScript([0x6a, .. new byte[252]]);

        // Act
        var weight = CollaborativeFeeCalculator.OutputWeight(script);

        // Assert
        Assert.Equal((8 + 3 + 253) * 4, weight);
    }

    [Fact]
    public void Given_SpentScript_When_EstimatingWitnessWeights_Then_P2WpkhP2TrP2Wsh()
    {
        // Act & Assert
        // minimum (receiver side): BOLT 3's 107, except the 66 of a P2TR key-path SIGHASH_DEFAULT signature
        Assert.Equal(107, CollaborativeFeeCalculator.GetMinimumWitnessWeight(P2Wpkh));
        Assert.Equal(66, CollaborativeFeeCalculator.GetMinimumWitnessWeight(P2Tr));
        Assert.Equal(107, CollaborativeFeeCalculator.GetMinimumWitnessWeight(P2Wsh));

        // maximum (what we budget): 1 + 1 + 73 + 1 + 33 for P2WPKH, 1 + 1 + 65 for P2TR, unknown for P2WSH
        Assert.Equal(109, CollaborativeFeeCalculator.GetMaximumWitnessWeight(P2Wpkh));
        Assert.Equal(67, CollaborativeFeeCalculator.GetMaximumWitnessWeight(P2Tr));
        Assert.Null(CollaborativeFeeCalculator.GetMaximumWitnessWeight(P2Wsh));
    }

    [Fact]
    public void Given_SpentScript_When_EstimatingOurInputWeight_Then_BasePlusMaximumWitnessNeverBelowBolt3Minimum()
    {
        // Act & Assert
        Assert.Equal(164 + 109, CollaborativeFeeCalculator.EstimateLocalInputWeight(P2Wpkh));
        Assert.Equal(164 + 107, CollaborativeFeeCalculator.EstimateLocalInputWeight(P2Tr)); // BOLT 3's minimum, not 67
        Assert.Throws<ArgumentException>(() => CollaborativeFeeCalculator.EstimateLocalInputWeight(P2Wsh));
    }

    [Theory]
    [InlineData(true, 42 + 384 + 172 + 271 + 124)] // initiator: common fields, shared input and output, its own items
    [InlineData(false, 271 + 124)] // non-initiator: its own input and output only
    public void Given_Splice_When_ComputingContributionWeights_Then_SharedItemsAreTheInitiators(bool localIsInitiator,
        long expectedLocal)
    {
        // Arrange: shared input (384 wu from the spec) and funding output, one P2WPKH input and output per side
        var spec = Splice(localIsInitiator);
        var initiator = localIsInitiator ? InteractiveTxParty.Local : InteractiveTxParty.Remote;
        var other = localIsInitiator ? InteractiveTxParty.Remote : InteractiveTxParty.Local;
        var inputs = new List<InteractiveTxInput>
        {
            new(0, initiator, FundingTxId, 1, Sequence, LightningMoney.Satoshis(1_000_000), FundingScript, null, true),
            new(1, other, PrevTxId(PrevTx(1)), 0, Sequence, LightningMoney.Satoshis(10_000), P2Wpkh, PrevTx(1), false),
            new(2, initiator, PrevTxId(PrevTx(2)), 0, Sequence, LightningMoney.Satoshis(10_000), P2Wpkh, PrevTx(2),
                false)
        };
        var outputs = new List<InteractiveTxOutput>
        {
            new(3, other, LightningMoney.Satoshis(5_000), P2Wpkh, false),
            new(4, initiator, spec.SharedOutputAmount, FundingScript, true),
            new(6, initiator, LightningMoney.Satoshis(5_000), P2Wpkh, false)
        };

        // Act
        var local = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Local,
                                                                     localIsInitiator, spec);
        var remote = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Remote,
                                                                      !localIsInitiator, spec);
        var total = CollaborativeFeeCalculator.EstimateTransactionWeight(inputs, outputs, spec);

        // Assert
        Assert.Equal(expectedLocal, local);
        Assert.Equal(total, local + remote);
    }

    [Fact]
    public void Given_ContributionWithoutCommonFields_When_ComputingTheInitiatorsWeight_Then_42Less()
    {
        // Arrange
        var (inputs, outputs, spec) = AppendixF(true);

        // Act
        var withCommon = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Local,
                                                                          true, spec);
        var withoutCommon = CollaborativeFeeCalculator.GetContributionWeight(inputs, outputs, InteractiveTxParty.Local,
                                                                             true, spec, false);

        // Assert
        Assert.Equal(42, withCommon - withoutCommon);
    }

    [Fact]
    public void Given_P2TrInputOfThePeer_When_Checking_Then_ChargedTheKeyPathMinimum()
    {
        // Arrange
        var input = new InteractiveTxInput(1, InteractiveTxParty.Remote, PrevTxId(PrevTx(1)), 0, Sequence,
                                           LightningMoney.Satoshis(10_000), P2Tr, PrevTx(1), false);

        // Act
        var weight = CollaborativeFeeCalculator.GetMinimumInputWeight(input, null);

        // Assert
        Assert.Equal(164 + 66, weight);
    }

    [Theory]
    [InlineData(true, 42 + 384 + 172 + 272)]
    [InlineData(false, 272)]
    public void Given_OurContribution_When_ComputingItsWeight_Then_InputWeightsAndSharedItemsAsInitiator(
        bool isInitiator, long expected)
    {
        // Arrange: one input of 272 wu (ContributedInput.InputWeight)
        var contribution = Contribution([Input(1)]);

        // Act
        var weight = CollaborativeFeeCalculator.GetLocalContributionWeight(contribution, isInitiator,
                                                                           Splice(isInitiator));

        // Assert
        Assert.Equal(expected, weight);
    }

    #endregion

    #region Fees

    [Theory]
    [InlineData(609L, 253U, 155L, 154L)]
    [InlineData(395L, 253U, 100L, 99L)]
    [InlineData(1_000L, 253U, 253L, 253L)] // exact
    [InlineData(0L, 253U, 0L, 0L)]
    [InlineData(1L, 1U, 1L, 0L)]
    [InlineData(400_000L, 50_000U, 20_000_000L, 20_000_000L)]
    public void Given_WeightAndFeerate_When_ComputingFees_Then_PaidRoundsUpRequiredRoundsDown(long weight,
        uint feerate, long expectedFee, long expectedMinimum)
    {
        // Act
        var fee = CollaborativeFeeCalculator.FeeForWeight(weight, feerate);
        var minimum = CollaborativeFeeCalculator.MinimumFeeForWeight(weight, feerate);

        // Assert
        Assert.Equal(expectedFee, fee.Satoshi);
        Assert.Equal(expectedMinimum, minimum.Satoshi);
    }

    [Fact]
    public void Given_NegativeWeight_When_ComputingFees_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CollaborativeFeeCalculator.FeeForWeight(-1, 253));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollaborativeFeeCalculator.MinimumFeeForWeight(-1, 253));
    }

    [Fact]
    public void Given_OurContribution_When_ComputingItsFee_Then_RoundedUpAtTheFeerate()
    {
        // Arrange: non-initiator, one 272 wu input and a P2WPKH change (124 wu) = 396 wu
        var contribution = Contribution([Input(1)], [Output(50_000)]);

        // Act
        var fee = CollaborativeFeeCalculator.GetLocalContributionFee(contribution, false, null, 253);

        // Assert
        Assert.Equal(101, fee.Satoshi); // 396 x 253 / 1000 = 100.188
    }

    [Fact]
    public void Given_OutputsAboveInputs_When_ComputingThePaidFee_Then_Negative()
    {
        // Arrange
        var inputs = new List<InteractiveTxInput>
        {
            new(1, InteractiveTxParty.Remote, PrevTxId(PrevTx(1)), 0, Sequence, LightningMoney.Satoshis(1_000), P2Wpkh,
                PrevTx(1), false)
        };
        var outputs = new List<InteractiveTxOutput>
        {
            new(3, InteractiveTxParty.Remote, LightningMoney.Satoshis(1_500), P2Wpkh, false)
        };

        // Act
        var paid = CollaborativeFeeCalculator.GetPaidFeeMsat(inputs, outputs, InteractiveTxParty.Remote, null);

        // Assert
        Assert.Equal(-500_000, paid);
    }

    [Theory]
    [InlineData(true, 1_000)] // local: 600,000 in - 599,000 out
    [InlineData(false, 2_000)] // remote: 400,000 in - 398,000 out
    public void Given_SpliceShares_When_ComputingThePaidFee_Then_SharesCount(bool local, long expectedSats)
    {
        // Arrange
        var spec = Splice(true, 997_000, 599_000, 398_000);
        var party = local ? InteractiveTxParty.Local : InteractiveTxParty.Remote;

        // Act
        var paid = CollaborativeFeeCalculator.GetPaidFeeMsat([], [], party, spec);

        // Assert
        Assert.Equal(expectedSats * 1_000, paid);
    }

    #endregion

    #region IT-R-04 weight cap

    [Theory]
    [InlineData(400_000, null)]
    [InlineData(400_001, "IT-R-04")]
    public void Given_EstimatedWeight_When_CheckingTxComplete_Then_Above400000Fails(long targetWeight,
                                                                                   string? expected)
    {
        // Arrange: a splice whose shared input weight tops the transaction up to the target (we are the non-initiator,
        // the peer's shares leave it far more than its fee)
        var baseWeight = CollaborativeFeeCalculator.CommonFieldsWeight
                         + CollaborativeFeeCalculator.OutputWeight(FundingScript);
        var sharedInput = new SharedFundingInput(FundingTxId, 1, LightningMoney.Satoshis(1_000_000), FundingScript,
                                                 (int)(targetWeight - baseWeight));
        var spec = new SharedFundingSpec(sharedInput, FundingScript, LightningMoney.Satoshis(500_000),
                                         LightningMoney.Satoshis(400_000), LightningMoney.Satoshis(600_000),
                                         LightningMoney.Satoshis(400_000), LightningMoney.Satoshis(100_000));
        var inputs = new List<InteractiveTxInput>
        {
            new(0, InteractiveTxParty.Remote, FundingTxId, 1, Sequence, sharedInput.Amount, FundingScript, null, true)
        };
        var outputs = new List<InteractiveTxOutput>
        {
            new(2, InteractiveTxParty.Remote, spec.SharedOutputAmount, FundingScript, true)
        };

        // Act
        var weight = CollaborativeFeeCalculator.EstimateTransactionWeight(inputs, outputs, spec);
        var violation = InteractiveTxRules.CheckTxComplete(inputs, outputs, spec, 253, false, []);

        // Assert
        Assert.Equal(targetWeight, weight);
        Assert.Equal(expected, violation?.RequirementId);
    }

    #endregion
}