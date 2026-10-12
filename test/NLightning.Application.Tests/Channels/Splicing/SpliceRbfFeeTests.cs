namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// NL-522: the totals SP-TX-05 compares when a peer RBFs a splice (BOLT 2 splice <c>tx_complete</c>: "This is an RBF
/// attempt and the transaction's total fees is less than the last successfully negotiated splice transaction's fees":
/// <c>tx_abort</c>). Both fees are every input's amount minus every output's (<c>SpliceService.GetTotalFee</c>), read
/// from the stored first attempt and from the constructed RBF attempt. The numbers are the wave d13 full CLN run's: CLN
/// spliced in 100,000 sat with a 300,000 sat wallet output and change, paying 3,178 sat (its wallet part at its own,
/// moved, estimate), then bumped at 1,000 sat/kw with its other 300,000 sat output, paying 1,202 sat.
/// </summary>
public class SpliceRbfFeeTests
{
    private const ulong Capacity = 1_000_000;
    private const ulong SpliceIn = 100_000;
    private const ulong FirstFee = 3_178;
    private const ulong BumpFee = 1_202;

    private static readonly BitcoinScript s_funding = new([0x00, 0x20, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly BitcoinScript s_clnChange = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x22, 20)]);
    private static readonly BitcoinScript s_clnWallet = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x33, 20)]);

    [Fact]
    public void Given_ClnsFirstSpliceAndItsBump_When_TheirFeesAreRead_Then_TheyAreInputsMinusOutputs()
    {
        // Arrange: shared input + CLN's wallet input, the new funding output + CLN's change, in both attempts
        var first = SpliceTx(0x01, clnInputTxByte: 0xA1, FirstFee);
        var bump = SpliceTx(0x02, clnInputTxByte: 0xA2, BumpFee);
        var stored = new InteractiveTxSessionModel
        {
            ChannelId = default,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.Splice,
            IsInitiator = false,
            FeeratePerKw = 253,
            Locktime = 500,
            Inputs = first.Inputs,
            Outputs = first.Outputs,
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = first,
            State = InteractiveTxSessionState.Signed,
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        // Act
        var previousFee = SpliceService.GetFee(stored);
        var bumpFee = SpliceService.GetTotalFee(bump);

        // Assert: neither the funding amount nor the contributions nor the shared input's value leak into the fee
        Assert.Equal(FirstFee, previousFee);
        Assert.Equal(BumpFee, bumpFee);
        Assert.Null(SpliceService.GetFee(stored with { ConstructedTx = null }));
    }

    [Fact]
    public void Given_ABumpPayingLessThanTheFirstAttempt_When_TxCompleteIsChecked_Then_SpTx05TxAbort()
    {
        // Arrange
        var facts = Facts(SpliceTx(0x02, clnInputTxByte: 0xA2, BumpFee)) with { PreviousAttemptFeeSatoshis = FirstFee };

        // Act
        var violation = SpliceRules.CheckTxComplete(facts);

        // Assert: the rule the wave d13 run hit, for the reason the numbers give
        Assert.NotNull(violation);
        Assert.Equal("SP-TX-05", violation.RequirementId);
        Assert.Equal(SpliceRuleAction.TxAbort, violation.Action);
        Assert.Contains($"pays {BumpFee} sat, less than the previous {FirstFee} sat", violation.Reason);
    }

    [Theory]
    [InlineData(FirstFee)]
    [InlineData(FirstFee + 1)]
    [InlineData(4_400ul)]
    public void Given_ABumpPayingAtLeastTheFirstAttempt_When_TxCompleteIsChecked_Then_Accepted(ulong fee)
    {
        // Arrange
        var facts = Facts(SpliceTx(0x02, clnInputTxByte: 0xA2, fee)) with { PreviousAttemptFeeSatoshis = FirstFee };

        // Act
        var violation = SpliceRules.CheckTxComplete(facts);

        // Assert
        Assert.Null(violation);
    }

    /// <summary>
    /// A splice-in of CLN's: the shared input (the current funding output, <see cref="Capacity"/>), a 300,000 sat
    /// wallet output of CLN's, the new funding output (capacity + <see cref="SpliceIn"/>) and CLN's change, paying
    /// <paramref name="fee"/>.
    /// </summary>
    private static ConstructedInteractiveTx SpliceTx(byte txByte, byte clnInputTxByte, ulong fee)
    {
        const ulong clnInput = 300_000;
        var change = clnInput - SpliceIn - fee;
        return new ConstructedInteractiveTx(
            new TxId(Enumerable.Repeat(txByte, 32).ToArray()), [], 500,
            [
                new InteractiveTxInput(0, InteractiveTxParty.Remote, new TxId(Enumerable.Repeat((byte)0xF0, 32).ToArray()),
                                       0, 0xFFFFFFFD, LightningMoney.Satoshis(Capacity), s_funding, null, true),
                new InteractiveTxInput(2, InteractiveTxParty.Remote,
                                       new TxId(Enumerable.Repeat(clnInputTxByte, 32).ToArray()), 1, 0xFFFFFFFD,
                                       LightningMoney.Satoshis(clnInput), s_clnWallet, [0x02], false)
            ],
            [
                new InteractiveTxOutput(0, InteractiveTxParty.Remote, LightningMoney.Satoshis(Capacity + SpliceIn),
                                        s_funding, true),
                new InteractiveTxOutput(2, InteractiveTxParty.Remote, LightningMoney.Satoshis(change), s_clnChange,
                                        false)
            ], 1_200, 0);
    }

    /// <summary>SP-TX-05's facts for <paramref name="transaction"/>: CLN's +100,000 sat, our 0, no reserve issue.</summary>
    private static SpliceTxCompleteFacts Facts(ConstructedInteractiveTx transaction) =>
        new(1, 1, Capacity + SpliceIn, Capacity, 0, (long)SpliceIn, 600_000_000, 400_000_000, 10_000, 10_000, false,
            true, SpliceService.GetTotalFee(transaction));
}