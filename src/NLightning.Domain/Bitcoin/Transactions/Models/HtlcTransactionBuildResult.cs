namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Money;
using ValueObjects;
using Wallet.Models;

/// <summary>
/// The result of building an HTLC-timeout/HTLC-success transaction: the unsigned transaction plus what a signer needs
/// for the sighash of its single input (BIP 143 for a P2WSH HTLC output, BIP 341 script path for a taproot one).
/// </summary>
/// <param name="Transaction">The unsigned HTLC transaction.</param>
/// <param name="SpentWitnessScript">
/// The witness script of the commitment HTLC output it spends; for a simple taproot HTLC output, the tapscript leaf it
/// spends (the timeout leaf for HTLC-timeout, the success leaf for HTLC-success).
/// </param>
/// <param name="SpentAmount">The value of that output (whole satoshis).</param>
public sealed record HtlcTransactionBuildResult(
    SignedTransaction Transaction,
    BitcoinScript SpentWitnessScript,
    LightningMoney SpentAmount)
{
    /// <summary>
    /// The scriptPubKey of the spent output when it is a P2TR output (<c>OP_1 &lt;output key&gt;</c>, a BIP 341
    /// signature commits to it); null for a P2WSH output, whose scriptPubKey follows from
    /// <see cref="SpentWitnessScript"/>.
    /// </summary>
    public BitcoinScript? SpentScriptPubKey { get; init; }

    /// <summary>
    /// The BIP 341 control block of the spent leaf (<c>(parity | 0xc0) || internal key || inclusion proof</c>) when the
    /// spent output is a simple taproot HTLC output; null for a P2WSH output.
    /// </summary>
    public byte[]? ControlBlock { get; init; }

    /// <summary>
    /// The outputs the wallet fee inputs of a combined simple taproot HTLC transaction spend, in input order from index
    /// 1 (NL-904 item 4): the holder's <c>SIGHASH_DEFAULT</c> signature on input 0 commits to every spent output, so the
    /// signer needs them. Null for a transaction as built (one input) and for P2WSH HTLC transactions, whose BIP 143
    /// sighash needs only the HTLC output.
    /// </summary>
    public IReadOnlyList<SpentOutput>? FeeInputSpentOutputs { get; init; }

    /// <summary>Whether the spent output is a simple taproot (P2TR) HTLC output, spent by a tapscript leaf.</summary>
    public bool IsTaproot => ControlBlock is not null;
}