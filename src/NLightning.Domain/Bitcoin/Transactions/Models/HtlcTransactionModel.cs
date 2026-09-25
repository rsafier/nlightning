namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Crypto.ValueObjects;
using Enums;
using Money;
using Outputs;
using ValueObjects;

/// <summary>
/// A BOLT 3 HTLC-timeout or HTLC-success transaction: version 2, one input spending an HTLC output of a commitment
/// transaction, one P2WSH output paying <c>revocationpubkey</c> or, after <c>to_self_delay</c>,
/// <c>local_delayedpubkey</c> (both of the commitment holder).
/// </summary>
public sealed class HtlcTransactionModel
{
    /// <summary>HTLC-timeout (holder offered the HTLC) or HTLC-success (holder received it).</summary>
    public HtlcTransactionType Type { get; }

    /// <summary>The txid of the commitment transaction that holds the HTLC output.</summary>
    public TxId CommitmentTxId { get; }

    /// <summary>The index of the HTLC output in the commitment transaction.</summary>
    public uint CommitmentOutputIndex { get; }

    /// <summary>The HTLC output being spent (amount, cltv_expiry, payment hash and its script keys).</summary>
    public HtlcOutputInfo SpentOutput { get; }

    /// <summary>Whether option_anchors applies (sequence 1, zero fee, SIGHASH_SINGLE|ANYONECANPAY remote signature).</summary>
    public bool HasAnchors { get; }

    /// <summary>The HTLC transaction fee: 0 with anchors, else feerate * 663 (timeout) or 703 (success) / 1000.</summary>
    public LightningMoney Fee { get; }

    /// <summary>The output amount: <c>floor(amount_msat / 1000) - fee</c>.</summary>
    public LightningMoney OutputAmount { get; }

    /// <summary><c>cltv_expiry</c> for HTLC-timeout, 0 for HTLC-success.</summary>
    public BitcoinLockTime LockTime { get; }

    /// <summary>1 with option_anchors, else 0.</summary>
    public BitcoinSequence Sequence { get; }

    /// <summary>The commitment's <c>revocationpubkey</c>.</summary>
    public CompactPubKey RevocationPubKey { get; }

    /// <summary>The commitment holder's <c>local_delayedpubkey</c>.</summary>
    public CompactPubKey LocalDelayedPubKey { get; }

    /// <summary>The CSV delay on the output.</summary>
    public ushort ToSelfDelay { get; }

    public HtlcTransactionModel(HtlcTransactionType type, TxId commitmentTxId, uint commitmentOutputIndex,
                                HtlcOutputInfo spentOutput, bool hasAnchors, LightningMoney fee,
                                LightningMoney outputAmount, BitcoinLockTime lockTime, BitcoinSequence sequence,
                                CompactPubKey revocationPubKey, CompactPubKey localDelayedPubKey, ushort toSelfDelay)
    {
        ArgumentNullException.ThrowIfNull(spentOutput);
        ArgumentNullException.ThrowIfNull(fee);
        ArgumentNullException.ThrowIfNull(outputAmount);

        Type = type;
        CommitmentTxId = commitmentTxId;
        CommitmentOutputIndex = commitmentOutputIndex;
        SpentOutput = spentOutput;
        HasAnchors = hasAnchors;
        Fee = fee;
        OutputAmount = outputAmount;
        LockTime = lockTime;
        Sequence = sequence;
        RevocationPubKey = revocationPubKey;
        LocalDelayedPubKey = localDelayedPubKey;
        ToSelfDelay = toSelfDelay;
    }
}