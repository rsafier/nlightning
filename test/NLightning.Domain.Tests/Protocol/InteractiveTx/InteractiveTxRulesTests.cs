namespace NLightning.Domain.Tests.Protocol.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using static InteractiveTxTestData;

/// <summary>
/// Rule tables for <see cref="InteractiveTxRules"/> (splicing plan §6.2: IT-S-01, IT-R-01..04, IT-SIG-02). Migrated
/// from the deleted <c>TxSerialIdParityTests</c>/<c>TxAddInputValidatorTests</c> (Infrastructure.Tests).
/// </summary>
public class InteractiveTxRulesTests
{
    #region IT-S-01 serial ids

    [Theory]
    [InlineData(true, 0UL, true)]
    [InlineData(true, 2UL, true)]
    [InlineData(true, 1UL, false)]
    [InlineData(false, 1UL, true)]
    [InlineData(false, 3UL, true)]
    [InlineData(false, 0UL, false)]
    [InlineData(false, 2UL, false)]
    public void Given_SerialIdAndSenderRole_When_CheckingParity_Then_InitiatorEvenNonInitiatorOdd(
        bool isSenderInitiator, ulong serialId, bool expected)
    {
        // Arrange
        // (BOLT 2: "if is the initiator: MUST send even serial_ids; if is the non-initiator: MUST send odd")

        // Act
        var hasParity = InteractiveTxRules.HasSenderParity(serialId, isSenderInitiator);
        var violation = InteractiveTxRules.CheckAddedSerialId(serialId, isSenderInitiator, false);

        // Assert
        Assert.Equal(expected, hasParity);
        Assert.Equal(expected, violation is null);
        if (!expected)
            Assert.Equal("IT-S-01", violation!.RequirementId);
    }

    [Fact]
    public void Given_SerialIdAlreadyIncluded_When_CheckingAddedSerialId_Then_Violation()
    {
        // Arrange
        // (BOLT 2: "the serial_id is already included in the transaction")

        // Act
        var violation = InteractiveTxRules.CheckAddedSerialId(1, false, true);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal("IT-S-01", violation.RequirementId);
        Assert.Contains("already included", violation.Reason);
    }

    #endregion

    #region IT-R-01/02 received message caps (NL-219)

    [Theory]
    [InlineData(0, true)]
    [InlineData(4094, true)]
    [InlineData(4095, false)]
    [InlineData(5000, false)]
    public void Given_ReceivedAddInputCount_When_Checking_Then_The4096thFails(int receivedBefore, bool ok)
    {
        // Arrange
        // (BOLT 2: "if has received 4096 tx_add_input messages during this negotiation")

        // Act
        var violation = InteractiveTxRules.CheckReceivedAddInputCount(receivedBefore);

        // Assert
        Assert.Equal(ok, violation is null);
        if (!ok)
            Assert.Equal("IT-R-01", violation!.RequirementId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4094, true)]
    [InlineData(4095, false)]
    public void Given_ReceivedAddOutputCount_When_Checking_Then_The4096thFails(int receivedBefore, bool ok)
    {
        // Arrange
        // (BOLT 2: "it has received 4096 tx_add_output messages during this negotiation")

        // Act
        var violation = InteractiveTxRules.CheckReceivedAddOutputCount(receivedBefore);

        // Assert
        Assert.Equal(ok, violation is null);
        if (!ok)
            Assert.Equal("IT-R-02", violation!.RequirementId);
    }

    #endregion

    #region IT-R-01 tx_add_input

    [Theory]
    [InlineData(0u, true)]
    [InlineData(0xFFFFFFFDu, true)]
    [InlineData(0xFFFFFFFEu, false)]
    [InlineData(0xFFFFFFFFu, false)]
    public void Given_Sequence_When_Checking_Then_FinalSequencesFail(uint sequence, bool ok)
    {
        // Arrange
        // (BOLT 2: "sequence is set to 0xFFFFFFFE or 0xFFFFFFFF")

        // Act
        var violation = InteractiveTxRules.CheckSequence(sequence);

        // Assert
        Assert.Equal(ok, violation is null);
    }

    public static TheoryData<string, bool, bool, bool, bool, string?> SharedInputCases => new()
    {
        // name, tlv set, matching txid, matching vout, already added, expected requirement (null = ok)
        { "valid", true, true, true, false, null },
        { "no shared_input_txid", false, true, true, false, "IT-R-01" },
        { "wrong txid", true, false, true, false, "SP-TX-02" },
        { "wrong vout", true, true, false, false, "SP-TX-02" },
        { "second shared input", true, true, true, true, "IT-R-01" }
    };

    [Theory]
    [MemberData(nameof(SharedInputCases))]
    public void Given_SharedInputAdd_When_Checking_Then_MatchesTheTable(string name, bool tlvSet, bool matchingTxId,
                                                                         bool matchingVout, bool alreadyAdded,
                                                                         string? expected)
    {
        // Arrange
        var spec = Splice(false).SharedInput!;
        var txId = tlvSet ? matchingTxId ? FundingTxId : TxId.One : default(TxId?);
        var vout = matchingVout ? spec.Vout : spec.Vout + 1;

        // Act
        var violation = InteractiveTxRules.CheckSharedInputAdd(txId, vout, true, spec, alreadyAdded);

        // Assert
        Assert.True(expected == violation?.RequirementId, name);
    }

    [Fact]
    public void Given_SharedInputFromNonInitiator_When_Checking_Then_Violation()
    {
        // Arrange
        var spec = Splice(true).SharedInput!;

        // Act
        var violation = InteractiveTxRules.CheckSharedInputAdd(FundingTxId, spec.Vout, false, spec, false);

        // Assert
        Assert.Equal("SP-TX-01", violation?.RequirementId);
    }

    [Fact]
    public void Given_SharedInputWithoutSpec_When_Checking_Then_Violation()
    {
        // Act
        var violation = InteractiveTxRules.CheckSharedInputAdd(FundingTxId, 1, true, null, false);

        // Assert
        Assert.Equal("IT-R-01", violation?.RequirementId);
    }

    public static TheoryData<string, PrevTxInspection, uint, bool, bool> PrevTxCases
    {
        get
        {
            var txId = PrevTxId(PrevTx(1));
            var amount = LightningMoney.Satoshis(1_000);
            var p2Pkh = new BitcoinScript([0x76, 0xa9, 0x14, .. new byte[20], 0x88, 0xac]);
            return new TheoryData<string, PrevTxInspection, uint, bool, bool>
            {
                // name, inspection, vout, outpoint already added, ok
                { "valid P2WPKH", new PrevTxInspection(true, txId, 1, amount, P2Wpkh, true, null), 0, false, true },
                { "valid P2WSH", new PrevTxInspection(true, txId, 1, amount, P2Wsh, true, null), 0, false, true },
                { "valid P2TR", new PrevTxInspection(true, txId, 1, amount, P2Tr, true, null), 0, false, true },
                { "invalid tx", new PrevTxInspection(false, null, 0, null, null, false, "bad"), 0, false, false },
                { "vout out of range", new PrevTxInspection(true, txId, 1, amount, P2Wpkh, true, null), 1, false, false },
                { "P2PKH", new PrevTxInspection(true, txId, 1, amount, p2Pkh, false, null), 0, false, false },
                { "inspector says witness but not", new PrevTxInspection(true, txId, 1, amount, p2Pkh, true, null), 0, false, false },
                { "duplicate outpoint", new PrevTxInspection(true, txId, 1, amount, P2Wpkh, true, null), 0, true, false }
            };
        }
    }

    [Theory]
    [MemberData(nameof(PrevTxCases))]
    public void Given_PrevTxInspection_When_Checking_Then_MatchesTheTable(string name, PrevTxInspection inspection,
                                                                           uint vout, bool alreadyAdded, bool ok)
    {
        // Arrange
        // (BOLT 2: prevtx not valid; prevtx_vout >= outputs; scriptPubKey not a witness program; identical to a
        // previously added input)

        // Act
        var violation = InteractiveTxRules.CheckPrevTx(inspection, vout, (_, _) => alreadyAdded);

        // Assert
        Assert.True(ok == violation is null, name);
        if (!ok)
            Assert.Equal("IT-R-01", violation!.RequirementId);
    }

    [Fact]
    public void Given_SameOutpoint_When_CheckingPrevTx_Then_ComparesTxIdAndVoutNotRawBytes()
    {
        // Arrange (NL-219: uniqueness by outpoint)
        var txId = PrevTxId(PrevTx(7));
        var inspection = new PrevTxInspection(true, txId, 2, LightningMoney.Satoshis(1_000), P2Wpkh, true, null);
        TxId? seenTxId = null;
        uint? seenVout = null;

        // Act
        _ = InteractiveTxRules.CheckPrevTx(inspection, 1, (t, v) =>
        {
            seenTxId = t;
            seenVout = v;
            return false;
        });

        // Assert
        Assert.Equal(txId, seenTxId);
        Assert.Equal(1u, seenVout);
    }

    [Theory]
    [InlineData("0014" + "0000000000000000000000000000000000000000", true)] // P2WPKH
    [InlineData("0020" + "0000000000000000000000000000000000000000000000000000000000000000", true)] // P2WSH
    [InlineData("5120" + "0000000000000000000000000000000000000000000000000000000000000000", true)] // P2TR
    [InlineData("6002" + "0000", true)] // v16, 2 bytes
    [InlineData("0028" + "00000000000000000000000000000000000000000000000000000000000000000000000000000000", true)] // 40
    [InlineData("0029" + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000", false)] // 41
    [InlineData("0001" + "00", false)] // 1-byte push
    [InlineData("4f02" + "0000", false)] // OP_1NEGATE
    [InlineData("0014" + "00000000000000000000000000000000000000", false)] // push length mismatch
    [InlineData("76a914" + "0000000000000000000000000000000000000000" + "88ac", false)] // P2PKH
    [InlineData("", false)]
    public void Given_Script_When_CheckingWitnessProgram_Then_OneBytePushThen2To40Bytes(string hex, bool expected)
    {
        // Arrange
        // (BOLT 2: "not exactly a 1-byte push opcode (for the numeric values 0 to 16) followed by a data push between
        // 2 and 40 bytes")

        // Act
        var result = InteractiveTxRules.IsWitnessProgram(Convert.FromHexString(hex));

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region IT-R-02 tx_add_output

    public static TheoryData<string, long, string, string?> OutputCases => new()
    {
        // name, sats, script hex, expected requirement (null = ok)
        { "P2WPKH at dust", 294, "0014" + new string('0', 40), null },
        { "P2WPKH below dust", 293, "0014" + new string('0', 40), "IT-R-02" },
        { "P2WSH at dust", 330, "0020" + new string('0', 64), null },
        { "P2WSH below dust", 329, "0020" + new string('0', 64), "IT-R-02" },
        { "P2TR at dust", 330, "5120" + new string('0', 64), null },
        { "P2PKH at dust", 546, "76a914" + new string('0', 40) + "88ac", null },
        { "P2PKH below dust", 545, "76a914" + new string('0', 40) + "88ac", "IT-R-02" },
        { "P2SH at dust", 540, "a914" + new string('0', 40) + "87", null },
        { "OP_RETURN zero", 0, "6a0400000000", null },
        { "empty script", 10_000, "", "IT-R-02" },
        { "bare multisig", 10_000, "5121" + new string('0', 66) + "51ae", "IT-R-02" },
        { "v0 of 25 bytes", 10_000, "0019" + new string('0', 50), "IT-R-02" }
    };

    [Theory]
    [MemberData(nameof(OutputCases))]
    public void Given_Output_When_Checking_Then_MatchesTheTable(string name, long sats, string scriptHex,
                                                                string? expected)
    {
        // Arrange
        var script = new BitcoinScript(Convert.FromHexString(scriptHex));

        // Act
        var violation = InteractiveTxRules.CheckOutput(LightningMoney.Satoshis(sats), script);

        // Assert
        Assert.True(expected == violation?.RequirementId, $"{name}: {violation?.Reason}");
    }

    [Fact]
    public void Given_OutputAboveMaxMoney_When_Checking_Then_Violation()
    {
        // Arrange
        // (BOLT 2: "the sats amount is greater than 2,100,000,000,000,000 (MAX_MONEY)")
        var amount = LightningMoney.Satoshis(InteractiveTxRules.MaxMoneySatoshis + 1);

        // Act
        var atMax = InteractiveTxRules.CheckOutput(LightningMoney.Satoshis(InteractiveTxRules.MaxMoneySatoshis),
                                                   P2Wpkh);
        var aboveMax = InteractiveTxRules.CheckOutput(amount, P2Wpkh);

        // Assert
        Assert.Null(atMax);
        Assert.Equal("IT-R-02", aboveMax?.RequirementId);
    }

    #endregion

    #region IT-R-03 tx_remove_*

    [Theory]
    [InlineData(null, true, "IT-R-03")]
    [InlineData(InteractiveTxParty.Local, true, "IT-R-03")]
    [InlineData(InteractiveTxParty.Remote, true, null)]
    [InlineData(null, false, "IT-R-03")]
    [InlineData(InteractiveTxParty.Local, false, "IT-R-03")]
    [InlineData(InteractiveTxParty.Remote, false, null)]
    public void Given_RemovedSerialId_When_Checking_Then_OnlyTheSendersCurrentItemsCanBeRemoved(
        InteractiveTxParty? addedBy, bool isInput, string? expected)
    {
        // Arrange
        // (BOLT 2: "the input or output identified by the serial_id was not added by the sender; the serial_id does
        // not correspond to a currently added input (or output)")

        // Act
        var violation = InteractiveTxRules.CheckRemove(5, addedBy, isInput);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    #endregion

    #region IT-R-04 tx_complete

    private static InteractiveTxInput RemoteInput(ulong serialId, long sats) =>
        new(serialId, InteractiveTxParty.Remote, PrevTxId(PrevTx((int)serialId)), 0, Sequence,
            LightningMoney.Satoshis(sats), P2Wpkh, PrevTx((int)serialId), false);

    private static InteractiveTxInput LocalInput(ulong serialId, long sats) =>
        RemoteInput(serialId, sats) with { AddedBy = InteractiveTxParty.Local };

    private static InteractiveTxOutput RemoteOutput(ulong serialId, long sats) =>
        new(serialId, InteractiveTxParty.Remote, LightningMoney.Satoshis(sats), P2Wpkh, false);

    [Theory]
    [InlineData(252, 1, null)]
    [InlineData(253, 1, "IT-R-04")]
    [InlineData(1, 252, null)]
    [InlineData(1, 253, "IT-R-04")]
    public void Given_InputAndOutputCounts_When_CheckingTxComplete_Then_At252(int inputs, int outputs,
                                                                              string? expected)
    {
        // Arrange
        // (BOLT 2: "there are more than 252 inputs; there are more than 252 outputs")
        var ins = Enumerable.Range(0, inputs).Select(i => LocalInput((ulong)i * 2, 1_000)).ToList();
        var outs = Enumerable.Range(0, outputs).Select(i => RemoteOutput((ulong)i * 2 + 1, 0)).ToList();

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(ins, outs, null);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Theory]
    [InlineData(10_000, 10_000, null)]
    [InlineData(10_000, 9_000, null)]
    [InlineData(10_000, 10_001, "IT-R-04")]
    public void Given_PeerInputsAndOutputs_When_CheckingTxComplete_Then_InputsMustCoverOutputs(long inSats,
        long outSats, string? expected)
    {
        // Arrange
        // (BOLT 2: "the peer's total input satoshis is less than their outputs")
        var ins = new List<InteractiveTxInput> { RemoteInput(1, inSats), LocalInput(0, 1_000_000) };
        var outs = new List<InteractiveTxOutput> { RemoteOutput(1, outSats) };

        // Act
        var violation = InteractiveTxRules.CheckTxComplete(ins, outs, null);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Theory]
    [InlineData(400_000, 0, null)] // splice-in of nothing: share in = share out
    [InlineData(390_000, 0, null)] // splice-out 10,000 taken by the output below
    [InlineData(400_000, 1, "IT-R-04")] // share out above share in with no input
    public void Given_SharedFundingShares_When_CheckingTxComplete_Then_PeersPortionCounts(long remoteOutShare,
        long extra, string? expected)
    {
        // Arrange
        // (BOLT 2: "One MUST account for the peer's portion of the funding output")
        var spec = Splice(true, 990_000 + extra, 600_000, remoteOutShare + extra);
        var shared = new InteractiveTxInput(0, InteractiveTxParty.Local, FundingTxId, 1, Sequence,
                                            LightningMoney.Satoshis(1_000_000), FundingScript, null, true);
        var funding = new InteractiveTxOutput(2, InteractiveTxParty.Local, spec.SharedOutputAmount, FundingScript,
                                              true);
        var outs = new List<InteractiveTxOutput> { funding };
        if (remoteOutShare < 400_000)
            outs.Add(RemoteOutput(1, 400_000 - remoteOutShare));

        // Act
        var violation = InteractiveTxRules.CheckTxComplete([shared], outs, spec);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    [Fact]
    public void Given_SpliceWithoutSharedInput_When_CheckingTxComplete_Then_SpTx05()
    {
        // Arrange
        // (BOLT 2 splicing: "There is not exactly one input spending the current funding transaction.")
        var spec = Splice(true);
        var funding = new InteractiveTxOutput(2, InteractiveTxParty.Local, spec.SharedOutputAmount, FundingScript,
                                              true);

        // Act
        var violation = InteractiveTxRules.CheckTxComplete([LocalInput(0, 1_000)], [funding], spec);

        // Assert
        Assert.Equal("SP-TX-05", violation?.RequirementId);
    }

    [Theory]
    [InlineData(0, 1_000_000, "SP-TX-05")]
    [InlineData(2, 1_000_000, "SP-TX-05")]
    [InlineData(1, 999_999, "SP-TX-05")]
    [InlineData(1, 1_000_000, null)]
    public void Given_FundingOutputs_When_CheckingTxComplete_Then_ExactlyOneWithTheAmount(int count, long sats,
                                                                                          string? expected)
    {
        // Arrange
        // (BOLT 2 splicing: "There is not exactly one channel funding output using the funding public keys and
        // funding contributions")
        var spec = Splice(true);
        var shared = new InteractiveTxInput(0, InteractiveTxParty.Local, FundingTxId, 1, Sequence,
                                            LightningMoney.Satoshis(1_000_000), FundingScript, null, true);
        var outs = Enumerable.Range(0, count)
                             .Select(i => new InteractiveTxOutput((ulong)(2 + 2 * i), InteractiveTxParty.Local,
                                                                  LightningMoney.Satoshis(sats), FundingScript, true))
                             .ToList();

        // Act
        var violation = InteractiveTxRules.CheckTxComplete([shared], outs, spec);

        // Assert
        Assert.Equal(expected, violation?.RequirementId);
    }

    #endregion

    #region IT-SIG-02 tx_signatures

    [Fact]
    public void Given_ValidSignatures_When_Checking_Then_Ok()
    {
        // Arrange
        var inputs = new[] { RemoteInput(1, 1_000), RemoteInput(3, 1_000) };

        // Act
        var violation = InteractiveTxRules.CheckTxSignatures(TxId.One, [P2WpkhWitness(), P2WpkhWitness()], TxId.One,
                                                             inputs);

        // Assert
        Assert.Null(violation);
    }

    [Fact]
    public void Given_WrongTxId_When_CheckingSignatures_Then_Violation()
    {
        // Arrange
        // (BOLT 2: "the txid does not match the txid of the transaction")

        // Act
        var violation = InteractiveTxRules.CheckTxSignatures(TxId.Zero, [P2WpkhWitness()], TxId.One,
                                                             [RemoteInput(1, 1_000)]);

        // Assert
        Assert.Equal("IT-SIG-02", violation?.RequirementId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Given_WrongWitnessCount_When_CheckingSignatures_Then_Violation(int count)
    {
        // Arrange
        // (BOLT 2: "the number of witnesses does not equal the number of inputs added by the sending node")
        var witnesses = Enumerable.Range(0, count).Select(_ => P2WpkhWitness()).ToList();

        // Act
        var violation = InteractiveTxRules.CheckTxSignatures(TxId.One, witnesses, TxId.One, [RemoteInput(1, 1_000)]);

        // Assert
        Assert.Equal("IT-SIG-02", violation?.RequirementId);
    }

    public static TheoryData<string, byte[], string, bool> WitnessCases
    {
        get
        {
            byte[] pubKey = [0x02, .. Enumerable.Repeat((byte)0x01, 32)];
            var p2Wpkh = Convert.ToHexString(P2Wpkh);
            var p2Wsh = Convert.ToHexString(P2Wsh);
            var p2Tr = Convert.ToHexString(P2Tr);
            return new TheoryData<string, byte[], string, bool>
            {
                // name, witness bytes, spent script hex, ok
                { "empty bytes", [], p2Wpkh, false },
                { "zero items", [0x00], p2Wpkh, false },
                { "truncated", [0x02, 0x05, 0x01], p2Wpkh, false },
                { "trailing bytes", [.. (byte[])P2WpkhWitness(), 0x00], p2Wpkh, false },
                { "P2WPKH ok", P2WpkhWitness(), p2Wpkh, true },
                { "P2WPKH SIGHASH_NONE", P2WpkhWitness(0x02), p2Wpkh, false },
                { "P2WPKH ANYONECANPAY", P2WpkhWitness(0x81), p2Wpkh, false },
                { "P2WPKH not DER", WitnessOf([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x01], pubKey), p2Wpkh, false },
                { "P2WPKH one item", WitnessOf(DerSignature()), p2Wpkh, false },
                { "P2WPKH uncompressed key", WitnessOf(DerSignature(), [0x04, .. new byte[64]]), p2Wpkh, false },
                { "P2TR 64-byte (default)", WitnessOf(new byte[64]), p2Tr, true },
                { "P2TR 65-byte ALL", WitnessOf([.. new byte[64], 0x01]), p2Tr, true },
                { "P2TR 65-byte NONE", WitnessOf([.. new byte[64], 0x02]), p2Tr, false },
                { "P2TR 63-byte", WitnessOf(new byte[63]), p2Tr, false },
                { "P2TR annex", WitnessOf(new byte[64], [0x50, 0x01]), p2Tr, false },
                { "P2WSH multisig ok", WitnessOf([], DerSignature(), DerSignature(), new byte[71]), p2Wsh, true },
                { "P2WSH SIGHASH_SINGLE", WitnessOf([], DerSignature(0x03), new byte[71]), p2Wsh, false },
                { "P2WSH item above 80", WitnessOf(new byte[81], new byte[10]), p2Wsh, false }
            };
        }
    }

    [Theory]
    [MemberData(nameof(WitnessCases))]
    public void Given_Witness_When_Checking_Then_MatchesTheTable(string name, byte[] witness, string scriptHex,
                                                                 bool ok)
    {
        // Arrange
        // (BOLT 2: "the message contains an empty witness; [...] the witnesses are non-standard; a signature uses a
        // flag that is not SIGHASH_ALL (0x01)")

        // Act
        var violation = InteractiveTxRules.CheckWitness(witness, Convert.FromHexString(scriptHex));

        // Assert
        Assert.True(ok == violation is null, $"{name}: {violation?.Reason}");
        if (!ok)
            Assert.Equal("IT-SIG-02", violation!.RequirementId);
    }

    [Fact]
    public void Given_P2WshWithTooManyItems_When_CheckingWitness_Then_NonStandard()
    {
        // Arrange (Bitcoin Core: at most 100 stack items besides the script)
        var items = Enumerable.Range(0, 101).Select(_ => Array.Empty<byte>()).Append(new byte[10]).ToArray();
        var bytes = new List<byte> { 0x66 };
        foreach (var item in items)
        {
            bytes.Add((byte)item.Length);
            bytes.AddRange(item);
        }

        // Act
        var violation = InteractiveTxRules.CheckWitness(new Witness([.. bytes]), P2Wsh);

        // Assert
        Assert.Equal("IT-SIG-02", violation?.RequirementId);
    }

    [Fact]
    public void Given_MultiByteCompactSizes_When_ParsingWitness_Then_Parsed()
    {
        // Arrange: one item of 300 bytes (0xfd 0x2c 0x01)
        var data = new byte[] { 0x01, 0xfd, 0x2c, 0x01 }.Concat(new byte[300]).ToArray();

        // Act
        var parsed = InteractiveTxRules.TryParseWitnessStack(data, out var stack);

        // Assert
        Assert.True(parsed);
        Assert.Single(stack);
        Assert.Equal(300, stack[0].Length);
    }

    #endregion

    #region tx_abort data

    [Theory]
    [InlineData(new byte[] { 0x68, 0x69 }, "hi")]
    [InlineData(new byte[] { 0x20, 0x7e }, " ~")]
    [InlineData(new byte[] { 0x68, 0x0a }, "0x680a")]
    [InlineData(new byte[] { 0x7f }, "0x7f")]
    [InlineData(new byte[] { }, "")]
    public void Given_AbortData_When_Describing_Then_NonPrintableIsHex(byte[] data, string expected)
    {
        // Arrange
        // (BOLT 2: "if data is not composed solely of printable ASCII characters [...] SHOULD NOT print out data
        // verbatim")

        // Act
        var text = InteractiveTxRules.DescribeAbortData(data);

        // Assert
        Assert.Equal(expected, text);
    }

    #endregion
}