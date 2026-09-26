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
}