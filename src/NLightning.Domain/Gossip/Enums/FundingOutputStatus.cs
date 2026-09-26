namespace NLightning.Domain.Gossip.Enums;

/// <summary>
/// The outcome of a short channel id → funding output lookup (BOLT 7 plan §3.4, D3).
/// </summary>
public enum FundingOutputStatus : byte
{
    /// <summary>
    /// The output exists, is confirmed at the SCID's height and unspent (and, when verified, is the P2WSH 2-of-2 of
    /// the two bitcoin keys with the expected amount).
    /// </summary>
    Found = 0,

    /// <summary>The SCID's block height is above the chain tip.</summary>
    BlockNotFound = 1,

    /// <summary>
    /// The block exists but its data is not available (pruned node); the caller decides
    /// (<c>Gossip:FundingValidation</c>).
    /// </summary>
    BlockUnavailable = 2,

    /// <summary>The SCID's transaction index is not in the block.</summary>
    TransactionIndexOutOfRange = 3,

    /// <summary>No unspent output at that index (spent, spent in the mempool, or no such output).</summary>
    OutputSpentOrMissing = 4,

    /// <summary>The output's script is not the P2WSH 2-of-2 of the announced bitcoin keys, or a key is invalid.</summary>
    ScriptMismatch = 5,

    /// <summary>The output's amount is zero or not the expected amount.</summary>
    AmountMismatch = 6,

    /// <summary>
    /// The chain moved during the lookup (the output was reported at another height: a reorg); transient, retry later.
    /// </summary>
    ChainMoved = 7,

    /// <summary>
    /// bitcoind could not be asked (connection or RPC failure); transient, retry later. Never a reason to treat the
    /// channel as invalid.
    /// </summary>
    ChainUnavailable = 8
}