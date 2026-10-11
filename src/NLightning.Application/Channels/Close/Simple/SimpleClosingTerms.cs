namespace NLightning.Application.Channels.Close.Simple;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Money;
using Domain.Protocol.Payloads;

/// <summary>
/// One closing transaction proposal of BOLT 2 <c>option_simple_close</c>, from the closer's point of view: the final
/// balances, both scripts and the fee the closer pays (BOLT 3 "Closing Transaction"). Pure.
/// </summary>
/// <param name="Funding">The funding output (txid and index set).</param>
/// <param name="CloserBalanceMsat">The closer's final balance.</param>
/// <param name="CloseeBalanceMsat">The closee's final balance.</param>
/// <param name="CloserScript">The <c>closer_scriptpubkey</c>.</param>
/// <param name="CloseeScript">The <c>closee_scriptpubkey</c>.</param>
/// <param name="FeeSat">The <c>fee_satoshis</c>, taken from the closer's output.</param>
/// <param name="CloserIsLocal">True when we are the closer (marks which output is ours).</param>
public sealed record SimpleClosingTerms(FundingOutputInfo Funding, ulong CloserBalanceMsat, ulong CloseeBalanceMsat,
                                        BitcoinScript CloserScript, BitcoinScript CloseeScript, ulong FeeSat,
                                        bool CloserIsLocal)
{
    /// <summary>The closer's balance in whole satoshis, rounded down (the most it can pay as fee).</summary>
    public ulong CloserBalanceSat => CloserBalanceMsat / 1000;

    /// <summary>True when the fee is at most the closer's balance (BOLT 2: MUST).</summary>
    public bool CloserCanPay => FeeSat <= CloserBalanceSat;

    /// <summary>
    /// The closer's output amount (BOLT 3): 0 for an <c>OP_RETURN</c> script, else its balance rounded down minus the
    /// fee.
    /// </summary>
    /// <exception cref="InvalidOperationException">The fee is above the closer's balance.</exception>
    public ulong CloserAmountSat
    {
        get
        {
            if (!CloserCanPay)
                throw new InvalidOperationException(
                    $"The fee of {FeeSat} sat is above the closer's balance of {CloserBalanceSat} sat");

            return IsOpReturn(CloserScript) ? 0 : CloserBalanceSat - FeeSat;
        }
    }

    /// <summary>The closee's output amount (BOLT 3): 0 for an <c>OP_RETURN</c> script, else its balance rounded down.
    /// </summary>
    public ulong CloseeAmountSat => IsOpReturn(CloseeScript) ? 0 : CloseeBalanceMsat / 1000;

    /// <summary>The closer's output is below its script's dust threshold (an <c>OP_RETURN</c> never is).</summary>
    public bool CloserIsDust => IsDust(CloserAmountSat, CloserScript);

    /// <summary>The closee's output is below its script's dust threshold (an <c>OP_RETURN</c> never is).</summary>
    public bool CloseeIsDust => IsDust(CloseeAmountSat, CloseeScript);

    /// <summary>
    /// The closer has less than the closee (millisatoshi, BOLT 2 "lesser amount"): it may drop its own output.
    /// </summary>
    public bool CloserIsLesser => CloserBalanceMsat < CloseeBalanceMsat;

    /// <summary>The closing transaction of the variant <paramref name="kind"/> (outputs as BOLT 3, in no order).</summary>
    public ClosingTransactionModel Build(ClosingSigKind kind)
    {
        var outputs = new List<ClosingOutput>(2);
        if (kind is ClosingSigKind.CloserOutputOnly or ClosingSigKind.CloserAndCloseeOutputs)
            outputs.Add(new ClosingOutput(CloserScript, LightningMoney.Satoshis(CloserAmountSat), CloserIsLocal));
        if (kind is ClosingSigKind.CloseeOutputOnly or ClosingSigKind.CloserAndCloseeOutputs)
            outputs.Add(new ClosingOutput(CloseeScript, LightningMoney.Satoshis(CloseeAmountSat), !CloserIsLocal));

        return new ClosingTransactionModel(Funding, LightningMoney.Satoshis(FeeSat), outputs);
    }

    /// <summary>True for a script that starts with <c>OP_RETURN</c> (BOLT 3: its output carries 0).</summary>
    public static bool IsOpReturn(BitcoinScript script) => script.Length > 0 && ((byte[])script)[0] == 0x6a;

    private static bool IsDust(ulong amountSat, BitcoinScript script) =>
        amountSat < ShutdownScriptValidator.GetDustThresholdSat((byte[])script);
}