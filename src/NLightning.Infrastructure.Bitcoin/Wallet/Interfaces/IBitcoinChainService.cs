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
    /// The output at <paramref name="outPoint"/> if it is unspent in the active chain (mempool spends ignored), with the
    /// height of the block that holds it (<c>gettxout</c> without the mempool; NL-293); null otherwise. The default
    /// knows no output.
    /// </summary>
    Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) =>
        Task.FromResult<(TxOut Output, uint Height)?>(null);
}