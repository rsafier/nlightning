namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Money;
using Outputs;
using ValueObjects;

/// <summary>
/// One output of a mutual close transaction: a side's final balance paid to its <c>shutdown</c> script.
/// </summary>
/// <param name="ScriptPubKey">The script from that side's <c>shutdown</c>.</param>
/// <param name="Amount">Whole satoshis (BOLT 3: rounded down, the fee already taken from the funder's output).</param>
/// <param name="IsLocal">True for our output.</param>
public sealed record ClosingOutput(BitcoinScript ScriptPubKey, LightningMoney Amount, bool IsLocal);

/// <summary>
/// A BOLT 3 legacy closing transaction (the <c>closing_signed</c> variant): version 2, locktime 0, the funding output
/// as its only input (sequence 0xFFFFFFFF), one or two outputs. Data only; the Bitcoin builder orders the outputs
/// (BOLT 3 "Transaction Output Ordering") and the signer signs it.
/// </summary>
public sealed class ClosingTransactionModel
{
    /// <summary>The funding output spent (txid and index set).</summary>
    public FundingOutputInfo FundingOutput { get; }

    /// <summary>The <c>fee_satoshis</c> of the <c>closing_signed</c> this transaction was built for.</summary>
    public LightningMoney Fee { get; }

    /// <summary>The outputs left after dust removal, in no particular order.</summary>
    public IReadOnlyList<ClosingOutput> Outputs { get; }

    public ClosingTransactionModel(FundingOutputInfo fundingOutput, LightningMoney fee,
                                   IReadOnlyList<ClosingOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(fundingOutput);
        ArgumentNullException.ThrowIfNull(outputs);
        if (fundingOutput.TransactionId is null || fundingOutput.Index is null)
            throw new ArgumentException("The funding outpoint is not known", nameof(fundingOutput));
        if (outputs.Count is 0 or > 2)
            throw new ArgumentException("A closing transaction has one or two outputs", nameof(outputs));

        FundingOutput = fundingOutput;
        Fee = fee;
        Outputs = outputs;
    }

    /// <summary>Our output, if it was not removed.</summary>
    public ClosingOutput? LocalOutput => Outputs.FirstOrDefault(o => o.IsLocal);

    /// <summary>The peer's output, if it was not removed.</summary>
    public ClosingOutput? RemoteOutput => Outputs.FirstOrDefault(o => !o.IsLocal);
}