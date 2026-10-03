namespace NLightning.Application.Tests.Channels.Close.Simple;

using Application.Channels.Close.Simple;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;

/// <summary>
/// BOLT 2 <c>option_simple_close</c> requirement table (BOLT2 plan §6.12): the closer's fee and <c>closing_tlvs</c>
/// (B2-SC-C01, C02, C06, C07), the BOLT 3 outputs (B2-SC-E04, OP_RETURN at zero) and the closee's signature choice
/// (B2-SC-E06).
/// </summary>
public class SimpleCloseRulesTests
{
    private static readonly BitcoinScript s_p2Wpkh = new([0x00, 0x14, .. Enumerable.Repeat((byte)0xA1, 20)]);
    private static readonly BitcoinScript s_p2Wsh = new([0x00, 0x20, .. Enumerable.Repeat((byte)0xB0, 32)]);
    private static readonly BitcoinScript s_opReturn = new([0x6a, 0x06, 1, 2, 3, 4, 5, 6]);

    private static readonly FundingOutputInfo s_funding =
        new(LightningMoney.Satoshis(1_000_000), new CompactPubKey([0x02, .. new byte[32]]),
            new CompactPubKey([0x03, .. new byte[32]]), new TxId(new byte[32]), 0);

    private static readonly CompactSignature s_signature = new(new byte[64]);

    private static SimpleClosingTerms Terms(ulong closerSat, ulong closeeSat, ulong feeSat,
                                            BitcoinScript? closer = null, BitcoinScript? closee = null) =>
        new(s_funding, closerSat * 1000, closeeSat * 1000, closer ?? s_p2Wpkh, closee ?? s_p2Wsh, feeSat, true);

    #region Closer selection

    public static TheoryData<ulong, ulong, ulong, ClosingSigKind[], string?> CloserCases => new()
    {
        // Not lesser, both outputs above dust: both closer_output_only and closer_and_closee_outputs (C07)
        { 600_000, 400_000, 1_000, [ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs], null },
        // Equal balances are "not lesser"
        { 500_000, 500_000, 1_000, [ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs], null },
        // Not lesser, closee (P2WSH, 330) dust: only closer_output_only (C07)
        { 600_000, 329, 1_000, [ClosingSigKind.CloserOutputOnly], null },
        // Not lesser, closee exactly at its dust threshold: not dust
        { 600_000, 330, 1_000, [ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs], null },
        // Lesser, own output above dust: closer_and_closee_outputs only, never closer_output_only (C06)
        { 400_000, 600_000, 1_000, [ClosingSigKind.CloserAndCloseeOutputs], null },
        // Lesser, own output (P2WPKH, 294) dust after the fee: closee_output_only only (C06)
        { 1_293, 600_000, 1_000, [ClosingSigKind.CloseeOutputOnly], null },
        // Lesser, own output exactly at dust after the fee: kept
        { 1_294, 600_000, 1_000, [ClosingSigKind.CloserAndCloseeOutputs], null },
        // Fee above the closer's balance (C01)
        { 999, 600_000, 1_000, [], "B2-SC-C01" },
        // Both outputs dust (C02)
        { 1_100, 300, 1_000, [], "B2-SC-C02" },
        // Not lesser and own output dust: closee_output_only is not allowed to it (C07)
        { 1_100, 1_000, 1_000, [], "B2-SC-C07" }
    };

    [Theory]
    [MemberData(nameof(CloserCases))]
    public void Given_Balances_When_SelectCloserKinds_Then_BoltFieldsOrRefusal(ulong closerSat, ulong closeeSat,
                                                                             ulong feeSat, ClosingSigKind[] expected,
                                                                             string? refusal)
    {
        // Arrange
        var terms = Terms(closerSat, closeeSat, feeSat);

        // Act
        var selection = SimpleCloseRules.SelectCloserKinds(terms);

        // Assert
        Assert.Equal(expected, selection.Kinds);
        Assert.Equal(refusal, selection.RequirementId);
        Assert.Equal(expected.Length > 0, selection.CanPropose);
    }

    [Fact]
    public void Given_LesserCloserAndDustCloseeWithDifferentThresholds_When_Select_Then_Refused()
    {
        // Arrange: the lesser closer pays P2WPKH (294), the closee's P2PKH-like script is at 546; closee 400 sat is dust
        BitcoinScript p2Pkh = new([0x76, 0xa9, 0x14, .. new byte[20], 0x88, 0xac]);
        var terms = new SimpleClosingTerms(s_funding, 399_000, 400_000, s_p2Wpkh, p2Pkh, 1, true);

        // Act
        var selection = SimpleCloseRules.SelectCloserKinds(terms);

        // Assert: C06 forbids closer_output_only to the lesser side, and closer_and_closee_outputs would carry dust
        Assert.False(selection.CanPropose);
        Assert.Equal("B2-SC-C06", selection.RequirementId);
    }

    [Fact]
    public void Given_OpReturnCloser_When_Select_Then_OutputIsZeroAndNeverDust()
    {
        // Arrange: BOLT 3: an OP_RETURN output carries 0 (all its funds go to fees) and is never dust
        var terms = Terms(600_000, 400_000, 1_000, closer: s_opReturn);

        // Act
        var selection = SimpleCloseRules.SelectCloserKinds(terms);
        var both = terms.Build(ClosingSigKind.CloserAndCloseeOutputs);

        // Assert
        Assert.Equal(0UL, terms.CloserAmountSat);
        Assert.False(terms.CloserIsDust);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs], selection.Kinds);
        Assert.Equal(0L, both.LocalOutput!.Amount.Satoshi);
        Assert.Equal(400_000L, both.RemoteOutput!.Amount.Satoshi);
    }

    #endregion

    #region Outputs

    [Fact]
    public void Given_Terms_When_Build_Then_CloserPaysTheFeeAndAmountsAreRoundedDown()
    {
        // Arrange: B3-CLTX-01: balances in msat are rounded down; the fee comes off the closer's output only
        var terms = new SimpleClosingTerms(s_funding, 600_000_999, 399_999_999, s_p2Wpkh, s_p2Wsh, 1_234, false);

        // Act
        var closerOnly = terms.Build(ClosingSigKind.CloserOutputOnly);
        var closeeOnly = terms.Build(ClosingSigKind.CloseeOutputOnly);
        var both = terms.Build(ClosingSigKind.CloserAndCloseeOutputs);

        // Assert (the peer is the closer here: its output is not ours)
        Assert.Equal(600_000L - 1_234, Assert.Single(closerOnly.Outputs).Amount.Satoshi);
        Assert.False(closerOnly.Outputs[0].IsLocal);
        Assert.Equal(399_999L, Assert.Single(closeeOnly.Outputs).Amount.Satoshi);
        Assert.True(closeeOnly.Outputs[0].IsLocal);
        Assert.Equal(2, both.Outputs.Count);
        Assert.Equal(1_234L, both.Fee.Satoshi);
    }

    [Fact]
    public void Given_FeeAboveBalance_When_CloserAmount_Then_Throws()
    {
        // Arrange
        var terms = Terms(1_000, 600_000, 1_001);

        // Act / Assert: B2-SC-C01 / E01
        Assert.False(terms.CloserCanPay);
        Assert.Throws<InvalidOperationException>(() => terms.CloserAmountSat);
    }

    #endregion

    #region Closee selection

    public static TheoryData<ulong, bool, bool, bool, ClosingSigKind> CloseeCases => new()
    {
        // Closee output above dust: closer_and_closee_outputs when present
        { 400_000, true, false, true, ClosingSigKind.CloserAndCloseeOutputs },
        { 400_000, true, true, true, ClosingSigKind.CloserAndCloseeOutputs },
        // ... else closee_output_only (the closer dropped its own output)
        { 400_000, false, true, false, ClosingSigKind.CloseeOutputOnly },
        // ... also when it is absent: the caller then fails the message (E07)
        { 400_000, true, false, false, ClosingSigKind.CloseeOutputOnly },
        // Closee output dust: closer_output_only, whatever else is present
        { 329, true, false, true, ClosingSigKind.CloserOutputOnly },
        { 329, false, false, true, ClosingSigKind.CloserOutputOnly }
    };

    [Theory]
    [MemberData(nameof(CloseeCases))]
    public void Given_ReceivedFields_When_SelectCloseeKind_Then_BoltRuleApplies(ulong closeeSat, bool hasCloserOnly,
                                                                              bool hasCloseeOnly, bool hasBoth,
                                                                              ClosingSigKind expected)
    {
        // Arrange
        var terms = Terms(600_000, closeeSat, 1_000);
        var received = new ClosingSignatures(hasCloserOnly ? s_signature : null, hasCloseeOnly ? s_signature : null,
                                             hasBoth ? s_signature : null);

        // Act
        var kind = SimpleCloseRules.SelectCloseeKind(terms, received);

        // Assert: B2-SC-E06
        Assert.Equal(expected, kind);
    }

    #endregion

    #region Our fee

    [Fact]
    public void Given_Feerate_When_ChooseFee_Then_WeightOfBothOutputsTimesFeerate()
    {
        // Act
        var fee = SimpleCloseRules.ChooseFee(600_000_000, 400_000_000, s_p2Wpkh, s_p2Wsh, 2_500);

        // Assert
        Assert.Equal(ClosingFeeCalculator.FeeSat(2_500, ClosingFeeCalculator.EstimateWeight(22, 34)), fee);
    }

    [Fact]
    public void Given_FeerateBelowFloor_When_ChooseFee_Then_Floor()
    {
        // Act
        var fee = SimpleCloseRules.ChooseFee(600_000_000, 400_000_000, s_p2Wpkh, s_p2Wsh, 1);

        // Assert
        Assert.Equal(ClosingFeeCalculator.FeeSat(253, ClosingFeeCalculator.EstimateWeight(22, 34)), fee);
    }

    [Fact]
    public void Given_NotLesserAndHugeFeerate_When_ChooseFee_Then_OwnOutputStaysAtDust()
    {
        // Act
        var fee = SimpleCloseRules.ChooseFee(600_000_000, 400_000_000, s_p2Wpkh, s_p2Wsh, 10_000_000);

        // Assert
        Assert.Equal(600_000UL - ShutdownScriptValidator.P2WpkhDustSat, fee);
    }

    [Fact]
    public void Given_LesserAndHugeFeerate_When_ChooseFee_Then_WholeBalance()
    {
        // Act
        var fee = SimpleCloseRules.ChooseFee(400_000_000, 600_000_000, s_p2Wpkh, s_p2Wsh, 10_000_000);

        // Assert: the lesser side may drop its own output (closee_output_only)
        Assert.Equal(400_000UL, fee);
    }

    [Fact]
    public void Given_BalanceBelowTheFloorFee_When_ChooseFee_Then_NoProposal()
    {
        // Act
        var fee = SimpleCloseRules.ChooseFee(100_000, 999_000_000, s_p2Wpkh, s_p2Wsh, 2_500);

        // Assert
        Assert.Null(fee);
    }

    #endregion
}