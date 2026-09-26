namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;

/// <summary>
/// A sweep, claim or penalty transaction before its witnesses (built by <c>SweepTransactionBuilder</c> or
/// <c>PenaltyTransactionBuilder</c> in Infrastructure.Bitcoin): one output to <see cref="DestinationScript"/>, every
/// input BIP 125 replaceable.
/// </summary>
/// <param name="Transaction">The unsigned transaction (txid and raw bytes without witnesses).</param>
/// <param name="Inputs">The spent outputs, in input order.</param>
/// <param name="DestinationScript">The scriptPubKey of the single output (a wallet address).</param>
/// <param name="OutputSat">The output amount.</param>
/// <param name="FeeSat">The fee: the inputs' total minus <see cref="OutputSat"/>.</param>
/// <param name="EstimatedWeight">The weight with worst-case 73-byte signatures; the signed transaction is never
/// heavier.</param>
public sealed record UnsignedSweepTransaction(
    SignedTransaction Transaction,
    IReadOnlyList<SweepInput> Inputs,
    byte[] DestinationScript,
    ulong OutputSat,
    ulong FeeSat,
    long EstimatedWeight)
{
    /// <summary>What the signer needs to sign input <paramref name="inputIndex"/>.</summary>
    public SweepSigningContext GetSigningContext(int inputIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(inputIndex, Inputs.Count);

        var input = Inputs[inputIndex];
        return new SweepSigningContext(Transaction.RawTxBytes, inputIndex, input.WitnessScript, input.AmountSat,
                                       input.KeyKind, input.PerCommitmentPoint, input.PerCommitmentSecret);
    }
}