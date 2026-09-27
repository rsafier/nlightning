namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Enums;
using Models;
using Money;

/// <summary>
/// The fee accounting of a collaboratively built transaction (BOLT 3 "Calculating Fees for Collaborative
/// Transactions", IT-S-03, and BOLT 2 tx_complete, IT-R-04): "fees are paid by each party to the transaction, at a
/// <c>feerate</c> determined during the initiation, with the initiator covering the fees for the common transaction
/// fields". Pure and table-testable.
/// </summary>
/// <remarks>
/// <para>Weights follow BOLT 3 "Expected Weight of the Funding Transaction (v2 Channel Establishment)" and the fee
/// calculation of Appendix F ("Funding Transaction Construction"): the common fields are
/// <c>(version 4 + input count 1 + output count 1 + locktime 4) x 4 + marker/flag 2 = 42</c> (input and output counts
/// are one byte each because both are capped at 252); an input is <c>(32 + 4 + 1 + 4) x 4 = 164</c> plus its witness;
/// an output is <c>(8 + script length prefix + script) x 4</c>. The shared input and the shared funding output are the
/// initiator's (BOLT 2 splicing: "The splice initiator is responsible for adding that input to the transaction, and pay
/// the fees for its weight"; the same for the new funding output; BOLT 2 dual funding: the opener adds and pays for the
/// funding output).</para>
/// <para>Two witness estimates exist. The <b>minimum</b> is what a receiver may assume the peer's input costs at
/// <c>tx_complete</c>, before any witness is seen: BOLT 3's "minimum witness weight" of 107 for every input, except a
/// P2TR key-path spend, whose smallest standard witness (a 64-byte <c>SIGHASH_DEFAULT</c> signature) weighs 66: charging
/// such an input 107 would refuse peers (LDK) that budget 66. The <b>maximum</b> is what we budget for our own inputs
/// (BOLT 2 tx_signatures rationale: "It is the responsibility of the sending peer to correctly account for the required
/// fee"): 109 for P2WPKH (a 72-byte DER signature, its sighash byte and a 33-byte key) and 67 for a P2TR key path with
/// an explicit <c>SIGHASH_ALL</c>; P2WSH depends on its script and has no generic maximum.</para>
/// <para>Fees are <c>weight x feerate / 1000</c> in satoshis. <see cref="FeeForWeight"/> rounds up (what Appendix F
/// charges: 609 wu at 253 sat/kw is 155 sat, 395 wu is 100 sat) and is what we pay; <see cref="MinimumFeeForWeight"/>
/// rounds down and is what the peer must pay at least (IT-R-04 "based on the <c>minimum fee</c>"), so a peer that
/// rounds down (Eclair's <c>weight2fee</c>) is never refused over one satoshi.</para>
/// </remarks>
public static class CollaborativeFeeCalculator
{
    /// <summary>
    /// The weight of the common fields: version, input count, output count and locktime (10 bytes x 4) plus the segwit
    /// marker and flag (2), paid by the initiator.
    /// </summary>
    public const int CommonFieldsWeight = (4 + 1 + 1 + 4) * 4 + 2;

    /// <summary>The non-witness weight of an input: outpoint (36), empty scriptSig length (1) and sequence (4), x 4.</summary>
    public const int InputBaseWeight = (32 + 4 + 1 + 4) * 4;

    /// <summary>BOLT 3 Appendix F: the "minimum witness weight" an input is charged before its witness is known.</summary>
    public const int MinimumWitnessWeight = 107;

    /// <summary>
    /// The smallest standard P2TR key-path witness: item count (1), signature length (1) and a 64-byte
    /// <c>SIGHASH_DEFAULT</c> Schnorr signature.
    /// </summary>
    public const int P2TrKeyPathMinimumWitnessWeight = 1 + 1 + 64;

    /// <summary>
    /// A P2TR key-path witness with an explicit <c>SIGHASH_ALL</c> (BOLT 2 tx_signatures: "MUST use the
    /// <c>SIGHASH_ALL</c> (0x01) flag on each signature"): 1 + 1 + 65.
    /// </summary>
    public const int P2TrKeyPathMaximumWitnessWeight = 1 + 1 + 65;

    /// <summary>
    /// The largest standard P2WPKH witness: item count (1), a 72-byte DER signature with its sighash byte (1 + 73) and a
    /// compressed key (1 + 33).
    /// </summary>
    public const int P2WpkhMaximumWitnessWeight = 1 + 1 + 73 + 1 + 33;

    #region Weights

    /// <summary>The weight of an output: value (8), script length prefix and script, x 4.</summary>
    public static long OutputWeight(BitcoinScript script)
    {
        var length = ((byte[])script).Length;
        return (8L + CompactSizeLength((ulong)length) + length) * 4;
    }

    /// <summary>
    /// The witness weight a receiver charges an input of the peer before its witness is known: 66 for a P2TR key-path
    /// spend, BOLT 3's 107 for anything else (P2WPKH, P2WSH, other witness versions).
    /// </summary>
    /// <param name="spentScript">The scriptPubKey the input spends.</param>
    public static int GetMinimumWitnessWeight(BitcoinScript spentScript) =>
        IsP2Tr(spentScript) ? P2TrKeyPathMinimumWitnessWeight : MinimumWitnessWeight;

    /// <summary>
    /// The witness weight we budget for an input we sign: 109 for P2WPKH, 67 for a P2TR key path; null when the script
    /// alone does not bound it (P2WSH, other versions): the contributor must know its witness.
    /// </summary>
    /// <param name="spentScript">The scriptPubKey the input spends.</param>
    public static int? GetMaximumWitnessWeight(BitcoinScript spentScript)
    {
        if (IsP2Wpkh(spentScript))
            return P2WpkhMaximumWitnessWeight;

        return IsP2Tr(spentScript) ? P2TrKeyPathMaximumWitnessWeight : null;
    }

    /// <summary>
    /// The weight we budget for a wallet input of ours (<see cref="ContributedInput.InputWeight"/>):
    /// <see cref="InputBaseWeight"/> plus <see cref="GetMaximumWitnessWeight"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The script does not bound its witness (P2WSH or another version).</exception>
    public static int EstimateLocalInputWeight(BitcoinScript spentScript) =>
        InputBaseWeight + (GetMaximumWitnessWeight(spentScript)
                           ?? throw new ArgumentException("The witness weight of this script type is not known.",
                                                          nameof(spentScript)));

    /// <summary>
    /// The weight a receiver charges one input at <c>tx_complete</c>: the shared input's
    /// <see cref="SharedFundingInput.InputWeight"/> (the whole signed input), otherwise
    /// <see cref="InputBaseWeight"/> plus <see cref="GetMinimumWitnessWeight"/>.
    /// </summary>
    public static long GetMinimumInputWeight(InteractiveTxInput input, SharedFundingSpec? sharedFunding)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.IsShared && sharedFunding?.SharedInput is { } sharedInput)
            return sharedInput.InputWeight;

        return InputBaseWeight + GetMinimumWitnessWeight(input.ScriptPubKey);
    }

    /// <summary>
    /// The weight one side pays for (IT-S-03): its own inputs and outputs, and, when it is the initiator, the common
    /// fields and the shared input and output (whoever added them). Inputs use the minimum witness estimate.
    /// </summary>
    /// <param name="inputs">Every input currently added.</param>
    /// <param name="outputs">Every output currently added.</param>
    /// <param name="party">The side whose weight is wanted.</param>
    /// <param name="partyIsInitiator">Whether <paramref name="party"/> is the initiator.</param>
    /// <param name="sharedFunding">The shared input/output, or null.</param>
    /// <param name="includeCommonFields">False to leave the common fields out (to tell "does not cover the common
    /// fields" apart from "does not pay for its own inputs and outputs").</param>
    public static long GetContributionWeight(IReadOnlyList<InteractiveTxInput> inputs,
                                             IReadOnlyList<InteractiveTxOutput> outputs, InteractiveTxParty party,
                                             bool partyIsInitiator, SharedFundingSpec? sharedFunding,
                                             bool includeCommonFields = true)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var weight = partyIsInitiator && includeCommonFields ? CommonFieldsWeight : 0L;

        foreach (var input in inputs)
        {
            if (input.IsShared ? partyIsInitiator : input.AddedBy == party)
                weight += GetMinimumInputWeight(input, sharedFunding);
        }

        foreach (var output in outputs)
        {
            if (output.IsShared ? partyIsInitiator : output.AddedBy == party)
                weight += OutputWeight(output.ScriptPubKey);
        }

        return weight;
    }

    /// <summary>
    /// The estimated weight of the signed transaction (the IT-R-04 check against 400,000): common fields, every input
    /// with the minimum witness estimate (the shared input with its own weight) and every output.
    /// </summary>
    public static long EstimateTransactionWeight(IReadOnlyList<InteractiveTxInput> inputs,
                                                 IReadOnlyList<InteractiveTxOutput> outputs,
                                                 SharedFundingSpec? sharedFunding)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var weight = (long)CommonFieldsWeight;
        foreach (var input in inputs)
            weight += GetMinimumInputWeight(input, sharedFunding);

        foreach (var output in outputs)
            weight += OutputWeight(output.ScriptPubKey);

        return weight;
    }

    /// <summary>
    /// The weight of our own contribution as we budget it: our inputs at <see cref="ContributedInput.InputWeight"/> and
    /// our outputs, and as initiator the common fields, the shared input (<see cref="SharedFundingInput.InputWeight"/>)
    /// and the shared output.
    /// </summary>
    public static long GetLocalContributionWeight(InteractiveTxContribution contribution, bool isInitiator,
                                                  SharedFundingSpec? sharedFunding)
    {
        ArgumentNullException.ThrowIfNull(contribution);

        var weight = 0L;
        if (isInitiator)
        {
            weight += CommonFieldsWeight;
            if (sharedFunding is not null)
            {
                weight += OutputWeight(sharedFunding.SharedOutputScript);
                if (sharedFunding.SharedInput is { } sharedInput)
                    weight += sharedInput.InputWeight;
            }
        }

        foreach (var input in contribution.Inputs)
            weight += input.InputWeight;

        foreach (var output in contribution.Outputs)
            weight += OutputWeight(output.ScriptPubKey);

        return weight;
    }

    #endregion

    #region Fees

    /// <summary>
    /// The fee we pay for <paramref name="weight"/> at <paramref name="feeratePerKw"/>: <c>weight x feerate / 1000</c>
    /// satoshis rounded up (BOLT 3 Appendix F: 609 wu at 253 sat/kw is 155 sat).
    /// </summary>
    public static LightningMoney FeeForWeight(long weight, uint feeratePerKw)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        var product = checked((ulong)weight * feeratePerKw);
        return LightningMoney.Satoshis((product + 999) / 1000);
    }

    /// <summary>
    /// The least fee a side must pay for <paramref name="weight"/> at <paramref name="feeratePerKw"/> (IT-R-04):
    /// <c>weight x feerate / 1000</c> satoshis rounded down.
    /// </summary>
    public static LightningMoney MinimumFeeForWeight(long weight, uint feeratePerKw)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        return LightningMoney.Satoshis(checked((ulong)weight * feeratePerKw) / 1000);
    }

    /// <summary>The fee we pay for our own contribution (<see cref="GetLocalContributionWeight"/>, rounded up).</summary>
    public static LightningMoney GetLocalContributionFee(InteractiveTxContribution contribution, bool isInitiator,
                                                         SharedFundingSpec? sharedFunding, uint feeratePerKw) =>
        FeeForWeight(GetLocalContributionWeight(contribution, isInitiator, sharedFunding), feeratePerKw);

    /// <summary>
    /// What a side pays in fees, in millisatoshis: its inputs (plus its share of the shared input) minus its outputs
    /// (plus its share of the shared output). Negative when its outputs exceed its inputs.
    /// </summary>
    /// <param name="inputs">Every input currently added.</param>
    /// <param name="outputs">Every output currently added.</param>
    /// <param name="party">The side.</param>
    /// <param name="sharedFunding">The shared input/output with each side's share, or null.</param>
    public static long GetPaidFeeMsat(IReadOnlyList<InteractiveTxInput> inputs,
                                      IReadOnlyList<InteractiveTxOutput> outputs, InteractiveTxParty party,
                                      SharedFundingSpec? sharedFunding)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        long paid = 0;
        foreach (var input in inputs)
        {
            if (!input.IsShared && input.AddedBy == party)
                paid = checked(paid + (long)input.Amount.MilliSatoshi);
        }

        foreach (var output in outputs)
        {
            if (!output.IsShared && output.AddedBy == party)
                paid = checked(paid - (long)output.Amount.MilliSatoshi);
        }

        if (sharedFunding is not null)
        {
            var local = party == InteractiveTxParty.Local;
            if (sharedFunding.SharedInput is not null)
                paid = checked(paid + (long)(local ? sharedFunding.LocalInputShare : sharedFunding.RemoteInputShare)
                                                .MilliSatoshi);

            paid = checked(paid - (long)(local ? sharedFunding.LocalOutputShare : sharedFunding.RemoteOutputShare)
                                        .MilliSatoshi);
        }

        return paid;
    }

    #endregion

    private static bool IsP2Wpkh(BitcoinScript script)
    {
        byte[] bytes = script;
        return bytes.Length == 22 && bytes[0] == 0x00 && bytes[1] == 0x14;
    }

    private static bool IsP2Tr(BitcoinScript script)
    {
        byte[] bytes = script;
        return bytes.Length == 34 && bytes[0] == 0x51 && bytes[1] == 0x20;
    }

    private static int CompactSizeLength(ulong value) => value switch
    {
        < 0xfd => 1,
        <= 0xffff => 3,
        <= 0xffffffff => 5,
        _ => 9
    };
}