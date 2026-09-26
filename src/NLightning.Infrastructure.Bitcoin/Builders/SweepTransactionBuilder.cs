using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Interfaces;

/// <summary>
/// Builds sweep and claim transactions from <see cref="SweepInput"/>s (BOLT 5 plan O3-T1): version 2, one output,
/// every input BIP 125 replaceable (<c>nSequence</c> = the CSV delay where the script needs one, else
/// <see cref="SweepFeePolicy.RbfSequence"/>), <c>nLockTime</c> = the largest <c>cltv_expiry</c> of the timeout claims.
/// Witnesses follow BOLT 3 §Commitment Transaction Outputs and BOLT 5.
/// </summary>
public class SweepTransactionBuilder : ISweepTransactionBuilder
{
    private const uint LockTimeThreshold = 500_000_000;
    private const uint SweepTransactionVersion = 2;

    private readonly Network _network;

    public SweepTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    /// <inheritdoc />
    public UnsignedSweepTransaction Build(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                          uint feeratePerKw, uint lockTime = 0)
    {
        ValidateInputs(inputs);
        ArgumentNullException.ThrowIfNull(destinationScript);

        var weight = SweepWeights.EstimateTransactionWeight(inputs, [destinationScript.Length]);
        return BuildCore(inputs, destinationScript, SweepWeights.FeeSat(feeratePerKw, weight), weight, lockTime);
    }

    /// <inheritdoc />
    public UnsignedSweepTransaction BuildWithFee(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                                 ulong feeSat, uint lockTime = 0)
    {
        ValidateInputs(inputs);
        ArgumentNullException.ThrowIfNull(destinationScript);

        var weight = SweepWeights.EstimateTransactionWeight(inputs, [destinationScript.Length]);
        return BuildCore(inputs, destinationScript, feeSat, weight, lockTime);
    }

    /// <inheritdoc />
    public SignedTransaction AddWitnesses(UnsignedSweepTransaction transaction,
                                          IReadOnlyList<CompactSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(signatures);
        if (signatures.Count != transaction.Inputs.Count)
            throw new ArgumentException(
                $"Expected {transaction.Inputs.Count} signatures but got {signatures.Count}", nameof(signatures));

        var tx = Transaction.Load(transaction.Transaction.RawTxBytes, _network);
        for (var i = 0; i < transaction.Inputs.Count; i++)
        {
            if (!ECDSASignature.TryParseFromCompact(signatures[i], out var ecdsa))
                throw new ArgumentException($"Signature {i} is not a valid compact signature", nameof(signatures));

            var signature = new TransactionSignature(ecdsa, SigHash.All).ToBytes();
            tx.Inputs[i].WitScript = new WitScript(CreateWitness(transaction.Inputs[i], signature));
        }

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes(), signatures.ToList());
    }

    /// <inheritdoc />
    public SignedTransaction Sign(UnsignedSweepTransaction transaction, ILightningSigner signer, ChannelId channelId)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(signer);

        var signatures = new List<CompactSignature>(transaction.Inputs.Count);
        for (var i = 0; i < transaction.Inputs.Count; i++)
            signatures.Add(signer.SignSweepInput(channelId, transaction.GetSigningContext(i)));

        return AddWitnesses(transaction, signatures);
    }

    /// <summary>
    /// The witness stack of one input (without the witness script for a P2WPKH <c>to_remote</c>).
    /// </summary>
    internal static byte[][] CreateWitness(SweepInput input, byte[] signature)
    {
        return input.SpendKind switch
        {
            SweepSpendKind.DelayedOutput or SweepSpendKind.HtlcTimeoutClaim => [signature, [], input.WitnessScript!],
            SweepSpendKind.PaymentToRemote => input.WitnessScript is null
                                                  ? [signature, (byte[])input.WitnessPubKey!.Value]
                                                  : [signature, input.WitnessScript],
            SweepSpendKind.HtlcPreimageClaim => [signature, input.Preimage!, input.WitnessScript!],
            SweepSpendKind.RevokedDelayedOutput => [signature, [1], input.WitnessScript!],
            SweepSpendKind.RevokedHtlc => [signature, (byte[])input.WitnessPubKey!.Value, input.WitnessScript!],
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.SpendKind, "Unknown spend kind")
        };
    }

    private UnsignedSweepTransaction BuildCore(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                               ulong feeSat, long weight, uint lockTime)
    {
        var total = inputs.Aggregate(0UL, (sum, i) => checked(sum + i.AmountSat));
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destinationScript);
        if (feeSat >= total || total - feeSat < dust)
            throw new ArgumentException(
                $"The inputs ({total} sat) do not cover the fee ({feeSat} sat) and a {dust} sat output",
                nameof(inputs));

        var effectiveLockTime = inputs.Where(i => i.SpendKind == SweepSpendKind.HtlcTimeoutClaim)
                                      .Select(i => i.CltvExpiry)
                                      .Append(lockTime)
                                      .Max();
        if (effectiveLockTime >= LockTimeThreshold)
            throw new ArgumentException("The lock time must be a block height", nameof(lockTime));

        var tx = Transaction.Create(_network);
        tx.Version = SweepTransactionVersion;
        tx.LockTime = new LockTime(effectiveLockTime);
        foreach (var input in inputs)
        {
            var sequence = input.CsvDelay > 0 ? input.CsvDelay : SweepFeePolicy.RbfSequence;
            tx.Inputs.Add(new OutPoint(new uint256((byte[])input.TxId), input.Vout), null, null,
                          new Sequence(sequence));
        }

        var outputSat = total - feeSat;
        tx.Outputs.Add(new TxOut(Money.Satoshis(outputSat), new Script(destinationScript)));

        return new UnsignedSweepTransaction(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()), inputs.ToList(),
                                           destinationScript, outputSat, feeSat, weight);
    }

    private static void ValidateInputs(IReadOnlyList<SweepInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            throw new ArgumentException("A sweep needs at least one input", nameof(inputs));

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i] ?? throw new ArgumentException($"Input {i} is null", nameof(inputs));
            var missing = input.SpendKind switch
            {
                SweepSpendKind.DelayedOutput when input.WitnessScript is null => "a witness script",
                SweepSpendKind.DelayedOutput when input.CsvDelay == 0 => "its CSV delay",
                SweepSpendKind.DelayedOutput when input.PerCommitmentPoint is null => "our per-commitment point",
                SweepSpendKind.PaymentToRemote when input.WitnessScript is null && input.WitnessPubKey is null =>
                    "our payment basepoint",
                SweepSpendKind.PaymentToRemote when input.WitnessScript is not null && input.CsvDelay == 0 =>
                    "the anchor CSV delay of 1",
                SweepSpendKind.HtlcTimeoutClaim when input.WitnessScript is null => "a witness script",
                SweepSpendKind.HtlcTimeoutClaim when input.CltvExpiry == 0 => "its cltv_expiry",
                SweepSpendKind.HtlcTimeoutClaim or SweepSpendKind.HtlcPreimageClaim
                    when input.PerCommitmentPoint is null => "the peer's per-commitment point",
                SweepSpendKind.HtlcPreimageClaim when input.WitnessScript is null => "a witness script",
                SweepSpendKind.HtlcPreimageClaim when input.Preimage is not { Length: CryptoConstants.Sha256HashLen } =>
                    "the 32-byte preimage",
                SweepSpendKind.RevokedDelayedOutput or SweepSpendKind.RevokedHtlc when input.WitnessScript is null =>
                    "a witness script",
                SweepSpendKind.RevokedDelayedOutput or SweepSpendKind.RevokedHtlc
                    when input.PerCommitmentSecret is null => "the peer's per-commitment secret",
                SweepSpendKind.RevokedHtlc when input.WitnessPubKey is null => "the revocation pubkey",
                SweepSpendKind.RevokedHtlc when !ScriptHasHash160(input.WitnessScript!, input.WitnessPubKey!.Value) =>
                    "a revocation pubkey whose HASH160 is in the script",
                _ => null
            };

            if (!Enum.IsDefined(input.SpendKind))
                throw new ArgumentException($"Input {i} has an unknown spend kind {input.SpendKind}", nameof(inputs));

            if (missing is not null)
                throw new ArgumentException($"Input {i} ({input.SpendKind}) needs {missing}", nameof(inputs));
        }
    }

    private static bool ScriptHasHash160(byte[] witnessScript, CompactPubKey pubKey)
    {
        var hash = Hashes.Hash160((byte[])pubKey).ToBytes();
        return new Script(witnessScript).ToOps().Any(op => op.PushData is { } data && data.AsSpan().SequenceEqual(hash));
    }
}