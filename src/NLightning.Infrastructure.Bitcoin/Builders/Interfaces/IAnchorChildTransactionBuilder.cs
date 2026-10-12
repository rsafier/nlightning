namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A wallet output that pays part of an anchor child's fee (BOLT 5 plan O7-T2): its outpoint, value, scriptPubKey and
/// the weight it adds to a transaction once signed (non-witness part plus its worst-case witness).
/// </summary>
/// <param name="TxId">The transaction holding the output.</param>
/// <param name="OutputIndex">Its index.</param>
/// <param name="AmountSat">Its value in satoshis.</param>
/// <param name="ScriptPubKey">Its scriptPubKey (P2WPKH or P2TR from our wallet).</param>
/// <param name="InputWeight">The weight of the input spending it, witness included (P2WPKH 273, P2TR key path 230).
/// </param>
public sealed record AnchorWalletInput(TxId TxId, uint OutputIndex, ulong AmountSat, byte[] ScriptPubKey,
                                       int InputWeight);

/// <summary>
/// An anchor output (BOLT 3 <c>to_local_anchor</c>/<c>to_remote_anchor</c>, 330 sat, P2WSH of
/// <c>&lt;funding_pubkey&gt; OP_CHECKSIG OP_IFDUP OP_NOTIF OP_16 OP_CHECKSEQUENCEVERIFY OP_ENDIF</c>).
/// </summary>
/// <param name="TxId">The commitment transaction holding it.</param>
/// <param name="OutputIndex">Its index in the commitment.</param>
/// <param name="FundingPubKey">The funding pubkey its script is keyed to; for a simple taproot anchor, its internal key
/// (the holder's <c>local_delayedpubkey</c> for <c>to_local_anchor</c>, the other side's <c>remotepubkey</c> for
/// <c>to_remote_anchor</c>; NL-915).</param>
/// <param name="IsTaproot">A simple taproot anchor (NL-966): P2TR of the internal key with one leaf
/// <c>OP_16 OP_CHECKSEQUENCEVERIFY</c>; its owner spends it by key path, anyone by that leaf after 16 blocks.</param>
/// <param name="OurPerCommitmentPoint">A simple taproot <c>to_local_anchor</c> of ours on our commitment: the
/// per-commitment point the signer tweaks our delayed key with; null for our anchor on the peer's commitment (keyed to
/// our payment basepoint) and for P2WSH anchors.</param>
public sealed record AnchorOutpoint(TxId TxId, uint OutputIndex, CompactPubKey FundingPubKey, bool IsTaproot = false,
                                    CompactPubKey? OurPerCommitmentPoint = null);

/// <summary>
/// An unsigned anchor child (CPFP): input <see cref="AnchorInputIndex"/> spends our anchor, the others the wallet inputs
/// in order, and one output returns the change to the wallet.
/// </summary>
/// <param name="Transaction">The unsigned transaction (no witnesses).</param>
/// <param name="AnchorInputIndex">The index of the anchor input (always 0).</param>
/// <param name="Anchor">The anchor it spends.</param>
/// <param name="WalletInputs">The wallet inputs, in input order after the anchor.</param>
/// <param name="FeeSat">The absolute fee it pays.</param>
/// <param name="Weight">Its estimated weight once signed (73-byte signatures).</param>
/// <param name="ChangeSat">The value of its single output.</param>
public sealed record UnsignedAnchorChild(SignedTransaction Transaction, int AnchorInputIndex, AnchorOutpoint Anchor,
                                         IReadOnlyList<AnchorWalletInput> WalletInputs, ulong FeeSat, long Weight,
                                         ulong ChangeSat);

/// <summary>
/// Builds the transactions that spend anchor outputs (BOLT 3 §to_local_anchor and to_remote_anchor Output, BOLT 5 plan
/// O7-T2): the CPFP child of our commitment (our anchor plus wallet inputs, one change output) and the sweep of anchors
/// anyone may spend 16 blocks after the commitment confirmed.
/// </summary>
public interface IAnchorChildTransactionBuilder
{
    /// <summary>The anchor's witness script for <paramref name="fundingPubKey"/> (40 bytes).</summary>
    byte[] GetAnchorWitnessScript(CompactPubKey fundingPubKey);

    /// <summary>The anchor's P2WSH scriptPubKey for <paramref name="fundingPubKey"/>.</summary>
    byte[] GetAnchorScriptPubKey(CompactPubKey fundingPubKey);

    /// <summary>
    /// The index of the 330-sat anchor output keyed to <paramref name="fundingPubKey"/> in
    /// <paramref name="commitmentTransaction"/>, or null when it has none (no anchors, or BOLT 3 omitted it because the
    /// owner has neither a <c>to_local</c>/<c>to_remote</c> output nor an untrimmed HTLC).
    /// </summary>
    uint? FindAnchorOutput(byte[] commitmentTransaction, CompactPubKey fundingPubKey);

    /// <summary>The P2WSH (or, for a simple taproot anchor, P2TR) scriptPubKey of <paramref name="anchor"/>.</summary>
    byte[] GetAnchorScriptPubKey(AnchorOutpoint anchor);

    /// <summary>
    /// The index of the 330-sat simple taproot anchor whose internal key is <paramref name="internalKey"/> in
    /// <paramref name="commitmentTransaction"/>, or null when it has none (NL-966).
    /// </summary>
    uint? FindTaprootAnchorOutput(byte[] commitmentTransaction, CompactPubKey internalKey);

    /// <summary>
    /// The weight of a child with <paramref name="walletInputs"/> and one output of
    /// <paramref name="changeScriptLength"/> bytes, with a 73-byte anchor signature, or with
    /// <paramref name="taprootAnchor"/> the 64-byte key-path signature of a simple taproot anchor.
    /// </summary>
    long EstimateChildWeight(IReadOnlyList<AnchorWalletInput> walletInputs, int changeScriptLength,
                             bool taprootAnchor = false);

    /// <summary>
    /// Builds the unsigned child: version 2, <c>nLockTime</c> 0, input 0 the anchor then the wallet inputs, every
    /// <c>nSequence</c> 0xFFFFFFFD (BIP 125 replaceable), one output of <c>330 + sum(wallet inputs) - fee</c> to
    /// <paramref name="changeScript"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No wallet input, a wallet input spends the anchor's commitment output twice,
    /// or the change would be below the change script's dust limit.</exception>
    UnsignedAnchorChild BuildChild(AnchorOutpoint anchor, IReadOnlyList<AnchorWalletInput> walletInputs,
                                   byte[] changeScript, ulong feeSat);

    /// <summary>
    /// Adds the anchor witness <c>&lt;sig&gt; &lt;anchor script&gt;</c> (<c>SIGHASH_ALL</c>) to input
    /// <paramref name="anchorInputIndex"/> of <paramref name="transaction"/> (its other witnesses are kept) and returns
    /// the transaction with its txid.
    /// </summary>
    SignedTransaction AddAnchorWitness(byte[] transaction, int anchorInputIndex, CompactSignature signature,
                                       CompactPubKey fundingPubKey);

    /// <summary>
    /// Adds the key-path witness <c>&lt;sig&gt;</c> (a 64-byte BIP 340 <c>SIGHASH_DEFAULT</c> signature) of a simple
    /// taproot anchor to input <paramref name="anchorInputIndex"/> of <paramref name="transaction"/> (NL-966).
    /// </summary>
    SignedTransaction AddTaprootAnchorWitness(byte[] transaction, int anchorInputIndex, CompactSignature signature);

    /// <summary>The weight of a sweep of <paramref name="anchorCount"/> anchors (empty signatures) to one output.</summary>
    long EstimateSweepWeight(int anchorCount, int destinationScriptLength);

    /// <summary>
    /// Builds the fully "signed" sweep of anchors anyone may spend (BOLT 3: after 16 blocks, witness
    /// <c>&lt;&gt; &lt;anchor script&gt;</c>; a simple taproot anchor by its leaf, <c>&lt;OP_16 OP_CSV&gt;
    /// &lt;control block&gt;</c>): version 2, every <c>nSequence</c> 16, one output of
    /// <c>330 * count - fee</c> to <paramref name="destinationScript"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No anchor, or the output would be below its dust limit.</exception>
    SignedTransaction BuildAnchorSweep(IReadOnlyList<AnchorOutpoint> anchors, byte[] destinationScript, ulong feeSat);
}