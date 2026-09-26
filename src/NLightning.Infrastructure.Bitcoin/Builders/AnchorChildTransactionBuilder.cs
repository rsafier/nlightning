using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Fees;
using Interfaces;
using Outputs;

/// <summary>
/// Builds the transactions that spend anchor outputs (BOLT 3 §to_local_anchor and to_remote_anchor Output, BOLT 5 plan
/// O7-T2).
/// </summary>
/// <remarks>
/// <para>CPFP child: version 2, <c>nLockTime</c> 0, input 0 is the anchor (keyed to our funding pubkey, spent with
/// <c>&lt;sig&gt; &lt;anchor script&gt;</c>, no relative lock), then the wallet inputs; every <c>nSequence</c> is
/// 0xFFFFFFFD so the child can be replaced (RBF) while the commitment waits. One output returns
/// <c>330 + sum(wallet inputs) - fee</c> to a wallet script; the fee is the caller's (it pays for the whole package).</para>
/// <para>Anchor sweep: after the commitment has 16 confirmations anyone may spend an anchor with an empty signature
/// (<c>OP_CHECKSIG</c> fails, <c>OP_16 OP_CHECKSEQUENCEVERIFY</c> passes with <c>nSequence</c> 16), so a sweep needs no
/// key.</para>
/// </remarks>
public sealed class AnchorChildTransactionBuilder : IAnchorChildTransactionBuilder
{
    /// <summary><c>nSequence</c> of an anchor spent after its 16-block delay (BOLT 3 <c>OP_16 OP_CSV</c>).</summary>
    public const uint AnchorCsvSequence = 16;

    private const int TransactionVersion = 2;

    // version (4) + input count (1) + output count (1) + nLockTime (4), times 4, plus the segwit marker and flag
    private const int OverheadWeight = 4 * 10 + 2;

    // 1 (item count) + 1 + 73 (signature) + 1 + 40 (anchor script)
    private const int AnchorSignedWitnessWeight = 1 + 1 + SweepWeights.MaxSignatureLength + 1 + AnchorScriptLength;

    // 1 (item count) + 1 (empty signature) + 1 + 40 (anchor script)
    private const int AnchorSweepWitnessWeight = 1 + 1 + 1 + AnchorScriptLength;

    private const int AnchorScriptLength = 40;

    private static readonly ulong s_anchorSat = (ulong)TransactionConstants.AnchorOutputAmount.Satoshi;

    /// <inheritdoc />
    public byte[] GetAnchorWitnessScript(CompactPubKey fundingPubKey) =>
        CreateAnchorOutput(fundingPubKey).RedeemScript.ToBytes();

    /// <inheritdoc />
    public byte[] GetAnchorScriptPubKey(CompactPubKey fundingPubKey) =>
        CreateAnchorOutput(fundingPubKey).ScriptPubKey.ToBytes();

    /// <inheritdoc />
    public uint? FindAnchorOutput(byte[] commitmentTransaction, CompactPubKey fundingPubKey)
    {
        ArgumentNullException.ThrowIfNull(commitmentTransaction);

        var tx = Transaction.Load(commitmentTransaction, Network.Main);
        var scriptPubKey = CreateAnchorOutput(fundingPubKey).ScriptPubKey;
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            if (output.ScriptPubKey == scriptPubKey && (ulong)output.Value.Satoshi == s_anchorSat)
                return (uint)i;
        }

        return null;
    }

    /// <inheritdoc />
    public long EstimateChildWeight(IReadOnlyList<AnchorWalletInput> walletInputs, int changeScriptLength)
    {
        ArgumentNullException.ThrowIfNull(walletInputs);

        return OverheadWeight
             + SweepWeights.InputNonWitnessWeight + AnchorSignedWitnessWeight
             + walletInputs.Sum(i => (long)i.InputWeight)
             + OutputWeight(changeScriptLength);
    }

    /// <inheritdoc />
    public UnsignedAnchorChild BuildChild(AnchorOutpoint anchor, IReadOnlyList<AnchorWalletInput> walletInputs,
                                          byte[] changeScript, ulong feeSat)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(walletInputs);
        ArgumentNullException.ThrowIfNull(changeScript);
        if (walletInputs.Count == 0)
            throw new ArgumentException("An anchor child needs at least one wallet input to pay its fee",
                                        nameof(walletInputs));

        var anchorOutPoint = new OutPoint(new uint256(anchor.TxId), anchor.OutputIndex);
        var seen = new HashSet<OutPoint> { anchorOutPoint };
        foreach (var input in walletInputs)
        {
            if (input.InputWeight <= 0)
                throw new ArgumentException($"Wallet input {input.TxId}:{input.OutputIndex} has no weight",
                                            nameof(walletInputs));
            if (!seen.Add(new OutPoint(new uint256(input.TxId), input.OutputIndex)))
                throw new ArgumentException($"Outpoint {input.TxId}:{input.OutputIndex} is spent twice",
                                            nameof(walletInputs));
        }

        var total = walletInputs.Aggregate(s_anchorSat, (sum, i) => checked(sum + i.AmountSat));
        var dust = ShutdownScriptValidator.GetDustThresholdSat(changeScript);
        if (feeSat >= total || total - feeSat < dust)
            throw new ArgumentException(
                $"The change ({total} - {feeSat} sat) would be below the change script's dust limit ({dust} sat)",
                nameof(feeSat));

        var change = total - feeSat;
        var tx = Transaction.Create(Network.Main);
        tx.Version = TransactionVersion;
        tx.LockTime = LockTime.Zero;
        tx.Inputs.Add(new TxIn(anchorOutPoint) { Sequence = new Sequence(SweepFeePolicy.RbfSequence) });
        foreach (var input in walletInputs)
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256(input.TxId), input.OutputIndex))
            {
                Sequence = new Sequence(SweepFeePolicy.RbfSequence)
            });
        tx.Outputs.Add(new TxOut(Money.Satoshis(change), new Script(changeScript)));

        var weight = EstimateChildWeight(walletInputs, changeScript.Length);
        return new UnsignedAnchorChild(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()), 0, anchor,
                                       walletInputs.ToList(), feeSat, weight, change);
    }

    /// <inheritdoc />
    public SignedTransaction AddAnchorWitness(byte[] transaction, int anchorInputIndex, CompactSignature signature,
                                              CompactPubKey fundingPubKey)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var tx = Transaction.Load(transaction, Network.Main);
        if (anchorInputIndex < 0 || anchorInputIndex >= tx.Inputs.Count)
            throw new ArgumentOutOfRangeException(nameof(anchorInputIndex), "The transaction has no such input");
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsa))
            throw new ArgumentException("The anchor signature is not a valid compact signature", nameof(signature));

        var witnessScript = CreateAnchorOutput(fundingPubKey).RedeemScript;
        tx.Inputs[anchorInputIndex].WitScript =
            new WitScript(Op.GetPushOp(new TransactionSignature(ecdsa, SigHash.All).ToBytes()),
                          Op.GetPushOp(witnessScript.ToBytes()));
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes(), [signature]);
    }

    /// <inheritdoc />
    public long EstimateSweepWeight(int anchorCount, int destinationScriptLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorCount);
        return OverheadWeight + (long)anchorCount * (SweepWeights.InputNonWitnessWeight + AnchorSweepWitnessWeight)
                              + OutputWeight(destinationScriptLength);
    }

    /// <inheritdoc />
    public SignedTransaction BuildAnchorSweep(IReadOnlyList<AnchorOutpoint> anchors, byte[] destinationScript,
                                              ulong feeSat)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(destinationScript);
        if (anchors.Count == 0)
            throw new ArgumentException("No anchor to sweep", nameof(anchors));

        var total = s_anchorSat * (ulong)anchors.Count;
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destinationScript);
        if (feeSat >= total || total - feeSat < dust)
            throw new ArgumentException(
                $"The sweep output ({total} - {feeSat} sat) would be below its dust limit ({dust} sat)",
                nameof(feeSat));

        var tx = Transaction.Create(Network.Main);
        tx.Version = TransactionVersion;
        tx.LockTime = LockTime.Zero;
        foreach (var anchor in anchors)
        {
            var witnessScript = CreateAnchorOutput(anchor.FundingPubKey).RedeemScript;
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256(anchor.TxId), anchor.OutputIndex))
            {
                Sequence = new Sequence(AnchorCsvSequence),
                WitScript = new WitScript(Op.GetPushOp([]), Op.GetPushOp(witnessScript.ToBytes()))
            });
        }

        tx.Outputs.Add(new TxOut(Money.Satoshis(total - feeSat), new Script(destinationScript)));
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    private static ToAnchorOutput CreateAnchorOutput(CompactPubKey fundingPubKey) =>
        new(TransactionConstants.AnchorOutputAmount, new PubKey(fundingPubKey));

    private static long OutputWeight(int scriptLength) =>
        4L * (8 + VarIntSize(scriptLength) + scriptLength);

    private static int VarIntSize(int value) => value < 0xFD ? 1 : 3;
}