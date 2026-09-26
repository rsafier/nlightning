namespace NLightning.Domain.Onchain.Fees;

using Enums;
using Models;

/// <summary>
/// Weights of sweep, claim and penalty transactions (BOLT 5 §Penalty Transactions Weight Calculation, BOLT 3 Appendix
/// A), computed from the real witness shape with worst-case 73-byte signatures (72-byte DER + the sighash byte), so an
/// estimate is never below the signed transaction's weight.
/// </summary>
public static class SweepWeights
{
    /// <summary>Largest DER signature plus the sighash byte.</summary>
    public const int MaxSignatureLength = 73;

    /// <summary>BOLT 5 <c>to_local_penalty_witness</c>: an upper bound (the spec assumes an 83-byte
    /// <c>to_local_script</c> with an 8-byte delay; real scripts are 77-78 bytes, see
    /// <see cref="EstimateWitnessSize"/>).</summary>
    public const int ToLocalPenaltyWitness = 160;

    /// <summary>BOLT 5 <c>offered_htlc_penalty_witness</c> (exact for the 133-byte offered HTLC script).</summary>
    public const int OfferedHtlcPenaltyWitness = 243;

    /// <summary>BOLT 5 <c>accepted_htlc_penalty_witness</c> (exact for the 138-byte received HTLC script).</summary>
    public const int AcceptedHtlcPenaltyWitness = 249;

    /// <summary>Non-witness weight of one input: <c>4 * (32 + 4 + 1 + 4)</c>.</summary>
    public const int InputNonWitnessWeight = 164;

    /// <summary>BOLT 5 <c>to_local_penalty_input_weight</c>.</summary>
    public const int ToLocalPenaltyInputWeight = InputNonWitnessWeight + ToLocalPenaltyWitness;

    /// <summary>BOLT 5 <c>offered_htlc_penalty_input_weight</c>.</summary>
    public const int OfferedHtlcPenaltyInputWeight = InputNonWitnessWeight + OfferedHtlcPenaltyWitness;

    /// <summary>BOLT 5 <c>accepted_htlc_penalty_input_weight</c>.</summary>
    public const int AcceptedHtlcPenaltyInputWeight = InputNonWitnessWeight + AcceptedHtlcPenaltyWitness;

    /// <summary>BOLT 5: the non-witness rest of a penalty transaction with one P2WSH output, <c>4 * 53</c>, plus the
    /// 2-byte segwit marker and flag.</summary>
    public const int PenaltyTransactionOverheadWeight = 4 * 53 + 2;

    /// <summary>BOLT 5: an extra <c>to_remote</c> P2WPKH input on a penalty, <c>108 + 164</c>.</summary>
    public const int ToRemoteInputWeight = 272;

    /// <summary>
    /// The witness size (= its weight) of <paramref name="input"/> with a <see cref="MaxSignatureLength"/> signature:
    /// the item count, then each item with its length prefix.
    /// </summary>
    public static int EstimateWitnessSize(SweepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var scriptItem = input.WitnessScript is null ? 0 : ItemSize(input.WitnessScript.Length);
        var signature = ItemSize(MaxSignatureLength);
        return input.SpendKind switch
        {
            // <sig> <> <script>
            SweepSpendKind.DelayedOutput or SweepSpendKind.HtlcTimeoutClaim => 1 + signature + ItemSize(0)
                                                                          + scriptItem,

            // P2WPKH: <sig> <pubkey>; anchors P2WSH: <sig> <script>
            SweepSpendKind.PaymentToRemote => input.WitnessScript is null
                                                  ? 1 + signature + ItemSize(33)
                                                  : 1 + signature + scriptItem,

            // <sig> <preimage> <script>
            SweepSpendKind.HtlcPreimageClaim => 1 + signature + ItemSize(32) + scriptItem,

            // <revocation_sig> 1 <script>
            SweepSpendKind.RevokedDelayedOutput => 1 + signature + ItemSize(1) + scriptItem,

            // <revocation_sig> <revocationpubkey> <script>
            SweepSpendKind.RevokedHtlc => 1 + signature + ItemSize(33) + scriptItem,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.SpendKind, "Unknown spend kind")
        };
    }

    /// <summary>
    /// The weight of a version-2 segwit transaction spending <paramref name="inputs"/> into outputs with the given
    /// scriptPubKey lengths, with <see cref="MaxSignatureLength"/> signatures.
    /// </summary>
    public static long EstimateTransactionWeight(IReadOnlyCollection<SweepInput> inputs,
                                                 IReadOnlyCollection<int> outputScriptLengths)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputScriptLengths);

        // version + input count + inputs + output count + outputs + locktime
        var nonWitness = 4 + CompactSizeLength(inputs.Count) + 41L * inputs.Count
                        + CompactSizeLength(outputScriptLengths.Count)
                        + outputScriptLengths.Sum(l => 8L + CompactSizeLength(l) + l) + 4;
        var witness = inputs.Sum(i => (long)EstimateWitnessSize(i));

        // Segwit marker and flag count once each
        return 4 * nonWitness + 2 + witness;
    }

    /// <summary>The fee of <paramref name="weight"/> at <paramref name="feeratePerKw"/> (rounded down, as BOLT 3).</summary>
    public static ulong FeeSat(uint feeratePerKw, long weight) =>
        weight <= 0 ? 0 : (ulong)feeratePerKw * (ulong)weight / 1000;

    /// <summary>The virtual size of <paramref name="weight"/> (rounded up).</summary>
    public static long VirtualSize(long weight) => (weight + 3) / 4;

    private static int ItemSize(int length) => CompactSizeLength(length) + length;

    private static int CompactSizeLength(int value) => value switch
    {
        < 0xfd => 1,
        <= 0xffff => 3,
        _ => 5
    };
}