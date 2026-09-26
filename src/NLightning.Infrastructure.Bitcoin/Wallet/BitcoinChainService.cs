using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Node.Options;
using Interfaces;
using Networks;
using Options;

public class BitcoinChainService : IBitcoinChainService
{
    private readonly RPCClient _rpcClient;
    private readonly ILogger<BitcoinChainService> _logger;

    public BitcoinChainService(IOptions<BitcoinOptions> bitcoinOptions, ILogger<BitcoinChainService> logger,
                               IOptions<NodeOptions> nodeOptions)
    {
        _logger = logger;
        // Fails on an unknown network instead of talking to bitcoind as if it were mainnet
        var network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();

        var rpcCredentials = new RPCCredentialString
        {
            UserPassword = new NetworkCredential(bitcoinOptions.Value.RpcUser, bitcoinOptions.Value.RpcPassword)
        };

        _rpcClient = new RPCClient(rpcCredentials, bitcoinOptions.Value.RpcEndpoint, network);
        _rpcClient.GetBlockchainInfo();
    }

    public async Task<uint256> SendTransactionAsync(Transaction transaction)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Broadcasting transaction {TxId}", transaction.GetHash());

            var result = await _rpcClient.SendRawTransactionAsync(transaction);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Successfully broadcast transaction {TxId}", result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast transaction {TxId}", transaction.GetHash());
            throw;
        }
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId)
    {
        try
        {
            return await _rpcClient.GetRawTransactionAsync(new uint256(txId), false);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null; // Transaction not found
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get transaction {TxId}", txId);
            throw;
        }
    }

    public async Task<uint> GetCurrentBlockHeightAsync()
    {
        try
        {
            var blockCount = await _rpcClient.GetBlockCountAsync();
            return (uint)blockCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get current block height");
            throw;
        }
    }

    public async Task<Block?> GetBlockAsync(uint height)
    {
        try
        {
            var blockHash = await _rpcClient.GetBlockHashAsync((int)height);
            return await _rpcClient.GetBlockAsync(blockHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get block at height {Height}", height);
            throw;
        }
    }

    public async Task<uint256> GetBlockHashAsync(uint height)
    {
        try
        {
            return await _rpcClient.GetBlockHashAsync((int)height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the hash of block {Height}", height);
            throw;
        }
    }

    public async Task<Block?> GetBlockAsync(uint256 blockHash)
    {
        try
        {
            return await _rpcClient.GetBlockAsync(blockHash);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null; // Block not found
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get block {BlockHash}", blockHash);
            throw;
        }
    }

    public async Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint)
    {
        try
        {
            var response = await _rpcClient.GetTxOutAsync(outPoint.Hash, (int)outPoint.N, true);
            if (response is null || response.Confirmations <= 0)
                return null;

            var tip = await _rpcClient.GetBlockCountAsync();
            return (response.TxOut, (uint)(tip - response.Confirmations + 1));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the unspent output {OutPoint}", outPoint);
            throw;
        }
    }

    public async Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetBlockTxIdsAsync(uint height)
    {
        uint256 blockHash;
        try
        {
            blockHash = await _rpcClient.GetBlockHashAsync((int)height);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_PARAMETER)
        {
            return null; // "Block height out of range"
        }

        try
        {
            // Verbosity 1: the header fields plus the txids (about 64 hex characters per transaction)
            var response = await _rpcClient.SendCommandAsync(RPCOperations.getblock, blockHash.ToString(), 1);
            if (response.Result?["tx"] is not JArray txs)
                throw new InvalidOperationException($"getblock {blockHash} 1 returned no tx array");

            var txIds = new List<uint256>(txs.Count);
            foreach (var tx in txs)
                txIds.Add(uint256.Parse(tx.Value<string>()
                                     ?? throw new InvalidOperationException($"getblock {blockHash} 1: null txid")));

            return (blockHash, txIds);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_MISC_ERROR)
        {
            // "Block not available (pruned data)"
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Block {Height} ({BlockHash}) is not available: {Message}", height, blockHash,
                                 ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the transaction ids of block {Height}", height);
            throw;
        }
    }

    public async Task<uint> GetTransactionConfirmationsAsync(uint256 txId)
    {
        try
        {
            var txInfo = await _rpcClient.GetRawTransactionInfoAsync(new uint256(txId));
            return txInfo.Confirmations;
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return 0; // Transaction not found
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get confirmations for transaction {TxId}", txId);
            throw;
        }
    }
}