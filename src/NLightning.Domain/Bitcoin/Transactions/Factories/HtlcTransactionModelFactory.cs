namespace NLightning.Domain.Bitcoin.Transactions.Factories;

using Enums;
using Models;
using Money;
using Outputs;
using ValueObjects;

/// <summary>
/// Creates the BOLT 3 HTLC-timeout/HTLC-success transaction models for the HTLC outputs of a commitment transaction.
/// </summary>
/// <remarks>
/// A commitment's HTLC transactions belong to its holder: an HTLC the holder offered is spent by HTLC-timeout, one it
/// received by HTLC-success. Both pay the holder's <c>local_delayedpubkey</c> after <c>to_self_delay</c>, or the
/// counterparty's <c>revocationpubkey</c>.
/// </remarks>
public static class HtlcTransactionModelFactory
{
    /// <summary>
    /// Creates one HTLC transaction per untrimmed HTLC output, in commitment output order (the order of
    /// <c>commitment_signed.htlc_signatures</c>).
    /// </summary>
    /// <param name="commitment">The commitment model (built by the commitment factory).</param>
    /// <param name="buildResult">The built commitment with its HTLC output map.</param>
    public static IReadOnlyList<HtlcTransactionModel> CreateHtlcTransactionModels(
        CommitmentTransactionModel commitment, CommitmentTransactionBuildResult buildResult)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(buildResult);

        return buildResult.HtlcOutputsInTxOrder
                          .Select(h => CreateHtlcTransactionModel(commitment, buildResult.Transaction.TxId, h.Output,
                                                                  h.Vout))
                          .ToList();
    }

    /// <summary>
    /// Creates the HTLC transaction that spends one HTLC output of a commitment transaction.
    /// </summary>
    /// <param name="commitment">The commitment model the output belongs to.</param>
    /// <param name="commitmentTxId">The commitment txid.</param>
    /// <param name="htlcOutput">The HTLC output being spent.</param>
    /// <param name="outputIndex">Its index in the commitment transaction.</param>
    public static HtlcTransactionModel CreateHtlcTransactionModel(CommitmentTransactionModel commitment,
                                                                  TxId commitmentTxId, HtlcOutputInfo htlcOutput,
                                                                  uint outputIndex)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        ArgumentNullException.ThrowIfNull(htlcOutput);

        if (commitment.LocalDelayedPubKey is null || commitment.RevocationPubKey is null)
            throw new InvalidOperationException(
                "The commitment model has no delayed/revocation keys; build it with the commitment factory");

        var type = htlcOutput.OutputType switch
        {
            OutputType.OfferedHtlc => HtlcTransactionType.Timeout,
            OutputType.ReceivedHtlc => HtlcTransactionType.Success,
            _ => throw new ArgumentException($"Output type {htlcOutput.OutputType} is not an HTLC output",
                                             nameof(htlcOutput))
        };

        var fee = CommitmentFeeCalculator.HtlcTransactionFee(type == HtlcTransactionType.Timeout,
                                                              commitment.FeeRatePerKw, commitment.HasAnchors);

        // txout[0] amount: floor(amount_msat / 1000) - fee. An untrimmed HTLC always covers its fee.
        var amountSats = htlcOutput.Amount.Satoshi;
        if (amountSats < fee.Satoshi)
            throw new InvalidOperationException(
                $"HTLC {htlcOutput.Htlc.Id} ({amountSats} sat) cannot pay its {fee.Satoshi} sat HTLC transaction fee");
        var outputAmount = LightningMoney.Satoshis(amountSats - fee.Satoshi);

        var lockTime = type == HtlcTransactionType.Timeout ? htlcOutput.CltvExpiry : 0U;
        var sequence = commitment.HasAnchors ? 1U : 0U;

        return new HtlcTransactionModel(type, commitmentTxId, outputIndex, htlcOutput, commitment.HasAnchors, fee,
                                        outputAmount, lockTime, sequence, commitment.RevocationPubKey.Value,
                                        commitment.LocalDelayedPubKey.Value, commitment.ToSelfDelay);
    }
}