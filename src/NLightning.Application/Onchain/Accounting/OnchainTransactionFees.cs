namespace NLightning.Application.Onchain.Accounting;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Money;
using Infrastructure.Bitcoin.Onchain;

/// <summary>
/// The absolute fee of a transaction we build, for <c>BroadcastTransactionModel.Fee</c> (NL-604): its inputs' value
/// minus its outputs' value, where the caller knows every input's value.
/// </summary>
internal static class OnchainTransactionFees
{
    /// <summary>
    /// <paramref name="inputsSat"/> minus the transaction's outputs; null (never an exception) when the bytes are not a
    /// transaction or the outputs exceed the inputs.
    /// </summary>
    public static LightningMoney? FromInputs(SignedTransaction? transaction, ulong inputsSat)
    {
        if (transaction?.RawTxBytes is not { Length: > 0 } raw || !ChainTxMapper.TryParse(raw, out var chainTx)
                                                                || chainTx is null)
            return null;

        ulong outputsSat = 0;
        try
        {
            foreach (var output in chainTx.Outputs)
                outputsSat = checked(outputsSat + output.AmountSat);
        }
        catch (OverflowException)
        {
            return null;
        }

        return outputsSat > inputsSat ? null : LightningMoney.Satoshis(inputsSat - outputsSat);
    }

    /// <summary>The fee of a commitment that spends the channel's current funding output (its capacity minus the
    /// commitment's outputs); null when the channel has no funding output.</summary>
    public static LightningMoney? ForCommitment(SignedTransaction transaction, ChannelModel channel) =>
        channel.FundingOutput is { } funding ? FromInputs(transaction, funding.Amount.MilliSatoshi / 1_000) : null;
}