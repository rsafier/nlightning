namespace NLightning.Domain.Channels.Closing;

using Bitcoin.Transactions.Models;
using Bitcoin.Transactions.Outputs;
using Bitcoin.ValueObjects;
using Money;

/// <summary>Which outputs a legacy closing transaction keeps (BOLT 3: "MAY eliminate its own output").</summary>
public enum ClosingVariant : byte
{
    /// <summary>Every output at or above the signer's dust limit.</summary>
    Full = 0,

    /// <summary>As <see cref="Full"/>, without our output.</summary>
    WithoutLocalOutput = 1,

    /// <summary>As <see cref="Full"/>, without the peer's output.</summary>
    WithoutRemoteOutput = 2
}

/// <summary>
/// Builds the content of a BOLT 3 legacy closing transaction (N10-T2, B3-LCTX-01) from the final balances.
/// </summary>
/// <remarks>
/// BOLT 3: each output is the side's final balance rounded down to whole satoshis; <c>fee_satoshis</c> comes off the
/// funder's output; an output below the signer's own <c>dust_limit_satoshis</c> is removed; the signer MAY also remove
/// its own output. The balances must be final (no HTLC left, BOLT 2).
/// </remarks>
public static class LegacyClosingTransactionFactory
{
    /// <summary>The highest fee the funder can pay: its whole balance in whole satoshis.</summary>
    public static ulong MaxFeeSat(ulong localBalanceMsat, ulong remoteBalanceMsat, bool localIsFunder) =>
        (localIsFunder ? localBalanceMsat : remoteBalanceMsat) / 1000;

    /// <summary>Builds the transaction content.</summary>
    /// <param name="fundingOutput">The funding output (txid and index set).</param>
    /// <param name="localBalanceMsat">Our final balance.</param>
    /// <param name="remoteBalanceMsat">The peer's final balance.</param>
    /// <param name="localIsFunder">True when we funded the channel (we pay the fee).</param>
    /// <param name="feeSat">The <c>fee_satoshis</c>.</param>
    /// <param name="localScript">Our <c>shutdown</c> script.</param>
    /// <param name="remoteScript">The peer's <c>shutdown</c> script.</param>
    /// <param name="dustLimitSat">The <c>dust_limit_satoshis</c> of the side whose signature this is for.</param>
    /// <param name="variant">Which outputs to keep.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fee exceeds the funder's balance.</exception>
    /// <exception cref="InvalidOperationException">No output is left.</exception>
    public static ClosingTransactionModel Create(FundingOutputInfo fundingOutput, ulong localBalanceMsat,
                                                 ulong remoteBalanceMsat, bool localIsFunder, ulong feeSat,
                                                 BitcoinScript localScript, BitcoinScript remoteScript,
                                                 ulong dustLimitSat, ClosingVariant variant = ClosingVariant.Full)
    {
        ArgumentNullException.ThrowIfNull(fundingOutput);
        var maxFee = MaxFeeSat(localBalanceMsat, remoteBalanceMsat, localIsFunder);
        if (feeSat > maxFee)
            throw new ArgumentOutOfRangeException(nameof(feeSat), feeSat,
                                                  $"The fee is above the funder's balance of {maxFee} sat");

        var localSat = localBalanceMsat / 1000;
        var remoteSat = remoteBalanceMsat / 1000;
        if (localIsFunder)
            localSat -= feeSat;
        else
            remoteSat -= feeSat;

        var outputs = new List<ClosingOutput>(2);
        if (variant != ClosingVariant.WithoutLocalOutput && localSat >= dustLimitSat && localSat > 0)
            outputs.Add(new ClosingOutput(localScript, LightningMoney.Satoshis(localSat), true));
        if (variant != ClosingVariant.WithoutRemoteOutput && remoteSat >= dustLimitSat && remoteSat > 0)
            outputs.Add(new ClosingOutput(remoteScript, LightningMoney.Satoshis(remoteSat), false));

        if (outputs.Count == 0)
            throw new InvalidOperationException("Every output of the closing transaction is below the dust limit");

        return new ClosingTransactionModel(fundingOutput, LightningMoney.Satoshis(feeSat), outputs);
    }
}