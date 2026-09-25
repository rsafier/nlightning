namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Money;
using ValueObjects;

/// <summary>
/// The result of building an HTLC-timeout/HTLC-success transaction: the unsigned transaction plus what a signer needs
/// for the BIP 143 sighash of its single input.
/// </summary>
/// <param name="Transaction">The unsigned HTLC transaction.</param>
/// <param name="SpentWitnessScript">The witness script of the commitment HTLC output it spends.</param>
/// <param name="SpentAmount">The value of that output (whole satoshis).</param>
public sealed record HtlcTransactionBuildResult(
    SignedTransaction Transaction,
    BitcoinScript SpentWitnessScript,
    LightningMoney SpentAmount);