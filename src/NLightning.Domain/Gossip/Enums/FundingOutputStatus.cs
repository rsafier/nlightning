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

    /// <summary>
    /// The SCID's block height is above our chain tip (bitcoind still syncing or behind the peer); transient, retry
    /// once the tip reaches the height (an LND "premature" announcement). Never proof of an invalid announcement.
    /// </summary>
    BlockNotFound = 1,

    /// <summary>
    /// The block exists but its data is not available (pruned node); the caller decides
    /// (<c>Gossip:FundingValidation</c>).
    /// </summary>
    BlockUnavailable = 2,

    /// <summary>The SCID's transaction index is not in the block.</summary>
    TransactionIndexOutOfRange = 3,

    /// <summary>No unspent output at that index on chain (spent in a block, or no such output).</summary>
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
    ChainUnavailable = 8,

    /// <summary>
    /// The output is confirmed and unspent on chain, but a mempool transaction spends it (a close not yet mined);
    /// transient: BOLT 7 ignores an announcement whose output is spent, which is spent on chain, and a mempool spend
    /// can still be replaced or evicted. Never proof of an invalid announcement.
    /// </summary>
    OutputSpentInMempool = 9
}