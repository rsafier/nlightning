namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Enums;
using Models;
using Money;

/// <summary>
/// The receiver-side rules of BOLT 2 "Interactive Transaction Construction" (splicing plan §1.2, IT-S/IT-R/IT-SIG),
/// pure and table-testable. <see cref="InteractiveTxSession"/> applies them to every message it receives; each check
/// returns null when the rule holds, or the <see cref="InteractiveTxRuleViolation"/> the negotiation fails with.
/// </summary>
/// <remarks>
/// Moved here from the old <c>Infrastructure/Protocol/Validators/Tx*Validator</c> (wave IT, IT1-T1), with the fixes of
/// NL-219 (the 4096 cap counts <b>received messages</b>, not the inputs currently held; input uniqueness compares
/// (txid, vout), not raw <c>prevtx</c> bytes) and NL-041 (the <c>prevtx</c> and script checks are real, through
/// <see cref="Interfaces.IPrevTxInspector"/>).
/// </remarks>
public static class InteractiveTxRules
{
    /// <summary>
    /// BOLT 2 (tx_add_input/tx_add_output receiver): "MUST fail the negotiation if: [...] has received 4096
    /// <c>tx_add_input</c> [<c>tx_add_output</c>] messages during this negotiation".
    /// </summary>
    public const int MaxReceivedAddMessages = 4096;

    /// <summary>BOLT 2 (tx_complete receiver): "there are more than 252 inputs".</summary>
    public const int MaxInputs = 252;

    /// <summary>BOLT 2 (tx_complete receiver): "there are more than 252 outputs".</summary>
    public const int MaxOutputs = 252;

    /// <summary>BOLT 2 (tx_add_input sender): "MUST set <c>sequence</c> to be less than or equal to 4294967293".</summary>
    public const uint MaxSequence = 0xFFFFFFFD;

    /// <summary>BOLT 2 (tx_add_output receiver): <c>MAX_MONEY</c>, in satoshis.</summary>
    public const ulong MaxMoneySatoshis = 2_100_000_000_000_000;

    /// <summary>BOLT 2 (tx_complete receiver): <c>MAX_STANDARD_TX_WEIGHT</c>.</summary>
    public const long MaxStandardTxWeight = 400_000;

    /// <summary>The SIGHASH_ALL flag every signature of <c>tx_signatures</c> must use (BOLT 2).</summary>
    public const byte SighashAll = 0x01;

    // Bitcoin Core standardness limits for a P2WSH spend (policy/policy.h).
    private const int MaxStandardP2WshStackItems = 100;
    private const int MaxStandardP2WshStackItemSize = 80;
    private const int MaxStandardP2WshScriptSize = 3600;

    // Bitcoin Core's default dustRelayFee (3000 sat/kvB) and the spend sizes of GetDustThreshold.
    private const ulong DustRelayFeePerKvB = 3000;
    private const int WitnessSpendSize = 32 + 4 + 1 + 107 / 4 + 4;
    private const int LegacySpendSize = 32 + 4 + 1 + 107 + 4;

    private const byte OpReturn = 0x6a;

    #region Serial ids and turns (IT-S-01, IT-S-02)

    /// <summary>
    /// BOLT 2: "if is the <i>initiator</i>: MUST send even <c>serial_id</c>s; if is the <i>non-initiator</i>: MUST send
    /// odd <c>serial_id</c>s" (IT-S-01).
    /// </summary>
    public static bool HasSenderParity(ulong serialId, bool isSenderInitiator) =>
        (serialId & 1UL) == (isSenderInitiator ? 0UL : 1UL);

    /// <summary>
    /// A received <c>tx_add_input</c>/<c>tx_add_output</c>'s <c>serial_id</c>: BOLT 2 "MUST fail the negotiation if:
    /// the <c>serial_id</c> is already included in the transaction; the <c>serial_id</c> has the wrong parity".
    /// </summary>
    /// <param name="serialId">The received <c>serial_id</c>.</param>
    /// <param name="isSenderInitiator">Whether the peer (the sender) is the initiator.</param>
    /// <param name="isAlreadyIncluded">Whether an input (for <c>tx_add_input</c>) or output (for
    /// <c>tx_add_output</c>) with that <c>serial_id</c> is currently added.</param>
    public static InteractiveTxRuleViolation? CheckAddedSerialId(ulong serialId, bool isSenderInitiator,
                                                                bool isAlreadyIncluded)
    {
        if (!HasSenderParity(serialId, isSenderInitiator))
            return new InteractiveTxRuleViolation("IT-S-01", $"serial_id {serialId} has the wrong parity");

        return isAlreadyIncluded
                   ? new InteractiveTxRuleViolation("IT-S-01", $"serial_id {serialId} is already included")
                   : null;
    }

    /// <summary>
    /// BOLT 2 (tx_add_input receiver): "MUST fail the negotiation if: [...] has received 4096 <c>tx_add_input</c>
    /// messages during this negotiation" (IT-R-01, NL-219). Removals do not give the count back.
    /// </summary>
    /// <param name="receivedBefore">The <c>tx_add_input</c>s received before this one.</param>
    public static InteractiveTxRuleViolation? CheckReceivedAddInputCount(int receivedBefore) =>
        receivedBefore + 1 >= MaxReceivedAddMessages
            ? new InteractiveTxRuleViolation("IT-R-01", $"received {MaxReceivedAddMessages} tx_add_input")
            : null;

    /// <summary>
    /// BOLT 2 (tx_add_output receiver): "it has received 4096 <c>tx_add_output</c> messages during this negotiation"
    /// (IT-R-02, NL-219).
    /// </summary>
    /// <param name="receivedBefore">The <c>tx_add_output</c>s received before this one.</param>
    public static InteractiveTxRuleViolation? CheckReceivedAddOutputCount(int receivedBefore) =>
        receivedBefore + 1 >= MaxReceivedAddMessages
            ? new InteractiveTxRuleViolation("IT-R-02", $"received {MaxReceivedAddMessages} tx_add_output")
            : null;

    #endregion

    #region tx_add_input (IT-R-01)

    /// <summary>
    /// BOLT 2 (tx_add_input receiver): "<c>sequence</c> is set to <c>0xFFFFFFFE</c> or <c>0xFFFFFFFF</c>".
    /// </summary>
    public static InteractiveTxRuleViolation? CheckSequence(uint sequence) =>
        sequence > MaxSequence
            ? new InteractiveTxRuleViolation("IT-R-01", $"sequence 0x{sequence:X8} does not signal replaceability")
            : null;

    /// <summary>
    /// A received <c>tx_add_input</c> with <c>prevtx_len</c> = 0. BOLT 2: "if <c>prevtx_len</c> is <c>0</c>:
    /// <c>shared_input_txid</c> is not set; <c>shared_input_txid</c> and <c>prevtx_vout</c> don't match the previous
    /// funding output; a previously added (and not removed) input already exists with <c>shared_input_txid</c> set".
    /// Also refused: a shared input from the non-initiator (BOLT 2 splicing: "If it is the splice initiator: MUST add
    /// the current channel input"), and a shared input where the negotiation has none.
    /// </summary>
    /// <param name="sharedInputTxId">The <c>shared_input_txid</c> TLV, or null when absent.</param>
    /// <param name="prevTxVout">The <c>prevtx_vout</c>.</param>
    /// <param name="isSenderInitiator">Whether the peer is the initiator.</param>
    /// <param name="expected">The funding output the negotiation spends (<see cref="SharedFundingSpec.SharedInput"/>),
    /// or null when it spends none.</param>
    /// <param name="isSharedInputAlreadyAdded">Whether a shared input is currently added.</param>
    public static InteractiveTxRuleViolation? CheckSharedInputAdd(TxId? sharedInputTxId, uint prevTxVout,
                                                                 bool isSenderInitiator, SharedFundingInput? expected,
                                                                 bool isSharedInputAlreadyAdded)
    {
        if (sharedInputTxId is null)
            return new InteractiveTxRuleViolation("IT-R-01", "prevtx_len is 0 without shared_input_txid");

        if (expected is null)
            return new InteractiveTxRuleViolation("IT-R-01", "shared_input_txid in a negotiation without a shared input");

        if (sharedInputTxId.Value != expected.TxId || prevTxVout != expected.Vout)
            return new InteractiveTxRuleViolation("SP-TX-02",
                                                  "shared_input_txid and prevtx_vout don't match the funding output");

        if (isSharedInputAlreadyAdded)
            return new InteractiveTxRuleViolation("IT-R-01", "a shared input is already added");

        return isSenderInitiator
                   ? null
                   : new InteractiveTxRuleViolation("SP-TX-01", "the shared input was added by the non-initiator");
    }

    /// <summary>
    /// A received <c>tx_add_input</c> with a <c>prevtx</c>. BOLT 2: "if <c>prevtx_len</c> is not <c>0</c>:
    /// <c>prevtx</c> and <c>prevtx_vout</c> are identical to a previously added (and not removed) input; <c>prevtx</c>
    /// is not a valid transaction; <c>prevtx_vout</c> is greater or equal to the number of outputs on <c>prevtx</c>;
    /// the <c>scriptPubKey</c> of the <c>prevtx_vout</c> output of <c>prevtx</c> is not exactly a 1-byte push opcode
    /// (for the numeric values <c>0</c> to <c>16</c>) followed by a data push between 2 and 40 bytes".
    /// </summary>
    /// <param name="inspection">What <see cref="Interfaces.IPrevTxInspector.Inspect"/> read.</param>
    /// <param name="prevTxVout">The <c>prevtx_vout</c>.</param>
    /// <param name="isOutpointAlreadyAdded">Whether an input currently added (by either side, the shared input
    /// included) spends the same (txid, vout) (NL-219: compared by outpoint, not by raw bytes).</param>
    public static InteractiveTxRuleViolation? CheckPrevTx(PrevTxInspection inspection, uint prevTxVout,
                                                         Func<TxId, uint, bool> isOutpointAlreadyAdded)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(isOutpointAlreadyAdded);

        if (!inspection.IsValid || inspection.TxId is null)
            return new InteractiveTxRuleViolation("IT-R-01",
                                                  $"prevtx is not a valid transaction: {inspection.FailureReason}");

        if (prevTxVout >= (uint)Math.Max(inspection.OutputCount, 0) || inspection.Amount is null
                                                                     || inspection.ScriptPubKey is null)
            return new InteractiveTxRuleViolation("IT-R-01",
                                                  $"prevtx_vout {prevTxVout} is out of range ({inspection.OutputCount} outputs)");

        if (!inspection.IsWitnessProgram || !IsWitnessProgram((byte[])inspection.ScriptPubKey.Value))
            return new InteractiveTxRuleViolation("IT-R-01", "the spent scriptPubKey is not a witness program");

        return isOutpointAlreadyAdded(inspection.TxId.Value, prevTxVout)
                   ? new InteractiveTxRuleViolation("IT-R-01",
                                                    $"{inspection.TxId.Value}:{prevTxVout} is already an input")
                   : null;
    }

    #endregion

    #region tx_add_output (IT-R-02)

    /// <summary>
    /// A received <c>tx_add_output</c>'s value and script. BOLT 2: "MUST accept P2WSH, P2WPKH, P2TR <c>script</c>s; MAY
    /// fail the negotiation if <c>script</c> is non-standard; MUST fail the negotiation if: [...] the <c>sats</c> amount
    /// is less than the <c>dust_limit</c>; the <c>sats</c> amount is greater than 2,100,000,000,000,000
    /// (<c>MAX_MONEY</c>)".
    /// </summary>
    /// <remarks>
    /// The <c>dust_limit</c> is Bitcoin Core's standard dust threshold of the script (<see cref="GetDustThreshold"/>),
    /// since the session parameters carry no negotiated one: 294 sat for P2WPKH, 330 for P2WSH/P2TR, 546 for P2PKH,
    /// 540 for P2SH and 0 for <c>OP_RETURN</c>. "Non-standard" is anything but P2PKH, P2SH, a witness program (v0 of 20
    /// or 32 bytes, v1-16 of 2-40 bytes) or an <c>OP_RETURN</c> (<see cref="IsStandardOutputScript"/>).
    /// </remarks>
    public static InteractiveTxRuleViolation? CheckOutput(LightningMoney amount, BitcoinScript script)
    {
        ArgumentNullException.ThrowIfNull(amount);

        byte[] bytes = script;
        if (!IsStandardOutputScript(bytes))
            return new InteractiveTxRuleViolation("IT-R-02", "the output script is non-standard");

        var sats = amount.MilliSatoshi / 1_000;
        if (amount.MilliSatoshi % 1_000 != 0)
            return new InteractiveTxRuleViolation("IT-R-02", "the output amount is not whole satoshis");

        var dust = GetDustThreshold(bytes);
        if (sats < dust)
            return new InteractiveTxRuleViolation("IT-R-02", $"the sats amount {sats} is less than the dust limit {dust}");

        return sats > MaxMoneySatoshis
                   ? new InteractiveTxRuleViolation("IT-R-02", $"the sats amount {sats} is greater than MAX_MONEY")
                   : null;
    }

    #endregion

    #region tx_remove_input / tx_remove_output (IT-R-03)

    /// <summary>
    /// BOLT 2 (tx_remove_input/tx_remove_output receiver): "MUST fail the negotiation if: the input or output
    /// identified by the <c>serial_id</c> was not added by the sender; the <c>serial_id</c> does not correspond to a
    /// currently added input (or output)" (IT-R-03).
    /// </summary>
    /// <param name="serialId">The received <c>serial_id</c>.</param>
    /// <param name="currentlyAddedBy">Who added the input (or output) with that <c>serial_id</c>, or null when none is
    /// currently added.</param>
    /// <param name="isInput">Whether it is <c>tx_remove_input</c> (for the reason text).</param>
    public static InteractiveTxRuleViolation? CheckRemove(ulong serialId, InteractiveTxParty? currentlyAddedBy,
                                                          bool isInput)
    {
        var kind = isInput ? "input" : "output";
        return currentlyAddedBy switch
        {
            null => new InteractiveTxRuleViolation("IT-R-03",
                                                   $"serial_id {serialId} is not a currently added {kind}"),
            InteractiveTxParty.Local => new InteractiveTxRuleViolation("IT-R-03",
                                                                       $"the {kind} {serialId} was not added by the sender"),
            _ => null
        };
    }

    #endregion

    #region tx_complete (IT-R-04, SP-TX-05)

    /// <summary>
    /// The checks at the end of the negotiation (two consecutive <c>tx_complete</c>). BOLT 2 (tx_complete receiver):
    /// "MUST fail the negotiation if: the peer's total input satoshis is less than their outputs. One MUST account for
    /// the peer's portion of the funding output when verifying compliance with this requirement. [...] there are more
    /// than 252 inputs; there are more than 252 outputs". With a <see cref="SharedFundingSpec"/> also (BOLT 2 splicing,
    /// SP-TX-05): "There is not exactly one input spending the current funding transaction. There is not exactly one
    /// channel funding output using the funding public keys and funding contributions".
    /// </summary>
    /// <remarks>
    /// The feerate checks of IT-R-04 ("the peer's paid feerate does not meet or exceed the agreed feerate", the
    /// initiator's common fields) and the 400,000 weight cap need the collaborative fee calculator (IT1-T2) and are not
    /// applied here yet.
    /// </remarks>
    /// <param name="inputs">Every input currently added.</param>
    /// <param name="outputs">Every output currently added.</param>
    /// <param name="sharedFunding">The shared input/output, or null.</param>
    public static InteractiveTxRuleViolation? CheckTxComplete(IReadOnlyList<InteractiveTxInput> inputs,
                                                              IReadOnlyList<InteractiveTxOutput> outputs,
                                                              SharedFundingSpec? sharedFunding)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        if (inputs.Count > MaxInputs)
            return new InteractiveTxRuleViolation("IT-R-04", $"there are more than {MaxInputs} inputs");

        if (outputs.Count > MaxOutputs)
            return new InteractiveTxRuleViolation("IT-R-04", $"there are more than {MaxOutputs} outputs");

        if (sharedFunding is not null)
        {
            var violation = CheckSharedFunding(inputs, outputs, sharedFunding);
            if (violation is not null)
                return violation;
        }

        // The peer's side: its inputs plus its share of the shared input must cover its outputs plus its share of the
        // shared output.
        ulong remoteIn = 0, remoteOut = 0;
        foreach (var input in inputs)
        {
            if (!input.IsShared && input.AddedBy == InteractiveTxParty.Remote)
                remoteIn = checked(remoteIn + input.Amount.MilliSatoshi);
        }

        foreach (var output in outputs)
        {
            if (!output.IsShared && output.AddedBy == InteractiveTxParty.Remote)
                remoteOut = checked(remoteOut + output.Amount.MilliSatoshi);
        }

        if (sharedFunding is not null)
        {
            if (sharedFunding.SharedInput is not null)
                remoteIn = checked(remoteIn + sharedFunding.RemoteInputShare.MilliSatoshi);
            remoteOut = checked(remoteOut + sharedFunding.RemoteOutputShare.MilliSatoshi);
        }

        return remoteIn < remoteOut
                   ? new InteractiveTxRuleViolation("IT-R-04",
                                                    $"the peer's inputs ({remoteIn / 1_000} sat) are less than its outputs ({remoteOut / 1_000} sat)")
                   : null;
    }

    private static InteractiveTxRuleViolation? CheckSharedFunding(IReadOnlyList<InteractiveTxInput> inputs,
                                                                  IReadOnlyList<InteractiveTxOutput> outputs,
                                                                  SharedFundingSpec spec)
    {
        if (spec.SharedInput is { } sharedInput)
        {
            var spending = 0;
            foreach (var input in inputs)
            {
                if (input.IsShared || (input.PrevTxId == sharedInput.TxId && input.PrevTxVout == sharedInput.Vout))
                    spending++;
            }

            if (spending != 1)
                return new InteractiveTxRuleViolation("SP-TX-05",
                                                      $"{spending} inputs spend the current funding output, not exactly one");
        }
        else if (inputs.Any(i => i.IsShared))
        {
            return new InteractiveTxRuleViolation("IT-R-01", "a shared input in a negotiation without one");
        }

        var fundingOutputs = outputs.Where(o => o.IsShared || o.ScriptPubKey == spec.SharedOutputScript).ToList();
        if (fundingOutputs.Count != 1)
            return new InteractiveTxRuleViolation("SP-TX-05",
                                                  $"{fundingOutputs.Count} channel funding outputs, not exactly one");

        return fundingOutputs[0].Amount.MilliSatoshi != spec.SharedOutputAmount.MilliSatoshi
                   ? new InteractiveTxRuleViolation("SP-TX-05",
                                                    $"the funding output pays {fundingOutputs[0].Amount.Satoshi} sat, not {spec.SharedOutputAmount.Satoshi}")
                   : null;
    }

    #endregion

    #region tx_signatures (IT-SIG-02)

    /// <summary>
    /// A received <c>tx_signatures</c>. BOLT 2 (receiver): "MUST fail the negotiation if: the message contains an empty
    /// <c>witness</c>; the number of <c>witnesses</c> does not equal the number of inputs added by the sending node;
    /// the <c>txid</c> does not match the txid of the transaction; the <c>witnesses</c> are non-standard; a signature
    /// uses a flag that is not <c>SIGHASH_ALL</c> (0x01)" (IT-SIG-02).
    /// </summary>
    /// <param name="receivedTxId">The message's <c>txid</c>.</param>
    /// <param name="witnesses">The message's witnesses.</param>
    /// <param name="expectedTxId">The constructed transaction's txid.</param>
    /// <param name="senderInputs">The inputs the sender added, except the shared input (whose signature travels in
    /// <c>shared_input_signature</c>), in ascending <c>serial_id</c> order.</param>
    public static InteractiveTxRuleViolation? CheckTxSignatures(TxId receivedTxId, IReadOnlyList<Witness> witnesses,
                                                                TxId expectedTxId,
                                                                IReadOnlyList<InteractiveTxInput> senderInputs)
    {
        ArgumentNullException.ThrowIfNull(witnesses);
        ArgumentNullException.ThrowIfNull(senderInputs);

        if (receivedTxId != expectedTxId)
            return new InteractiveTxRuleViolation("IT-SIG-02",
                                                  $"txid {receivedTxId} does not match the transaction {expectedTxId}");

        if (witnesses.Count != senderInputs.Count)
            return new InteractiveTxRuleViolation("IT-SIG-02",
                                                  $"{witnesses.Count} witnesses for {senderInputs.Count} inputs");

        for (var i = 0; i < witnesses.Count; i++)
        {
            var violation = CheckWitness(witnesses[i], senderInputs[i].ScriptPubKey);
            if (violation is not null)
                return violation with { Reason = $"input {senderInputs[i].SerialId}: {violation.Reason}" };
        }

        return null;
    }

    /// <summary>
    /// One <c>tx_signatures</c> witness (the BIP 141 stack serialization: a CompactSize item count, then each item as
    /// a CompactSize length and its bytes) against the scriptPubKey it spends: not empty, parsable, standard, and every
    /// signature with <c>SIGHASH_ALL</c>.
    /// </summary>
    /// <remarks>
    /// P2WPKH: exactly a DER signature ending in 0x01 and a 33-byte compressed key. P2TR key path: a 64-byte signature
    /// (<c>SIGHASH_DEFAULT</c>, which BIP 341 defines as signing everything, like <c>SIGHASH_ALL</c>) or a 65-byte one
    /// ending in 0x01; an annex is non-standard. P2WSH: Bitcoin Core's standard stack limits (100 items of at most 80
    /// bytes, a script of at most 3600 bytes), and every item encoded as a DER signature must end in 0x01. Other
    /// witness versions and P2TR script paths are only checked for a non-empty stack.
    /// </remarks>
    public static InteractiveTxRuleViolation? CheckWitness(Witness witness, BitcoinScript spentScript)
    {
        byte[] data = witness;
        if (data is null || data.Length == 0)
            return new InteractiveTxRuleViolation("IT-SIG-02", "empty witness");

        if (!TryParseWitnessStack(data, out var stack))
            return new InteractiveTxRuleViolation("IT-SIG-02", "the witness does not parse");

        if (stack.Count == 0)
            return new InteractiveTxRuleViolation("IT-SIG-02", "empty witness");

        byte[] script = spentScript;
        if (IsP2Wpkh(script))
        {
            if (stack.Count != 2 || stack[1].Length != 33 || (stack[1][0] != 0x02 && stack[1][0] != 0x03))
                return new InteractiveTxRuleViolation("IT-SIG-02", "non-standard P2WPKH witness");

            return CheckSignatureHashType(stack[0]);
        }

        if (IsP2Tr(script))
        {
            if (stack.Count >= 2 && stack[^1].Length > 0 && stack[^1][0] == 0x50)
                return new InteractiveTxRuleViolation("IT-SIG-02", "a taproot annex is non-standard");

            if (stack.Count != 1)
                return null; // script path: the leaf script decides

            return stack[0].Length switch
            {
                64 => null,
                65 when stack[0][64] == SighashAll => null,
                65 => new InteractiveTxRuleViolation("IT-SIG-02",
                                                     $"a signature uses the flag 0x{stack[0][64]:X2}, not SIGHASH_ALL"),
                _ => new InteractiveTxRuleViolation("IT-SIG-02", "non-standard P2TR key-path witness")
            };
        }

        if (IsP2Wsh(script))
        {
            if (stack.Count - 1 > MaxStandardP2WshStackItems || stack[^1].Length > MaxStandardP2WshScriptSize)
                return new InteractiveTxRuleViolation("IT-SIG-02", "non-standard P2WSH witness");

            for (var i = 0; i < stack.Count - 1; i++)
            {
                if (stack[i].Length > MaxStandardP2WshStackItemSize)
                    return new InteractiveTxRuleViolation("IT-SIG-02", "non-standard P2WSH witness item");

                if (IsDerSignature(stack[i]) && stack[i][^1] != SighashAll)
                    return new InteractiveTxRuleViolation("IT-SIG-02",
                                                          $"a signature uses the flag 0x{stack[i][^1]:X2}, not SIGHASH_ALL");
            }
        }

        return null;
    }

    private static InteractiveTxRuleViolation? CheckSignatureHashType(byte[] signature)
    {
        if (!IsDerSignature(signature))
            return new InteractiveTxRuleViolation("IT-SIG-02", "the signature is not a DER signature");

        return signature[^1] == SighashAll
                   ? null
                   : new InteractiveTxRuleViolation("IT-SIG-02",
                                                    $"a signature uses the flag 0x{signature[^1]:X2}, not SIGHASH_ALL");
    }

    #endregion

    #region Scripts and encodings

    /// <summary>
    /// BIP 141 witness program as BOLT 2 words it: "exactly a 1-byte push opcode (for the numeric values <c>0</c> to
    /// <c>16</c>) followed by a data push between 2 and 40 bytes".
    /// </summary>
    public static bool IsWitnessProgram(ReadOnlySpan<byte> script)
    {
        if (script.Length < 4 || script.Length > 42)
            return false;

        var version = script[0];
        if (version != 0x00 && (version < 0x51 || version > 0x60))
            return false;

        var pushLength = script[1];
        return pushLength is >= 2 and <= 40 && script.Length == pushLength + 2;
    }

    /// <summary>
    /// Whether an output script is standard for <c>tx_add_output</c>: P2PKH, P2SH, a witness program (v0 must be 20 or
    /// 32 bytes) or an <c>OP_RETURN</c>. P2WPKH, P2WSH and P2TR are always accepted, as BOLT 2 requires.
    /// </summary>
    public static bool IsStandardOutputScript(ReadOnlySpan<byte> script)
    {
        if (script.Length == 0)
            return false;

        if (script[0] == OpReturn)
            return true;

        if (IsWitnessProgram(script))
            return script[0] != 0x00 || script[1] is 20 or 32;

        // P2PKH: OP_DUP OP_HASH160 <20> OP_EQUALVERIFY OP_CHECKSIG
        if (script.Length == 25 && script[0] == 0x76 && script[1] == 0xa9 && script[2] == 0x14 && script[23] == 0x88
            && script[24] == 0xac)
            return true;

        // P2SH: OP_HASH160 <20> OP_EQUAL
        return script.Length == 23 && script[0] == 0xa9 && script[1] == 0x14 && script[22] == 0x87;
    }

    /// <summary>
    /// Bitcoin Core's dust threshold of an output (<c>GetDustThreshold</c> at the default 3000 sat/kvB
    /// <c>dustRelayFee</c>), in satoshis: 0 for <c>OP_RETURN</c>, 294 for P2WPKH, 330 for P2WSH and P2TR, 546 for P2PKH.
    /// </summary>
    public static ulong GetDustThreshold(ReadOnlySpan<byte> script)
    {
        if (script.Length > 0 && script[0] == OpReturn)
            return 0;

        var outputSize = 8 + CompactSizeLength((ulong)script.Length) + script.Length;
        var spendSize = IsWitnessProgram(script) ? WitnessSpendSize : LegacySpendSize;
        return (ulong)(outputSize + spendSize) * DustRelayFeePerKvB / 1000;
    }

    /// <summary>
    /// Parses a BIP 141 witness stack serialization: a CompactSize item count, then each item as a CompactSize length
    /// and its bytes, with nothing left over.
    /// </summary>
    public static bool TryParseWitnessStack(ReadOnlySpan<byte> data, out IReadOnlyList<byte[]> stack)
    {
        var items = new List<byte[]>();
        stack = items;

        if (!TryReadCompactSize(ref data, out var count) || count > (ulong)data.Length)
            return false;

        for (ulong i = 0; i < count; i++)
        {
            if (!TryReadCompactSize(ref data, out var length) || length > (ulong)data.Length)
                return false;

            items.Add(data[..(int)length].ToArray());
            data = data[(int)length..];
        }

        return data.IsEmpty;
    }

    /// <summary>
    /// A received <c>tx_abort</c>'s data as log text. BOLT 2: "if <c>data</c> is not composed solely of printable ASCII
    /// characters (For reference: the printable character set includes byte values 32 through 126, inclusive): SHOULD
    /// NOT print out <c>data</c> verbatim". Non-printable data is shown as hex.
    /// </summary>
    public static string DescribeAbortData(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b is < 32 or > 126)
                return "0x" + Convert.ToHexStringLower(data);
        }

        return System.Text.Encoding.ASCII.GetString(data);
    }

    /// <summary>
    /// The DER encoding rules of BIP 66 (<c>IsValidSignatureEncoding</c>) for a signature followed by its sighash byte.
    /// </summary>
    public static bool IsDerSignature(ReadOnlySpan<byte> sig)
    {
        if (sig.Length is < 9 or > 73 || sig[0] != 0x30 || sig[1] != sig.Length - 3)
            return false;

        var lenR = sig[3];
        if (5 + lenR >= sig.Length)
            return false;

        var lenS = sig[5 + lenR];
        if (lenR + lenS + 7 != sig.Length)
            return false;

        if (sig[2] != 0x02 || lenR == 0 || (sig[4] & 0x80) != 0 || (lenR > 1 && sig[4] == 0x00 && (sig[5] & 0x80) == 0))
            return false;

        return sig[lenR + 4] == 0x02 && lenS != 0 && (sig[lenR + 6] & 0x80) == 0
               && (lenS <= 1 || sig[lenR + 6] != 0x00 || (sig[lenR + 7] & 0x80) != 0);
    }

    private static bool IsP2Wpkh(ReadOnlySpan<byte> script) =>
        script.Length == 22 && script[0] == 0x00 && script[1] == 0x14;

    private static bool IsP2Wsh(ReadOnlySpan<byte> script) =>
        script.Length == 34 && script[0] == 0x00 && script[1] == 0x20;

    private static bool IsP2Tr(ReadOnlySpan<byte> script) =>
        script.Length == 34 && script[0] == 0x51 && script[1] == 0x20;

    private static int CompactSizeLength(ulong value) => value switch
    {
        < 0xfd => 1,
        <= 0xffff => 3,
        <= 0xffffffff => 5,
        _ => 9
    };

    private static bool TryReadCompactSize(ref ReadOnlySpan<byte> data, out ulong value)
    {
        value = 0;
        if (data.IsEmpty)
            return false;

        var prefix = data[0];
        var size = prefix switch
        {
            0xfd => 2,
            0xfe => 4,
            0xff => 8,
            _ => 0
        };

        if (size == 0)
        {
            value = prefix;
            data = data[1..];
            return true;
        }

        if (data.Length < 1 + size)
            return false;

        for (var i = size; i >= 1; i--)
            value = (value << 8) | data[i];

        data = data[(1 + size)..];
        return true;
    }

    #endregion
}