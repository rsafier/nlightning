namespace NLightning.Domain.Channels.Closing;

/// <summary>
/// The fee maths of a legacy mutual close: the expected weight of the fully signed closing transaction with both
/// outputs, and the fee at a feerate (B2-CLS-02: "its estimate of cost of inclusion in a block").
/// </summary>
public static class ClosingFeeCalculator
{
    /// <summary>
    /// Non-witness bytes that do not depend on the outputs: version (4), input count (1), the input (outpoint 36,
    /// empty script length 1, sequence 4), output count (1), locktime (4).
    /// </summary>
    private const ulong BaseNonWitnessBytes = 4 + 1 + 36 + 1 + 4 + 1 + 4;

    /// <summary>
    /// Witness bytes: marker and flag (2), item count (1), the empty item (1), two signatures of at most 72 bytes plus
    /// the sighash byte, each with its length (2 x 74), and the 71-byte 2-of-2 funding script with its length (72).
    /// </summary>
    private const ulong WitnessBytes = 2 + 1 + 1 + 2 * 74 + 72;

    /// <summary>
    /// The weight of the signed closing transaction paying both <paramref name="localScriptLength"/> and
    /// <paramref name="remoteScriptLength"/> byte scripts (an upper bound: signatures counted at their maximum size).
    /// </summary>
    public static ulong EstimateWeight(int localScriptLength, int remoteScriptLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(localScriptLength);
        ArgumentOutOfRangeException.ThrowIfNegative(remoteScriptLength);

        var outputs = OutputBytes(localScriptLength) + OutputBytes(remoteScriptLength);
        return (BaseNonWitnessBytes + outputs) * 4 + WitnessBytes;
    }

    /// <summary>The fee in satoshis at <paramref name="feeratePerKw"/> for <paramref name="weight"/> (rounded down).</summary>
    public static ulong FeeSat(ulong feeratePerKw, ulong weight) => checked(feeratePerKw * weight) / 1000;

    /// <summary>Amount (8), script length as a Bitcoin varint, script.</summary>
    private static ulong OutputBytes(int scriptLength) =>
        8 + (scriptLength < 0xfd ? 1UL : 3UL) + (ulong)scriptLength;
}