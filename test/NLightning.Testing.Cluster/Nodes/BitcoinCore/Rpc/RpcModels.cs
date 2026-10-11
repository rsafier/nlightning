namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>What <c>getblockchaininfo</c> says the harness needs.</summary>
/// <param name="Chain">The chain's name (<c>regtest</c>).</param>
/// <param name="Blocks">The active chain's height.</param>
/// <param name="Headers">The best header's height.</param>
/// <param name="BestBlockHash">The tip's hash.</param>
/// <param name="InitialBlockDownload">Whether the node thinks it is still syncing.</param>
public sealed record ChainInfo(string Chain, long Blocks, long Headers, string BestBlockHash,
                               bool InitialBlockDownload);

/// <summary>A block header as <c>getblockheader</c> gives it.</summary>
/// <param name="Hash">The block hash.</param>
/// <param name="Height">Its height.</param>
/// <param name="Confirmations">Confirmations, or -1 when the block is not in the active chain.</param>
/// <param name="PreviousHash">The parent's hash, or null for the genesis block.</param>
public sealed record BlockHeaderInfo(string Hash, long Height, long Confirmations, string? PreviousHash)
{
    public bool InActiveChain => Confirmations >= 1;
}

/// <summary>Where a transaction is.</summary>
public enum TxState
{
    /// <summary>Neither in the mempool nor in an active-chain block (unknown, evicted, or in a stale block only).</summary>
    NotFound,
    InMempool,
    Confirmed
}

/// <summary>A transaction's place: the mempool, an active-chain block, or nowhere.</summary>
/// <param name="TxId">The transaction id (display hex).</param>
/// <param name="State">Where it is.</param>
/// <param name="BlockHash">The active-chain block holding it, when <see cref="TxState.Confirmed"/>.</param>
/// <param name="BlockHeight">That block's height.</param>
/// <param name="Confirmations">Its confirmations (0 unless confirmed).</param>
public sealed record TxStatus(string TxId, TxState State, string? BlockHash = null, long? BlockHeight = null,
                              long Confirmations = 0)
{
    public static TxStatus NotFound(string txId) => new(txId, TxState.NotFound);

    public static TxStatus Mempool(string txId) => new(txId, TxState.InMempool);
}

/// <summary><c>estimatesmartfee</c>'s modes.</summary>
public enum FeeEstimateMode
{
    Economical,
    Conservative
}

/// <summary>A fee estimate in sat/vB and the target it holds for.</summary>
public sealed record FeeEstimate(decimal SatPerVb, int Blocks);