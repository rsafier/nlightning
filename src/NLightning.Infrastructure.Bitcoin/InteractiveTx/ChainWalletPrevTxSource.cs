using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Wallet.Interfaces;

/// <summary>
/// <see cref="IWalletPrevTxSource"/> over bitcoind: <c>getrawtransaction</c> first (a transaction index, the mempool),
/// then the block at the output's height, which works on a node without <c>-txindex</c> as long as it keeps the block
/// (not pruned).
/// </summary>
public sealed class ChainWalletPrevTxSource : IWalletPrevTxSource
{
    private readonly IBitcoinChainService _bitcoinChainService;
    private readonly ILogger<ChainWalletPrevTxSource> _logger;

    public ChainWalletPrevTxSource(IBitcoinChainService bitcoinChainService,
                                   ILogger<ChainWalletPrevTxSource>? logger = null)
    {
        _bitcoinChainService = bitcoinChainService;
        _logger = logger ?? NullLogger<ChainWalletPrevTxSource>.Instance;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetTransactionAsync(TxId txId, uint blockHeight,
                                                   CancellationToken cancellationToken = default)
    {
        var hash = new uint256((byte[])txId);
        try
        {
            var transaction = await _bitcoinChainService.GetTransactionAsync(hash);
            if (transaction is not null && transaction.GetHash() == hash)
                return transaction.ToBytes();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // No transaction index: read the block instead
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(e, "getrawtransaction {TxId} failed; reading block {Height}", txId, blockHeight);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (blockHeight == 0)
            return null;

        try
        {
            var block = await _bitcoinChainService.GetBlockAsync(blockHeight);
            var transaction = block?.Transactions.FirstOrDefault(t => t.GetHash() == hash);
            return transaction?.ToBytes();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(e, "Could not read block {Height} for transaction {TxId}", blockHeight, txId);

            return null;
        }
    }
}