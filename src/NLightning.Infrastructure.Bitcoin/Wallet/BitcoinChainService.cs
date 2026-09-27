using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Node.Options;
using Interfaces;
using Models;
using Networks;
using Options;

public class BitcoinChainService : IBitcoinChainService
{
    private const string PackageRelayUnsupportedReason = "bitcoind has no usable submitpackage";

    // Package results that mean bitcoind already has the transaction (mempool or chain)
    private static readonly string[] s_alreadyKnownErrors =
    [
        "txn-already-in-mempool", "txn-already-known", "txn-same-nonwitness-data-in-mempool", "already in block chain"
    ];

    private readonly RPCClient _rpcClient;
    private readonly ILogger<BitcoinChainService> _logger;
    private int _packageRelayUnsupported;

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

    public Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) =>
        GetUnspentOutputAsync(outPoint, true);

    public Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint) =>
        GetUnspentOutputAsync(outPoint, false);

    /// <summary>
    /// True for the <c>getblock</c> error of a pruned block (Bitcoin Core: RPC_MISC_ERROR "Block not available (pruned
    /// data)"). Other RPC_MISC_ERRORs (such as "Block not found on disk") are failures, not a pruned answer.
    /// </summary>
    internal static bool IsPrunedBlockError(RPCErrorCode code, string? message) =>
        code == RPCErrorCode.RPC_MISC_ERROR
     && message?.Contains("pruned", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// <c>gettxout</c>, with the output's height from the answer's own <c>bestblock</c> (NL-413): the height of that
    /// block (<c>getblockheader</c>) minus the confirmations plus one. A separate <c>getblockcount</c> could see a block
    /// connected after <c>gettxout</c> answered and report the output one block too high (the funding output lookup
    /// then answered <c>ChainMoved</c>); the same number of RPCs.
    /// </summary>
    private async Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint, bool includeMempool)
    {
        try
        {
            var response = await _rpcClient.GetTxOutAsync(outPoint.Hash, (int)outPoint.N, includeMempool);
            if (response is null || response.Confirmations <= 0)
                return null;

            var bestHeight = await GetBlockHeightAsync(response.BestBlock);
            return (response.TxOut, (uint)(bestHeight - response.Confirmations + 1));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the unspent output {OutPoint}", outPoint);
            throw;
        }
    }

    /// <summary>The height of <paramref name="blockHash"/> (<c>getblockheader &lt;hash&gt; true</c>), also for a stale
    /// block.</summary>
    private async Task<long> GetBlockHeightAsync(uint256 blockHash)
    {
        var response = await _rpcClient.SendCommandAsync("getblockheader", blockHash.ToString(), true);
        return response.Result?["height"]?.Value<long>()
            ?? throw new InvalidOperationException($"getblockheader {blockHash} returned no height");
    }

    public async Task<(uint256 MerkleRoot, int TxCount)?> GetBlockHeaderSummaryAsync(uint256 blockHash)
    {
        try
        {
            var response = await _rpcClient.SendCommandAsync("getblockheader", blockHash.ToString(), true);
            return ParseBlockHeaderSummary(response.Result);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null; // "Block not found"
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get the header of block {BlockHash}", blockHash);
            throw;
        }
    }

    /// <summary>
    /// <c>merkleroot</c> and <c>nTx</c> of a <c>getblockheader &lt;hash&gt; true</c> answer (<c>nTx</c> missing = 0,
    /// unknown).
    /// </summary>
    internal static (uint256 MerkleRoot, int TxCount) ParseBlockHeaderSummary(JToken? result)
    {
        if (result?["merkleroot"]?.Value<string>() is not { } merkleRoot)
            throw new InvalidOperationException("getblockheader returned no merkleroot");

        var txCount = result["nTx"]?.Value<int>() ?? 0;
        return (uint256.Parse(merkleRoot), txCount);
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
        catch (RPCException ex) when (IsPrunedBlockError(ex.RPCCode, ex.Message))
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

    /// <inheritdoc />
    /// <remarks>
    /// Feature detection: the first answer that says the node has no usable <c>submitpackage</c> (method not found, or
    /// a node that keeps it to regtest) is remembered for the life of the service and logged once; every later call
    /// answers <see cref="PackageSubmitStatus.Unsupported"/> without asking. Core 28+ answers a refused package with
    /// its per-transaction results (<see cref="ParseSubmitPackageResponse"/>); an older node throws a JSON-RPC error,
    /// read as <see cref="PackageSubmitStatus.Rejected"/> with its message.
    /// </remarks>
    public async Task<PackageSubmitResult> SubmitPackageAsync(Transaction parent, Transaction child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        if (Volatile.Read(ref _packageRelayUnsupported) != 0)
            return PackageSubmitResult.Unsupported(PackageRelayUnsupportedReason);

        try
        {
            var response = await _rpcClient.SendCommandAsync(CreateSubmitPackageRequest(parent, child),
                                                             CancellationToken.None);
            var result = ParseSubmitPackageResponse(response.Result);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Package of {ParentTxId} and {ChildTxId}: {Outcome}", parent.GetHash(),
                                       child.GetHash(), result.Describe());
            return result;
        }
        catch (RPCException ex) when (IsPackageRelayUnavailable(ex.RPCCode, ex.Message))
        {
            if (Interlocked.Exchange(ref _packageRelayUnsupported, 1) == 0)
                _logger.LogWarning("bitcoind has no usable submitpackage ({Reason}); a transaction below the mempool "
                                 + "minimum fee cannot get in with its CPFP child (Bitcoin Core 28 or newer is needed "
                                 + "for package relay); transactions are sent one by one", ex.Message);
            return PackageSubmitResult.Unsupported(ex.Message);
        }
        catch (RPCException ex)
        {
            // Core 26/27 throw for a package that fails validation; Core 28 for a malformed or disallowed package
            return new PackageSubmitResult(PackageSubmitStatus.Rejected, ex.Message, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to submit the package of {ParentTxId} and {ChildTxId}", parent.GetHash(),
                             child.GetHash());
            return PackageSubmitResult.Failed(ex.Message);
        }
    }

    /// <summary>The <c>submitpackage</c> request: one parameter, the array of raw transactions, parent first.</summary>
    internal static RPCRequest CreateSubmitPackageRequest(Transaction parent, Transaction child) =>
        new("submitpackage", [new JArray(parent.ToHex(), child.ToHex())]);

    /// <summary>
    /// True for a <c>submitpackage</c> error that means the node cannot take packages at all: the method is unknown
    /// (before Bitcoin Core 24), or it is kept to regtest (Core 24/25: "submitpackage is for regression testing
    /// (-regtest mode) only").
    /// </summary>
    internal static bool IsPackageRelayUnavailable(RPCErrorCode code, string? message) =>
        code == RPCErrorCode.RPC_METHOD_NOT_FOUND
     || (message?.Contains("regression testing", StringComparison.OrdinalIgnoreCase) == true
      && message.Contains("submitpackage", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a <c>submitpackage</c> answer: <c>package_msg</c> (Core 28+, <c>success</c> when every transaction is in
    /// the mempool), <c>tx-results</c> keyed by wtxid (each with <c>txid</c>, and <c>error</c> when refused or
    /// <c>unevaluated</c>, or <c>vsize</c>/<c>fees</c> when accepted or already there; <c>other-wtxid</c> when the same
    /// txid with another witness is in the mempool) and the Core 26/27 <c>package-feerate</c>. A transaction bitcoind
    /// already has (its "already known" rejections) counts as accepted.
    /// </summary>
    internal static PackageSubmitResult ParseSubmitPackageResponse(JToken? result)
    {
        if (result is not JObject answer)
            return new PackageSubmitResult(PackageSubmitStatus.Rejected, "submitpackage returned no result", []);

        var message = answer["package_msg"]?.Value<string>();
        var transactions = new List<PackageTransactionResult>();
        decimal? highestEffective = null;
        if (answer["tx-results"] is JObject txResults)
        {
            foreach (var (wtxid, value) in txResults)
            {
                if (value is not JObject entry)
                    continue;

                var txIdText = entry["txid"]?.Value<string>() ?? wtxid;
                if (!uint256.TryParse(txIdText, out var txId))
                    continue;

                var error = entry["error"]?.Value<string>();
                var effective = entry["fees"]?["effective-feerate"]?.Value<decimal?>();
                if (effective is { } rate && (highestEffective is null || rate > highestEffective))
                    highestEffective = rate;

                var accepted = error is null || IsAlreadyKnown(error);
                transactions.Add(new PackageTransactionResult(txId, accepted, accepted ? null : error, effective));
            }
        }

        var packageFeerate = answer["package-feerate"]?.Value<decimal?>() ?? highestEffective;
        var success = message is null || message.Equals("success", StringComparison.OrdinalIgnoreCase);
        var status = success && transactions.Count > 0 && transactions.All(t => t.Accepted)
                         ? PackageSubmitStatus.Accepted
                         : PackageSubmitStatus.Rejected;
        return new PackageSubmitResult(status, message ?? (status == PackageSubmitStatus.Accepted ? "success" : null),
                                       transactions, packageFeerate);
    }

    private static bool IsAlreadyKnown(string error) =>
        s_alreadyKnownErrors.Any(e => error.Contains(e, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<uint?> GetMempoolMinFeeRatePerKwAsync()
    {
        try
        {
            var response = await _rpcClient.SendCommandAsync("getmempoolinfo");
            return ParseMempoolMinFeeRatePerKw(response.Result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Cannot read bitcoind's mempool minimum fee: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The <c>mempoolminfee</c> (BTC/kvB) of a <c>getmempoolinfo</c> answer in sat/kw, rounded up (1 kvB is 4000
    /// weight units); null when missing or not positive.
    /// </summary>
    internal static uint? ParseMempoolMinFeeRatePerKw(JToken? result)
    {
        if (result?["mempoolminfee"]?.Value<decimal?>() is not { } btcPerKvb || btcPerKvb <= 0)
            return null;

        var satPerKw = decimal.Ceiling(btcPerKvb * 100_000_000m / 4m);
        return satPerKw > uint.MaxValue ? uint.MaxValue : (uint)satPerKw;
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