namespace NLightning.Domain.Onchain.Models;

using Bitcoin.Transactions.Models;

/// <summary>
/// A zero-fee anchors HTLC-timeout/success transaction combined with wallet inputs that pay its fee (B5-HTX-02): the
/// HTLC input and its second-level output stay at index 0 (the peer's <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>
/// signature pairs them), the fee inputs follow, and the change output, if any, is output 1.
/// </summary>
/// <param name="BuildResult">The combined unsigned transaction, with the HTLC output's witness script and amount (the
/// sighash inputs of our <c>SIGHASH_ALL</c> signature on input 0).</param>
/// <param name="FeeInputs">The wallet inputs, in input order from index 1.</param>
/// <param name="FeeSat">The fee the transaction pays (the fee inputs minus the change).</param>
/// <param name="ChangeSat">The change output's amount, or null when the change was below dust and went to the fee.</param>
/// <param name="EstimatedWeight">The weight once signed (worst-case signatures).</param>
public sealed record AnchorHtlcTransaction(
    HtlcTransactionBuildResult BuildResult,
    IReadOnlyList<AnchorFeeInput> FeeInputs,
    ulong FeeSat,
    ulong? ChangeSat,
    long EstimatedWeight);