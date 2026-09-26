namespace NLightning.Domain.Gossip.Models;

using Bitcoin.ValueObjects;
using Enums;
using Money;

/// <summary>
/// The result of an <c>IFundingOutputLookup</c> call. <see cref="TransactionId"/>, <see cref="Amount"/>,
/// <see cref="ScriptPubKey"/> and <see cref="Confirmations"/> are set only when the output was found (also for
/// <see cref="FundingOutputStatus.ScriptMismatch"/> and <see cref="FundingOutputStatus.AmountMismatch"/>).
/// </summary>
public sealed class FundingOutputLookupResult
{
    public FundingOutputStatus Status { get; }

    /// <summary>The funding transaction id (the txid at the SCID's block height and index).</summary>
    public TxId? TransactionId { get; }

    /// <summary>The output amount: the channel capacity.</summary>
    public LightningMoney? Amount { get; }

    /// <summary>The output's scriptPubKey bytes.</summary>
    public byte[]? ScriptPubKey { get; }

    /// <summary>Confirmations of the funding block at the tip read during the lookup (tip − height + 1).</summary>
    public uint Confirmations { get; }

    /// <summary>True for <see cref="FundingOutputStatus.Found"/>.</summary>
    public bool IsFound => Status == FundingOutputStatus.Found;

    /// <summary>
    /// True for outcomes that may change without the announcement changing (bitcoind unavailable or behind the SCID's
    /// height, the chain moving, a spend still in the mempool): retry later, never score the announcement as invalid.
    /// </summary>
    public bool IsTransient => Status is FundingOutputStatus.BlockNotFound or FundingOutputStatus.ChainMoved
                                      or FundingOutputStatus.ChainUnavailable
                                      or FundingOutputStatus.OutputSpentInMempool;

    private FundingOutputLookupResult(FundingOutputStatus status, TxId? transactionId, LightningMoney? amount,
                                      byte[]? scriptPubKey, uint confirmations)
    {
        Status = status;
        TransactionId = transactionId;
        Amount = amount;
        ScriptPubKey = scriptPubKey;
        Confirmations = confirmations;
    }

    /// <summary>A result without an output.</summary>
    public static FundingOutputLookupResult Failed(FundingOutputStatus status)
    {
        if (status == FundingOutputStatus.Found)
            throw new ArgumentException("A found output needs its details", nameof(status));

        return new FundingOutputLookupResult(status, null, null, null, 0);
    }

    /// <summary>A result with the output's details.</summary>
    public static FundingOutputLookupResult WithOutput(FundingOutputStatus status, TxId transactionId,
                                                       LightningMoney amount, byte[] scriptPubKey,
                                                       uint confirmations)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(scriptPubKey);
        return new FundingOutputLookupResult(status, transactionId, amount, scriptPubKey, confirmations);
    }
}