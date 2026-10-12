using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Models;

public interface IBitcoinChainService
{
    Task<uint256> SendTransactionAsync(Transaction transaction);
    Task<uint> GetIncrementalRelayFeeRatePerKwAsync() => Task.FromResult(250u);
    Task<IReadOnlyList<uint256>> GetMempoolTransactionIdsAsync() => Task.FromResult<IReadOnlyList<uint256>>([]);
    Task<WalletMempoolEntry?> GetMempoolEntryAsync(uint256 txId) => Task.FromResult<WalletMempoolEntry?>(null);
    /// <summary>Unconfirmed and unspent with mempool spends included; confirmed coins are not returned.</summary>
    Task<TxOut?> GetMempoolUnspentOutputAsync(OutPoint outpoint) => Task.FromResult<TxOut?>(null);
    Task<Transaction?> GetTransactionAsync(uint256 txId);
    Task<uint> GetCurrentBlockHeightAsync();
    /// <summary>Lowest block whose full data is retained (Core pruneheight, or zero on an unpruned node).</summary>
    Task<uint> GetBlockDataStartHeightAsync() => Task.FromResult(0u);
    Task<Block?> GetBlockAsync(uint height);

    /// <summary>The hash of the active chain's block at <paramref name="height"/>.</summary>
    Task<uint256> GetBlockHashAsync(uint height);
    Task<uint> GetTransactionConfirmationsAsync(uint256 txId);

    /// <summary>
    /// Sends <paramref name="parent"/> and its <paramref name="child"/> as one package (<c>submitpackage</c>, Bitcoin
    /// Core 28+ one-parent-one-child package relay; NL-380): the pair is judged at its package feerate, so a parent
    /// below the mempool minimum fee (a commitment signed before a fee spike) gets in with a child paying for both.
    /// Never throws: a node without a usable <c>submitpackage</c> answers <see cref="PackageSubmitStatus.Unsupported"/>
    /// (send the transactions one by one), a call that fails <see cref="PackageSubmitStatus.Failed"/>. The default
    /// supports no package.
    /// </summary>
    Task<PackageSubmitResult> SubmitPackageAsync(Transaction parent, Transaction child) =>
        Task.FromResult(PackageSubmitResult.Unsupported("this chain service has no package relay"));

    /// <summary>
    /// bitcoind's <c>submitpackage</c> of <paramref name="transactions"/> as given (parents first, the child last) with
    /// its optional <c>maxfeerate</c> in BTC/kvB (0 = no limit), answered as bitcoind does: the package message, each
    /// transaction's result by wtxid and the transactions package RBF replaced (LND's walletrpc <c>SubmitPackage</c>,
    /// NL-1186). bitcoind's JSON-RPC errors are thrown (<c>RPCException</c>). The default supports no package.
    /// </summary>
    /// <exception cref="NotSupportedException">This chain service has no <c>submitpackage</c>.</exception>
    Task<RawPackageSubmitResult> SubmitRawPackageAsync(IReadOnlyList<Transaction> transactions,
                                                       decimal? maxFeeRateBtcPerKvb) =>
        throw new NotSupportedException("this chain service has no package relay");

    /// <summary>
    /// bitcoind's current mempool minimum feerate (<c>getmempoolinfo</c> <c>mempoolminfee</c>, BTC/kvB) in sat per
    /// 1000 weight units, rounded up: a transaction (or package) paying less is refused. Null when unknown; the default
    /// knows none.
    /// </summary>
    Task<uint?> GetMempoolMinFeeRatePerKwAsync() => Task.FromResult<uint?>(null);

    /// <summary>
    /// The mempool transactions spending each of <paramref name="outPoints"/> (<c>gettxspendingprevout</c>, Bitcoin
    /// Core 24+ and rbitcoin; NL-1094): an entry per outpoint a mempool transaction spends, none for an unspent one or
    /// one spent only on chain. Null when the node has no such call; the default knows none.
    /// </summary>
    Task<IReadOnlyDictionary<OutPoint, uint256>?> GetMempoolSpendersAsync(IReadOnlyCollection<OutPoint> outPoints) =>
        Task.FromResult<IReadOnlyDictionary<OutPoint, uint256>?>(null);

    /// <summary>
    /// The block with <paramref name="blockHash"/>, also when it is no longer in the active chain (a disconnected
    /// block, whose wallet effects a reorg rolls back, NL-293); null when unknown. The default knows no block.
    /// </summary>
    Task<Block?> GetBlockAsync(uint256 blockHash) => Task.FromResult<Block?>(null);

    /// <summary>
    /// The output at <paramref name="outPoint"/> if it is confirmed and unspent in the active chain and no mempool
    /// transaction spends it, with the height of the block that holds it (<c>gettxout</c> with the mempool; NL-293);
    /// null otherwise. The default knows no output.
    /// </summary>
    Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) =>
        Task.FromResult<(TxOut Output, uint Height)?>(null);

    /// <summary>
    /// The output at <paramref name="outPoint"/> if it is confirmed and unspent in the active chain, ignoring mempool
    /// spends (<c>gettxout</c> without the mempool), with the height of the block that holds it; null otherwise. Tells
    /// a mempool spend from an on-chain one. The default is <see cref="GetUnspentOutputAsync"/>.
    /// </summary>
    Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint) =>
        GetUnspentOutputAsync(outPoint);

    /// <summary>
    /// The merkle root and the transaction count of the block with <paramref name="blockHash"/>
    /// (<c>getblockheader &lt;hash&gt; true</c>: <c>merkleroot</c>, <c>nTx</c>). A pruned node keeps every header, so this
    /// answers where <see cref="GetBlockTxIdsAsync"/> cannot; it lets the funding output lookup prove a txid an
    /// untrusted index names (BOLT 7 plan D12, Esplora source). <c>TxCount</c> is 0 when the node does not know it (a
    /// block it never downloaded, e.g. below an assumeutxo snapshot). Null when the block is unknown. The default reads
    /// the whole block through <see cref="GetBlockAsync(uint256)"/>.
    /// </summary>
    async Task<(uint256 MerkleRoot, int TxCount)?> GetBlockHeaderSummaryAsync(uint256 blockHash)
    {
        var block = await GetBlockAsync(blockHash);
        return block is null ? null : (block.Header.HashMerkleRoot, block.Transactions.Count);
    }

    /// <summary>
    /// The hash and the transaction ids, in block order, of the active chain's block at <paramref name="height"/>
    /// (<c>getblockhash</c> + <c>getblock &lt;hash&gt; 1</c>: txids only, no txindex needed; BOLT 7 plan §3.4). Null
    /// when the height is above the tip or the block's data is not available (a pruned node). The default reads the
    /// whole block through <see cref="GetBlockAsync(uint)"/>.
    /// </summary>
    async Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetBlockTxIdsAsync(uint height)
    {
        if (height > await GetCurrentBlockHeightAsync())
            return null;

        var block = await GetBlockAsync(height);
        if (block is null)
            return null;

        return (block.GetHash(), block.Transactions.Select(t => t.GetHash()).ToList());
    }

    /// <summary>
    /// The header timestamp of the active chain's block at <paramref name="height"/> (<c>getblockhash</c> +
    /// <c>getblockheader</c>; a pruned node keeps every header), or null when the height is above the tip (NL-623, the
    /// accounting channel report's open time of a channel funded before the feed began). The default reads the whole
    /// block through <see cref="GetBlockAsync(uint)"/>.
    /// </summary>
    async Task<DateTimeOffset?> GetBlockTimeAsync(uint height)
    {
        if (height > await GetCurrentBlockHeightAsync())
            return null;

        return (await GetBlockAsync(height))?.Header.BlockTime;
    }
}