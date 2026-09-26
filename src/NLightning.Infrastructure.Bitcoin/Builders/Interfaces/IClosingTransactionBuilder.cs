namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Builds the BOLT 3 closing transactions: the legacy one (BOLT2 plan N10-T2, B3-LCTX-01) and the
/// <c>option_simple_close</c> one (N11-T2, B3-CLTX-01).
/// </summary>
public interface IClosingTransactionBuilder
{
    /// <summary>
    /// The unsigned transaction: version 2, locktime 0, the funding outpoint with sequence 0xFFFFFFFF and an empty
    /// script, the outputs in BOLT 3 order (amount, then scriptpubkey).
    /// </summary>
    SignedTransaction Build(ClosingTransactionModel transaction);

    /// <summary>
    /// The unsigned BOLT 3 closing transaction of <c>option_simple_close</c> (<c>closing_complete</c>/
    /// <c>closing_sig</c>): version 2, <paramref name="lockTime"/> from the message, the funding outpoint with sequence
    /// 0xFFFFFFFD (replaceable) and an empty script, the outputs in BOLT 3 order (amount, then scriptpubkey). An
    /// <c>OP_RETURN</c> output carries the amount of its model (zero, BOLT 3).
    /// </summary>
    SignedTransaction BuildSimple(ClosingTransactionModel transaction, uint lockTime);

    /// <summary>
    /// Adds the witness <c>0 &lt;signature_for_pubkey1&gt; &lt;signature_for_pubkey2&gt; &lt;funding script&gt;</c>
    /// (pubkey1 is the lexicographically lesser funding key; both signatures <c>SIGHASH_ALL</c>).
    /// </summary>
    /// <param name="unsignedTransaction">The result of <see cref="Build"/>.</param>
    /// <param name="fundingOutput">The funding output spent: its local and remote funding keys.</param>
    /// <param name="localSignature">Our signature.</param>
    /// <param name="remoteSignature">The peer's signature.</param>
    SignedTransaction AddWitness(SignedTransaction unsignedTransaction,
                                 FundingOutputInfo fundingOutput,
                                 CompactSignature localSignature, CompactSignature remoteSignature);
}