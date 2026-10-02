using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

using Chain;

/// <summary>
/// <see cref="IBitcoinCoreRpc"/> over an <see cref="IBitcoinRpcTransport"/>, so the same typed calls run over HTTP or
/// through <c>bitcoin-cli</c> in the pod.
/// </summary>
public sealed class BitcoinCoreRpcClient(IBitcoinRpcTransport transport) : IBitcoinCoreRpc
{
    /// <summary>Satoshis per bitcoin.</summary>
    public const decimal SatoshisPerBitcoin = 100_000_000m;

    /// <summary>
    /// sat/vB to BTC/kvB (<c>settxfee</c>, <c>-fallbackfee</c>, <c>estimatesmartfee</c>): 1 sat/vB = 1,000 sat/kvB.
    /// </summary>
    public const decimal BtcPerKvBPerSatPerVb = 0.00001m;

    public IBitcoinRpcTransport Transport { get; } = transport ?? throw new ArgumentNullException(nameof(transport));

    public string Description => Transport.Description;

    public Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                                  CancellationToken cancellationToken) =>
        Transport.CallAsync(method, namedArgs, cancellationToken);

    public async Task<ChainInfo> GetChainInfoAsync(CancellationToken cancellationToken)
    {
        var info = await CallAsync("getblockchaininfo", null, cancellationToken).ConfigureAwait(false);
        return new ChainInfo(Required<string>(info, "chain"), Required<long>(info, "blocks"),
                             Required<long>(info, "headers"), Required<string>(info, "bestblockhash"),
                             info.Value<bool?>("initialblockdownload") ?? false);
    }

    public async Task<ChainTip> GetTipAsync(CancellationToken cancellationToken)
    {
        var info = await GetChainInfoAsync(cancellationToken).ConfigureAwait(false);
        return new ChainTip(info.Blocks, info.BestBlockHash);
    }

    public async Task<string> GetBlockHashAsync(long height, CancellationToken cancellationToken) =>
        (await CallAsync("getblockhash", Args(("height", height)), cancellationToken).ConfigureAwait(false))
       .Value<string>()!;

    public async Task<BlockHeaderInfo> GetBlockHeaderAsync(string blockHash, CancellationToken cancellationToken)
    {
        var header = await CallAsync("getblockheader", Args(("blockhash", blockHash), ("verbose", true)),
                                     cancellationToken).ConfigureAwait(false);
        return new BlockHeaderInfo(Required<string>(header, "hash"), Required<long>(header, "height"),
                                   Required<long>(header, "confirmations"),
                                   header.Value<string?>("previousblockhash"));
    }

    public async Task<IReadOnlyList<string>> GenerateToAddressAsync(int blocks, string address,
                                                                    CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blocks);
        var hashes = await CallAsync("generatetoaddress", Args(("nblocks", blocks), ("address", address)),
                                     cancellationToken).ConfigureAwait(false);
        return hashes.Values<string>().Select(h => h!).ToList();
    }

    public async Task<string> GenerateBlockAsync(string address, IReadOnlyList<string> transactions,
                                                 CancellationToken cancellationToken)
    {
        var result = await CallAsync("generateblock",
                                     Args(("output", address), ("transactions", new JArray(transactions.ToArray()))),
                                     cancellationToken).ConfigureAwait(false);
        return Required<string>(result, "hash");
    }

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken) =>
        (await CallAsync("getnewaddress", null, cancellationToken).ConfigureAwait(false)).Value<string>()!;

    public async Task<string> SendToAddressAsync(string address, long amountSat, decimal? feeRateSatPerVb,
                                                 CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountSat);
        var txId = await CallAsync("sendtoaddress",
                                   Args(("address", address), ("amount", ToBitcoin(amountSat)),
                                        ("fee_rate", feeRateSatPerVb)), cancellationToken).ConfigureAwait(false);
        return txId.Value<string>()!;
    }

    public async Task<string> SendManyAsync(IReadOnlyDictionary<string, long> amountsSat, decimal? feeRateSatPerVb,
                                            CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(amountsSat.Count);
        var amounts = new JObject();
        foreach (var (address, sat) in amountsSat)
            amounts[address] = ToBitcoin(sat);
        // sendmany's first parameter (dummy) must be "" when given; named calls may leave it out
        var txId = await CallAsync("sendmany", Args(("amounts", amounts), ("fee_rate", feeRateSatPerVb)),
                                   cancellationToken).ConfigureAwait(false);
        return txId.Value<string>()!;
    }

    public async Task<long> GetTrustedBalanceSatAsync(CancellationToken cancellationToken)
    {
        var balances = await CallAsync("getbalances", null, cancellationToken).ConfigureAwait(false);
        var trusted = balances["mine"]?.Value<decimal?>("trusted")
                   ?? throw new BitcoinRpcException("getbalances", null, "no mine.trusted in the answer");
        return ToSatoshis(trusted);
    }

    public async Task<IReadOnlyList<string>> GetRawMempoolAsync(CancellationToken cancellationToken)
    {
        var txIds = await CallAsync("getrawmempool", null, cancellationToken).ConfigureAwait(false);
        return txIds.Values<string>().Select(t => t!).ToList();
    }

    public async Task<TxStatus> GetTransactionStatusAsync(string txId, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("getmempoolentry", Args(("txid", txId)), cancellationToken).ConfigureAwait(false);
            return TxStatus.Mempool(txId);
        }
        catch (BitcoinRpcException e) when (e.Code == BitcoinRpcErrorCodes.InvalidAddressOrKey)
        {
            // Not in the mempool
        }

        JToken transaction;
        try
        {
            transaction = await CallAsync("getrawtransaction", Args(("txid", txId), ("verbose", true)),
                                          cancellationToken).ConfigureAwait(false);
        }
        catch (BitcoinRpcException e) when (e.Code == BitcoinRpcErrorCodes.InvalidAddressOrKey)
        {
            return TxStatus.NotFound(txId);
        }

        // With -txindex a transaction of a disconnected block is still found, with 0 confirmations
        var blockHash = transaction.Value<string?>("blockhash");
        var confirmations = transaction.Value<long?>("confirmations") ?? 0;
        if (blockHash is null)
            return TxStatus.Mempool(txId);
        if (confirmations < 1)
            return TxStatus.NotFound(txId);

        var header = await GetBlockHeaderAsync(blockHash, cancellationToken).ConfigureAwait(false);
        return new TxStatus(txId, TxState.Confirmed, blockHash, header.Height, confirmations);
    }

    public Task InvalidateBlockAsync(string blockHash, CancellationToken cancellationToken) =>
        CallAsync("invalidateblock", Args(("blockhash", blockHash)), cancellationToken);

    public Task ReconsiderBlockAsync(string blockHash, CancellationToken cancellationToken) =>
        CallAsync("reconsiderblock", Args(("blockhash", blockHash)), cancellationToken);

    public Task SetTxFeeAsync(decimal satPerVb, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(satPerVb);
        return CallAsync("settxfee", Args(("amount", satPerVb * BtcPerKvBPerSatPerVb)), cancellationToken);
    }

    public async Task<FeeEstimate?> EstimateSmartFeeAsync(int confirmationTarget, FeeEstimateMode mode,
                                                          CancellationToken cancellationToken)
    {
        var estimate = await CallAsync("estimatesmartfee",
                                       Args(("conf_target", confirmationTarget),
                                            ("estimate_mode", mode == FeeEstimateMode.Economical
                                                                  ? "economical"
                                                                  : "conservative")),
                                       cancellationToken).ConfigureAwait(false);
        var feeRate = estimate.Value<decimal?>("feerate");
        return feeRate is null
                   ? null
                   : new FeeEstimate(feeRate.Value / BtcPerKvBPerSatPerVb,
                                     estimate.Value<int?>("blocks") ?? confirmationTarget);
    }

    public async Task<int> GetConnectionCountAsync(CancellationToken cancellationToken) =>
        (await CallAsync("getconnectioncount", null, cancellationToken).ConfigureAwait(false)).Value<int>();

    public Task AddNodeAsync(string node, string command, CancellationToken cancellationToken) =>
        CallAsync("addnode", Args(("node", node), ("command", command)), cancellationToken);

    public async Task EnsureWalletAsync(string wallet, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("createwallet", Args(("wallet_name", wallet), ("load_on_startup", true)),
                            cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (BitcoinRpcException e) when (e.Code is BitcoinRpcErrorCodes.WalletError
                                                   or BitcoinRpcErrorCodes.WalletAlreadyLoaded)
        {
            // It exists: load it below
        }

        try
        {
            await CallAsync("loadwallet", Args(("filename", wallet), ("load_on_startup", true)), cancellationToken)
               .ConfigureAwait(false);
        }
        catch (BitcoinRpcException e) when (e.Code == BitcoinRpcErrorCodes.WalletAlreadyLoaded)
        {
            // Loaded already
        }
    }

    /// <summary>Satoshis as a BTC amount with 8 decimals.</summary>
    public static decimal ToBitcoin(long satoshis) => decimal.Round(satoshis / SatoshisPerBitcoin, 8);

    /// <summary>A BTC amount in satoshis.</summary>
    public static long ToSatoshis(decimal bitcoin) => (long)decimal.Round(bitcoin * SatoshisPerBitcoin);

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args) =>
        args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

    private static T Required<T>(JToken token, string name) =>
        token[name] is { Type: not JTokenType.Null } value
            ? value.Value<T>()!
            : throw new BitcoinRpcException("?", null, $"no '{name}' in {token.ToString(Newtonsoft.Json.Formatting.None)}");
}