namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// Builds BOLT 3 HTLC-timeout and HTLC-success transactions.
/// </summary>
public interface IHtlcTransactionBuilder
{
    /// <summary>
    /// Builds the unsigned HTLC transaction and returns it with the witness script and amount of the commitment HTLC
    /// output it spends (the BIP 143 sighash inputs).
    /// </summary>
    HtlcTransactionBuildResult Build(HtlcTransactionModel transaction);

    /// <summary>
    /// Adds the BOLT 3 witness to an unsigned HTLC transaction:
    /// <c>0 &lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; &lt;payment_preimage&gt;</c> (HTLC-success) or
    /// <c>0 &lt;remotehtlcsig&gt; &lt;localhtlcsig&gt; &lt;&gt;</c> (HTLC-timeout), followed by the witness script.
    /// The remote signature carries <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> with option_anchors, else
    /// <c>SIGHASH_ALL</c>; the local signature always carries <c>SIGHASH_ALL</c>.
    /// </summary>
    /// <param name="transaction">The HTLC transaction model.</param>
    /// <param name="buildResult">The result of <see cref="Build"/> for that model.</param>
    /// <param name="remoteHtlcSignature">The counterparty's HTLC signature (from <c>commitment_signed</c>).</param>
    /// <param name="localHtlcSignature">Our HTLC signature.</param>
    /// <param name="paymentPreimage">The 32-byte preimage; required for HTLC-success, must be null for HTLC-timeout.</param>
    /// <returns>The signed transaction.</returns>
    SignedTransaction AddWitness(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                 CompactSignature remoteHtlcSignature, CompactSignature localHtlcSignature,
                                 byte[]? paymentPreimage = null);

    /// <summary>
    /// The signed weight of an anchors HTLC transaction with a change output of <paramref name="changeScriptLength"/>
    /// bytes and no fee input yet: what the wallet adds its inputs' weights to when it selects them.
    /// </summary>
    long EstimateAnchorBaseWeight(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                  int changeScriptLength);

    /// <summary>
    /// Combines a zero-fee option_anchors HTLC transaction with wallet inputs that pay its fee (BOLT 5 §Generation of
    /// HTLC Transactions, B5-HTX-02): the HTLC input and output stay at index 0 (the peer's
    /// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> signature commits to that pair only), the fee inputs follow with a
    /// BIP 125 replaceable <c>nSequence</c>, and what they carry beyond the fee at <paramref name="feeratePerKw"/> goes
    /// to a change output (to the fee when it would be dust). Our own HTLC signature (<c>SIGHASH_ALL</c>) is made over
    /// the returned transaction, then <see cref="AddWitness"/> completes input 0 and the wallet signs the others.
    /// </summary>
    /// <exception cref="ArgumentException">The transaction is not an anchors one as built, the fee inputs are empty,
    /// repeated or incomplete, or they do not pay the fee.</exception>
    AnchorHtlcTransaction AddFeeInputs(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                       IReadOnlyList<AnchorFeeInput> feeInputs, byte[] changeScript,
                                       uint feeratePerKw);
}