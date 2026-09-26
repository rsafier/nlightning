using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

public interface IBitcoinChainService
{
    Task<uint256> SendTransactionAsync(Transaction transaction);
    Task<Transaction?> GetTransactionAsync(uint256 txId);
    Task<uint> GetCurrentBlockHeightAsync();
    Task<Block?> GetBlockAsync(uint height);

    /// <summary>The hash of the active chain's block at <paramref name="height"/>.</summary>
    Task<uint256> GetBlockHashAsync(uint height);
    Task<uint> GetTransactionConfirmationsAsync(uint256 txId);

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
}