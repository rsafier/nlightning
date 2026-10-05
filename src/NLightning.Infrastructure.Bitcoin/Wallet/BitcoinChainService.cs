using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json;
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
    private readonly string _rpcAuthorization;
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

        // No RPC here: the service must be constructible without a live bitcoind (NL-153; a new key's birth height is
        // the one caller that needs it, and it asks async). Every call fails on its own until bitcoind is reachable.
        _rpcClient = new RPCClient(rpcCredentials, bitcoinOptions.Value.RpcEndpoint, network);
        _rpcAuthorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{bitcoinOptions.Value.RpcUser}:{bitcoinOptions.Value.RpcPassword}"));
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
            // NL-534: bitcoind refusing the transaction (missing or spent inputs, a mempool conflict, a fee too low,
            // already known) is the caller's to report, at the level it knows (the chain monitor's rebroadcast, a
            // funding publish); only an unreachable node or an unexpected RPC failure is an error here
            if (BroadcastRefusalRules.IsNodeRefusal(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("bitcoind refused transaction {TxId}: {Reason}", transaction.GetHash(),
                                     ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Failed to broadcast transaction {TxId}", transaction.GetHash());
            }

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

    public async Task<DateTimeOffset?> GetBlockTimeAsync(uint height)
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

        var response = await _rpcClient.SendCommandAsync("getblockheader", blockHash.ToString(), true);
        return ParseBlockHeaderTime(response.Result);
    }

    /// <summary>The <c>time</c> of a <c>getblockheader &lt;hash&gt; true</c> answer (Unix seconds), null when missing.
    /// </summary>
    internal static DateTimeOffset? ParseBlockHeaderTime(JToken? result) =>
        result?["time"]?.Value<long?>() is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

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
            // Verbosity 1: the header fields plus the txids (about 64 hex characters per transaction). The answer is
            // read with a streaming parser: a 100-400 KB answer parsed as a JToken tree lands on the large object
            // heap, which peaked a verified sync at 630 MB RSS (NL-416)
            var txIds = await GetBlockTxIdsStreamingAsync(blockHash);
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

    /// <summary>
    /// The <c>getblock &lt;hash&gt; 1</c> request, sent through the shared <see cref="RPCClient"/>'s HTTP client with
    /// its URL and credentials, but read with <c>ResponseHeadersRead</c> so the answer is parsed streaming and never
    /// buffered whole.
    /// </summary>
    private async Task<IReadOnlyList<uint256>> GetBlockTxIdsStreamingAsync(uint256 blockHash)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _rpcClient.Address)
        {
            Content = new StringContent(CreateGetBlockRequest(blockHash), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _rpcAuthorization);

        using var response = await _rpcClient.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        return ParseGetBlockAnswer(reader, blockHash, response.StatusCode);
    }

    /// <summary>The <c>getblock &lt;hash&gt; 1</c> request body (JSON-RPC 1.0, like NBitcoin's own requests).</summary>
    internal static string CreateGetBlockRequest(uint256 blockHash) =>
        $$"""{"jsonrpc":"1.0","id":1,"method":"getblock","params":["{{blockHash}}",1]}""";

    /// <summary>
    /// Reads a <c>getblock &lt;hash&gt; 1</c> JSON-RPC answer streaming: of the answer only the entries of the
    /// result's <c>tx</c> array are materialized (NL-416). A JSON-RPC error is thrown as <see cref="RPCException"/>
    /// (bitcoind answers a pruned block with <c>RPC_MISC_ERROR</c>, read as <c>BlockUnavailable</c> by the caller); a
    /// result without a <c>tx</c> array, or a body that is no JSON object, fails.
    /// </summary>
    internal static IReadOnlyList<uint256> ParseGetBlockAnswer(TextReader body, uint256 blockHash,
                                                               HttpStatusCode statusCode)
    {
        using var reader = new JsonTextReader(body);
        if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            throw new InvalidOperationException($"getblock {blockHash} answered no JSON object (HTTP {(int)statusCode})");

        List<uint256>? txIds = null;
        RPCException? error = null;
        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
        {
            switch ((string)reader.Value!)
            {
                case "result":
                    txIds ??= ReadResultTxIds(reader, blockHash, statusCode);
                    break;
                case "error":
                    error = ReadRpcError(reader, blockHash, statusCode);
                    break;
                default:
                    reader.Read();
                    SkipJsonValue(reader);
                    break;
            }
        }

        if (error is not null)
            throw error;
        return txIds ?? throw new InvalidOperationException($"getblock {blockHash} returned no tx array");
    }

    /// <summary>
    /// Reads the <c>result</c> value of the envelope: the <c>tx</c> array of a verbose block, with every other field
    /// skipped. The reader is on the <c>result</c> property name.
    /// </summary>
    private static List<uint256>? ReadResultTxIds(JsonTextReader reader, uint256 blockHash, HttpStatusCode statusCode)
    {
        if (!reader.Read())
            throw new InvalidOperationException($"getblock {blockHash} answered no result (HTTP {(int)statusCode})");
        if (reader.TokenType != JsonToken.StartObject)
        {
            SkipJsonValue(reader); // an error answer carries result null
            return null;
        }

        var txIds = new List<uint256>();
        var sawTx = false;
        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
        {
            if ((string)reader.Value! != "tx")
            {
                reader.Read();
                SkipJsonValue(reader);
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                throw new InvalidOperationException($"getblock {blockHash} 1 returned no tx array");

            sawTx = true;
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType != JsonToken.String || reader.Value is not string txId)
                    throw new InvalidOperationException($"getblock {blockHash} 1: a tx entry is not a txid string");
                txIds.Add(uint256.Parse(txId));
            }
        }

        return sawTx ? txIds
                     : throw new InvalidOperationException($"getblock {blockHash} 1 returned no tx array");
    }

    /// <summary>
    /// Reads the <c>error</c> value of the envelope (the reader is on its property name): null when the answer has no
    /// error, otherwise the <see cref="RPCException"/> to throw.
    /// </summary>
    private static RPCException? ReadRpcError(JsonTextReader reader, uint256 blockHash, HttpStatusCode statusCode)
    {
        if (!reader.Read() || reader.TokenType == JsonToken.Null)
            return null;
        if (reader.TokenType != JsonToken.StartObject)
            throw new InvalidOperationException(
                $"getblock {blockHash} answered an unexpected error value (HTTP {(int)statusCode})");

        var code = RPCErrorCode.RPC_MISC_ERROR;
        string? message = null;
        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
        {
            switch ((string)reader.Value!)
            {
                case "code":
                    if (reader.Read() && reader.TokenType == JsonToken.Integer)
                        code = (RPCErrorCode)Convert.ToInt32(reader.Value);
                    else
                        SkipJsonValue(reader);
                    break;
                case "message":
                    if (reader.Read() && reader.TokenType == JsonToken.String)
                        message = (string)reader.Value!;
                    else
                        SkipJsonValue(reader);
                    break;
                default:
                    reader.Read();
                    SkipJsonValue(reader);
                    break;
            }
        }

        return new RPCException(code, message ?? $"getblock {blockHash} failed (HTTP {(int)statusCode})", null);
    }

    /// <summary>Consumes the value the reader is on (a scalar is already read; a container is read through).</summary>
    private static void SkipJsonValue(JsonTextReader reader)
    {
        if (reader.TokenType is not (JsonToken.StartObject or JsonToken.StartArray))
            return;

        var depth = 1;
        while (depth > 0 && reader.Read())
        {
            if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
                depth++;
            else if (reader.TokenType is JsonToken.EndObject or JsonToken.EndArray)
                depth--;
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
                var effective = ReadDecimal(entry["fees"]?["effective-feerate"]);
                if (effective is { } rate && (highestEffective is null || rate > highestEffective))
                    highestEffective = rate;

                var accepted = error is null || IsAlreadyKnown(error);
                transactions.Add(new PackageTransactionResult(txId, accepted, accepted ? null : error, effective));
            }
        }

        var packageFeerate = ReadDecimal(answer["package-feerate"]) ?? highestEffective;
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
        if (ReadDecimal(result?["mempoolminfee"]) is not { } btcPerKvb || btcPerKvb <= 0)
            return null;

        var satPerKw = decimal.Ceiling(btcPerKvb * 100_000_000m / 4m);
        return satPerKw > uint.MaxValue ? uint.MaxValue : (uint)satPerKw;
    }

    /// <summary>
    /// A JSON number of a bitcoind answer as the decimal it was written as (NL-1087). NBitcoin's RPC client parses
    /// numbers as <see cref="double"/>, and .NET 11 converts a double to decimal exactly, so <c>Value&lt;decimal&gt;()</c>
    /// turned 0.00012 into 0.0001200000000000000030401029; the shortest round-trip text of the double ("1.2E-04") is
    /// what bitcoind sent, so it is parsed instead. Null for a missing or null token.
    /// </summary>
    internal static decimal? ReadDecimal(JToken? token) =>
        token switch
        {
            null => null,
            JValue { Type: JTokenType.Null or JTokenType.Undefined } => null,
            JValue { Value: double d } => decimal.Parse(d.ToString("R", CultureInfo.InvariantCulture),
                                                        NumberStyles.Float, CultureInfo.InvariantCulture),
            JValue { Value: string text } => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => token.Value<decimal?>()
        };

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