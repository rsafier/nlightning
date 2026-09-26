using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Interfaces;
using Outputs;

/// <summary>
/// Builds BOLT 3 HTLC-timeout/HTLC-success transactions from a Domain <see cref="HtlcTransactionModel"/>, and combines
/// the zero-fee option_anchors ones with wallet inputs that pay their fee (BOLT 5 §Generation of HTLC Transactions,
/// B5-HTX-02, plan O7-T3).
/// </summary>
public class HtlcTransactionBuilder : IHtlcTransactionBuilder
{
    /// <summary>Most fee inputs one combined transaction takes (their count stays a one-byte CompactSize).</summary>
    public const int MaxFeeInputs = 200;

    private const int MaxSignatureLength = 73;
    private const long InputNonWitnessWeight = 4 * 41;

    private readonly Network _network;

    public HtlcTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    /// <inheritdoc />
    public HtlcTransactionBuildResult Build(HtlcTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var spentOutput = CreateSpentOutput(transaction.SpentOutput, transaction.HasAnchors);

        var tx = Transaction.Create(_network);
        tx.Version = TransactionConstants.HtlcTransactionVersion;
        tx.LockTime = new LockTime(transaction.LockTime);
        tx.Inputs.Add(new OutPoint(new uint256(transaction.CommitmentTxId), transaction.CommitmentOutputIndex), null,
                      null, new Sequence(transaction.Sequence));

        var output = new HtlcResolutionOutput(transaction.OutputAmount, new PubKey(transaction.LocalDelayedPubKey),
                                              new PubKey(transaction.RevocationPubKey), transaction.ToSelfDelay);
        tx.Outputs.Add(output.ToTxOut());

        return new HtlcTransactionBuildResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                                              spentOutput.RedeemScript.ToBytes(),
                                              LightningMoney.Satoshis(spentOutput.Amount.Satoshi));
    }

    /// <inheritdoc />
    public SignedTransaction AddWitness(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                        CompactSignature remoteHtlcSignature, CompactSignature localHtlcSignature,
                                        byte[]? paymentPreimage = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(remoteHtlcSignature);
        ArgumentNullException.ThrowIfNull(localHtlcSignature);

        byte[] preimagePush;
        if (transaction.Type == HtlcTransactionType.Success)
        {
            if (paymentPreimage is not { Length: CryptoConstants.Sha256HashLen })
                throw new ArgumentException("HTLC-success needs the 32-byte payment preimage", nameof(paymentPreimage));
            preimagePush = paymentPreimage;
        }
        else
        {
            if (paymentPreimage is not null)
                throw new ArgumentException("HTLC-timeout takes no preimage", nameof(paymentPreimage));
            preimagePush = [];
        }

        // BOLT 3 / BOLT 5: with option_anchors the remote HTLC signature is SIGHASH_SINGLE|SIGHASH_ANYONECANPAY
        var remoteSigHash = transaction.HasAnchors ? SigHash.Single | SigHash.AnyoneCanPay : SigHash.All;
        var remoteSignature = ToTransactionSignature(remoteHtlcSignature, remoteSigHash, nameof(remoteHtlcSignature));
        var localSignature = ToTransactionSignature(localHtlcSignature, SigHash.All, nameof(localHtlcSignature));

        var tx = Transaction.Load(buildResult.Transaction.RawTxBytes, _network);
        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), remoteSignature.ToBytes(), localSignature.ToBytes(), preimagePush,
            (byte[])buildResult.SpentWitnessScript
        });

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes(),
                                     [remoteHtlcSignature, localHtlcSignature]);
    }

    /// <inheritdoc />
    public long EstimateAnchorBaseWeight(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                         int changeScriptLength)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentOutOfRangeException.ThrowIfNegative(changeScriptLength);

        return EstimateWeight(transaction, buildResult, [], changeScriptLength);
    }

    /// <inheritdoc />
    public AnchorHtlcTransaction AddFeeInputs(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                              IReadOnlyList<AnchorFeeInput> feeInputs, byte[] changeScript,
                                              uint feeratePerKw)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(feeInputs);
        ArgumentNullException.ThrowIfNull(changeScript);
        if (!transaction.HasAnchors)
            throw new ArgumentException("Only an option_anchors HTLC transaction (SIGHASH_SINGLE|ANYONECANPAY) can take "
                                      + "fee inputs: a SIGHASH_ALL peer signature would no longer verify",
                                        nameof(transaction));

        if (changeScript.Length == 0)
            throw new ArgumentException("The change script is empty", nameof(changeScript));

        var tx = Transaction.Load(buildResult.Transaction.RawTxBytes, _network);
        if (tx.Inputs.Count != 1 || tx.Outputs.Count != 1)
            throw new ArgumentException("Fee inputs are added once, to the HTLC transaction as built",
                                        nameof(buildResult));

        if (tx.Outputs[0].Value.Satoshi != buildResult.SpentAmount.Satoshi)
            throw new ArgumentException("An anchors HTLC transaction pays no fee of its own: its output must equal the "
                                      + "HTLC output", nameof(buildResult));

        ValidateFeeInputs(feeInputs, tx.Inputs[0].PrevOut);

        var total = feeInputs.Aggregate(0UL, (sum, i) => checked(sum + i.AmountSat));
        var withChangeWeight = EstimateWeight(transaction, buildResult, feeInputs, changeScript.Length);
        var withoutChangeWeight = EstimateWeight(transaction, buildResult, feeInputs, null);
        var withChangeFee = SweepWeights.FeeSat(feeratePerKw, withChangeWeight);
        var withoutChangeFee = SweepWeights.FeeSat(feeratePerKw, withoutChangeWeight);
        var dust = ShutdownScriptValidator.GetDustThresholdSat(changeScript);

        ulong? change;
        ulong fee;
        long weight;
        if (total > withChangeFee && total - withChangeFee >= dust)
        {
            change = total - withChangeFee;
            fee = withChangeFee;
            weight = withChangeWeight;
        }
        else if (total >= withoutChangeFee)
        {
            // The change would be dust: it goes to the fee
            change = null;
            fee = total;
            weight = withoutChangeWeight;
        }
        else
        {
            throw new ArgumentException($"The fee inputs ({total} sat) do not pay the {withoutChangeFee} sat fee at "
                                      + $"{feeratePerKw} sat/kw", nameof(feeInputs));
        }

        foreach (var input in feeInputs)
            tx.Inputs.Add(new OutPoint(new uint256((byte[])input.TxId), input.Vout), null, null,
                          new Sequence(SweepFeePolicy.RbfSequence));

        if (change is { } changeSat)
            tx.Outputs.Add(new TxOut(Money.Satoshis(changeSat), new Script(changeScript)));

        var combined = new HtlcTransactionBuildResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                                                      buildResult.SpentWitnessScript, buildResult.SpentAmount);
        return new AnchorHtlcTransaction(combined, feeInputs.ToList(), fee, change, weight);
    }

    /// <summary>
    /// The signed weight of the HTLC transaction with <paramref name="feeInputs"/> and, when
    /// <paramref name="changeScriptLength"/> is set, a change output: the non-witness bytes of every input and output,
    /// the segwit marker and flag, the HTLC witness (<c>0 &lt;remotesig&gt; &lt;localsig&gt; &lt;preimage or empty&gt;
    /// &lt;script&gt;</c> with worst-case signatures) and the fee inputs' own weights.
    /// </summary>
    private static long EstimateWeight(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                       IReadOnlyList<AnchorFeeInput> feeInputs, int? changeScriptLength)
    {
        const int htlcOutputScriptLength = 34; // P2WSH
        var inputCount = 1 + feeInputs.Count;
        var outputCount = changeScriptLength is null ? 1 : 2;
        var nonWitness = 4L + CompactSizeLength(inputCount) + 41 * inputCount + CompactSizeLength(outputCount)
                       + 8 + CompactSizeLength(htlcOutputScriptLength) + htlcOutputScriptLength + 4;
        if (changeScriptLength is { } length)
            nonWitness += 8 + CompactSizeLength(length) + length;

        var scriptLength = ((byte[])buildResult.SpentWitnessScript).Length;
        var preimageLength = transaction.Type == HtlcTransactionType.Success ? CryptoConstants.Sha256HashLen : 0;
        var htlcWitness = 1 + ItemSize(0) + 2 * ItemSize(MaxSignatureLength) + ItemSize(preimageLength)
                        + ItemSize(scriptLength);

        // Each fee input's weight includes its 41 non-witness bytes: count them once
        var feeInputsWitness = feeInputs.Sum(i => i.InputWeight - InputNonWitnessWeight);
        return 4 * nonWitness + 2 + htlcWitness + feeInputsWitness;
    }

    private static void ValidateFeeInputs(IReadOnlyList<AnchorFeeInput> feeInputs, OutPoint htlcOutPoint)
    {
        if (feeInputs.Count == 0)
            throw new ArgumentException("An anchors HTLC transaction needs at least one fee input", nameof(feeInputs));

        if (feeInputs.Count > MaxFeeInputs)
            throw new ArgumentException($"At most {MaxFeeInputs} fee inputs", nameof(feeInputs));

        var seen = new HashSet<OutPoint> { htlcOutPoint };
        for (var i = 0; i < feeInputs.Count; i++)
        {
            var input = feeInputs[i] ?? throw new ArgumentException($"Fee input {i} is null", nameof(feeInputs));
            if (input.AmountSat == 0)
                throw new ArgumentException($"Fee input {i} has no value", nameof(feeInputs));

            if (input.InputWeight <= InputNonWitnessWeight)
                throw new ArgumentException($"Fee input {i} needs its signed weight (more than {InputNonWitnessWeight})",
                                            nameof(feeInputs));

            if (input.ScriptPubKey is not { Length: > 0 })
                throw new ArgumentException($"Fee input {i} has no script", nameof(feeInputs));

            if (!seen.Add(new OutPoint(new uint256((byte[])input.TxId), input.Vout)))
                throw new ArgumentException($"Fee input {i} spends an outpoint twice", nameof(feeInputs));
        }
    }

    private static int ItemSize(int length) => CompactSizeLength(length) + length;

    private static int CompactSizeLength(int value) => value switch
    {
        < 0xfd => 1,
        <= 0xffff => 3,
        _ => 5
    };

    private static BaseHtlcOutput CreateSpentOutput(HtlcOutputInfo htlcOutput, bool hasAnchors)
    {
        return htlcOutput switch
        {
            OfferedHtlcOutputInfo offered => new OfferedHtlcOutput(offered.Amount, offered.CltvExpiry, hasAnchors,
                                                                   new PubKey(offered.LocalHtlcPubKey),
                                                                   offered.PaymentHash,
                                                                   new PubKey(offered.RemoteHtlcPubKey),
                                                                   new PubKey(offered.RevocationPubKey)),
            ReceivedHtlcOutputInfo received => new ReceivedHtlcOutput(received.Amount, received.CltvExpiry,
                                                                      hasAnchors,
                                                                      new PubKey(received.LocalHtlcPubKey),
                                                                      received.PaymentHash,
                                                                      new PubKey(received.RemoteHtlcPubKey),
                                                                      new PubKey(received.RevocationPubKey)),
            _ => throw new ArgumentException($"Unsupported HTLC output type {htlcOutput.GetType().Name}",
                                             nameof(htlcOutput))
        };
    }

    private static TransactionSignature ToTransactionSignature(CompactSignature signature, SigHash sigHash,
                                                               string paramName)
    {
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsaSignature))
            throw new ArgumentException("Invalid compact signature", paramName);

        return new TransactionSignature(ecdsaSignature, sigHash);
    }
}