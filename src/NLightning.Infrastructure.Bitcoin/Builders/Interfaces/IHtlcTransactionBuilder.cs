namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

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
}