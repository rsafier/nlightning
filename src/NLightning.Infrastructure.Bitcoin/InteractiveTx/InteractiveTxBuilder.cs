using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// Builds the transaction a completed interactive-tx negotiation describes and puts the witnesses on it (BOLT 2
/// "Interactive Transaction Construction", splicing plan IT2-T2), proven byte-exact against BOLT 3 Appendix G.
/// </summary>
/// <remarks>
/// <para><see cref="Build"/>: version 2, the negotiated locktime, inputs and outputs each sorted by ascending
/// <c>serial_id</c> (BOLT 2: "Inputs in the constructed transaction MUST be sorted by serial_id", the same for
/// outputs), each input with its own <c>nSequence</c>; a <c>serial_id</c> used twice (inputs and outputs together, as
/// the receiving rules count them), no input, no output or more than one shared output is an
/// <see cref="ArgumentException"/>. The estimated weight is the exact size without witnesses x 4, the segwit marker and
/// flag, and per input a witness estimate from its spent script: P2WPKH 108, P2TR key path 67 (explicit sighash byte),
/// the shared 2-of-2 funding input 222 (BOLT 3), anything else BOLT 3's minimum witness weight of 107.</para>
/// <para><see cref="Finalize"/> takes one BIP 141 witness stack serialization per input, keyed by <c>serial_id</c>
/// (as <c>tx_signatures</c> carries them), refuses a missing, unknown or unparsable one, and checks the txid did not
/// change. It checks no signature: the driver verifies the peer's witnesses against the spent outputs.</para>
/// </remarks>
public sealed class InteractiveTxBuilder : IInteractiveTxBuilder
{
    /// <summary>The transaction version of every interactive-tx construction (BOLT 3 Appendix G).</summary>
    public const uint TransactionVersion = 2;

    /// <summary>The segwit marker and flag, in weight units.</summary>
    internal const int SegwitMarkerAndFlagWeight = 2;

    /// <summary>A P2WPKH witness: item count, a 72-byte signature with its sighash byte and the 33-byte key, each with
    /// its length byte.</summary>
    internal const int P2WpkhWitnessWeight = 108;

    /// <summary>A P2TR key path witness: item count and a 65-byte Schnorr signature with its length byte.</summary>
    internal const int P2TrKeyPathWitnessWeight = 67;

    /// <summary>The 2-of-2 funding input's witness (BOLT 3 "Expected Weight of the Commitment Transaction").</summary>
    internal const int FundingWitnessWeight = 222;

    /// <summary>BOLT 3's minimum witness weight of an input ("Calculating Fees for Collaborative Transaction
    /// Construction").</summary>
    internal const int MinimumWitnessWeight = 107;

    /// <inheritdoc />
    public ConstructedInteractiveTx Build(uint locktime, IReadOnlyList<InteractiveTxInput> inputs,
                                          IReadOnlyList<InteractiveTxOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (inputs.Count == 0)
            throw new ArgumentException("An interactive transaction needs at least one input", nameof(inputs));
        if (outputs.Count == 0)
            throw new ArgumentException("An interactive transaction needs at least one output", nameof(outputs));

        var serialIds = new HashSet<ulong>();
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input, nameof(inputs));
            if (!serialIds.Add(input.SerialId))
                throw new ArgumentException($"serial_id {input.SerialId} is used more than once", nameof(inputs));
        }

        foreach (var output in outputs)
        {
            ArgumentNullException.ThrowIfNull(output, nameof(outputs));
            if (!serialIds.Add(output.SerialId))
                throw new ArgumentException($"serial_id {output.SerialId} is used more than once", nameof(outputs));
        }

        if (outputs.Count(o => o.IsShared) > 1)
            throw new ArgumentException("An interactive transaction has at most one shared output", nameof(outputs));

        var sortedInputs = inputs.OrderBy(i => i.SerialId).ToList();
        var sortedOutputs = outputs.OrderBy(o => o.SerialId).ToList();

        var tx = Network.Main.CreateTransaction();
        tx.Version = TransactionVersion;
        tx.LockTime = new LockTime(locktime);
        foreach (var input in sortedInputs)
        {
            if ((byte[])input.PrevTxId is not { Length: 32 })
                throw new ArgumentException($"Input {input.SerialId} has no 32-byte previous txid", nameof(inputs));

            tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])input.PrevTxId), input.PrevTxVout))
            {
                Sequence = new Sequence(input.Sequence)
            });
        }

        uint? sharedOutputIndex = null;
        for (var i = 0; i < sortedOutputs.Count; i++)
        {
            var output = sortedOutputs[i];
            if ((byte[])output.ScriptPubKey is null)
                throw new ArgumentException($"Output {output.SerialId} has no script", nameof(outputs));
            if (output.Amount.MilliSatoshi % 1_000 != 0)
                throw new ArgumentException($"Output {output.SerialId} is not a whole number of satoshis",
                                            nameof(outputs));

            tx.Outputs.Add(new TxOut(Money.Satoshis(output.Amount.Satoshi), new Script((byte[])output.ScriptPubKey)));
            if (output.IsShared)
                sharedOutputIndex = (uint)i;
        }

        var unsigned = tx.ToBytes();
        var weight = (long)tx.GetSerializedSize(TransactionOptions.None) * 4 + SegwitMarkerAndFlagWeight
                   + sortedInputs.Sum(i => (long)EstimateWitnessWeight(i));

        return new ConstructedInteractiveTx(new TxId(tx.GetHash().ToBytes()), unsigned, locktime, sortedInputs,
                                            sortedOutputs, weight, sharedOutputIndex);
    }

    /// <inheritdoc />
    public SignedTransaction Finalize(ConstructedInteractiveTx transaction,
                                      IReadOnlyDictionary<ulong, Witness> witnessesBySerialId)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(witnessesBySerialId);

        if (!InteractiveTxTransactionReader.TryReadTransaction(transaction.UnsignedTx, out var tx) || tx is null)
            throw new ArgumentException("The constructed transaction does not parse", nameof(transaction));
        if (tx.Inputs.Count != transaction.Inputs.Count)
            throw new ArgumentException("The constructed transaction does not match its inputs", nameof(transaction));

        var known = transaction.Inputs.Select(i => i.SerialId).ToHashSet();
        foreach (var serialId in witnessesBySerialId.Keys)
        {
            if (!known.Contains(serialId))
                throw new ArgumentException($"serial_id {serialId} is not an input of the transaction",
                                            nameof(witnessesBySerialId));
        }

        for (var i = 0; i < transaction.Inputs.Count; i++)
        {
            var serialId = transaction.Inputs[i].SerialId;
            if (!witnessesBySerialId.TryGetValue(serialId, out var witness) || (byte[])witness is null)
                throw new ArgumentException($"Input {serialId} has no witness", nameof(witnessesBySerialId));

            if (!InteractiveTxTransactionReader.TryReadWitness((byte[])witness, out var witScript)
             || witScript is null || witScript.PushCount == 0)
                throw new ArgumentException($"The witness of input {serialId} does not parse",
                                            nameof(witnessesBySerialId));

            tx.Inputs[i].WitScript = witScript;
        }

        var txId = tx.GetHash().ToBytes();
        if (!txId.AsSpan().SequenceEqual((byte[])transaction.TxId))
            throw new ArgumentException("The witnesses changed the transaction's txid", nameof(witnessesBySerialId));

        return new SignedTransaction(new TxId(txId), tx.ToBytes());
    }

    /// <summary>The estimated witness weight of <paramref name="input"/> once signed.</summary>
    internal static int EstimateWitnessWeight(InteractiveTxInput input)
    {
        var script = ((byte[]?)input.ScriptPubKey ?? []).AsSpan();
        if (script.Length == 22 && script[0] == 0x00 && script[1] == 0x14)
            return P2WpkhWitnessWeight;
        if (script.Length == 34 && script[0] == 0x51 && script[1] == 0x20)
            return P2TrKeyPathWitnessWeight;
        if (input.IsShared)
            return FundingWitnessWeight;

        return MinimumWitnessWeight;
    }
}