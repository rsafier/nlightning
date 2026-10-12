using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

using Chain;

/// <summary>
/// The typed bitcoind RPC calls the chain helpers and the node adapters use. Hashes and txids are display hex, amounts
/// satoshis, fee rates sat/vB. <see cref="CallAsync"/> reaches anything else.
/// </summary>
public interface IBitcoinCoreRpc
{
    /// <summary>Where calls go (<see cref="IBitcoinRpcTransport.Description"/>).</summary>
    string Description { get; }

    /// <summary>Any RPC with named arguments (null values are left out).</summary>
    Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                           CancellationToken cancellationToken);

    Task<ChainInfo> GetChainInfoAsync(CancellationToken cancellationToken);

    /// <summary>The active tip (height and hash from one <c>getblockchaininfo</c>, so they match).</summary>
    Task<ChainTip> GetTipAsync(CancellationToken cancellationToken);

    Task<string> GetBlockHashAsync(long height, CancellationToken cancellationToken);

    Task<BlockHeaderInfo> GetBlockHeaderAsync(string blockHash, CancellationToken cancellationToken);

    /// <summary>Mines <paramref name="blocks"/> blocks (with the mempool's transactions) to <paramref name="address"/>.</summary>
    Task<IReadOnlyList<string>> GenerateToAddressAsync(int blocks, string address,
                                                       CancellationToken cancellationToken);

    /// <summary>
    /// Mines one block with exactly <paramref name="transactions"/> (txids from the mempool or raw hex; empty for an
    /// empty block) and returns its hash.
    /// </summary>
    Task<string> GenerateBlockAsync(string address, IReadOnlyList<string> transactions,
                                    CancellationToken cancellationToken);

    /// <summary>A new address of the wallet.</summary>
    Task<string> GetNewAddressAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends from the wallet and returns the txid; <paramref name="feeRateSatPerVb"/> pins the fee rate, null takes the
    /// wallet's (<c>settxfee</c>, the estimate or <c>-fallbackfee</c>).
    /// </summary>
    Task<string> SendToAddressAsync(string address, long amountSat, decimal? feeRateSatPerVb,
                                    CancellationToken cancellationToken);

    /// <summary>One transaction paying every address its amount (a fan-out); returns the txid.</summary>
    Task<string> SendManyAsync(IReadOnlyDictionary<string, long> amountsSat, decimal? feeRateSatPerVb,
                               CancellationToken cancellationToken);

    /// <summary>The wallet's confirmed (trusted) balance.</summary>
    Task<long> GetTrustedBalanceSatAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRawMempoolAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Where the transaction is (needs <c>-txindex</c> for one that is not the wallet's and no longer in the mempool).
    /// </summary>
    Task<TxStatus> GetTransactionStatusAsync(string txId, CancellationToken cancellationToken);

    Task InvalidateBlockAsync(string blockHash, CancellationToken cancellationToken);

    Task ReconsiderBlockAsync(string blockHash, CancellationToken cancellationToken);

    /// <summary>
    /// The wallet's fee rate (<c>settxfee</c>; 0 goes back to estimates and the fallback fee). Bitcoin Core 31 removed
    /// it (<see cref="BitcoinRpcErrorCodes.MethodNotFound"/>).
    /// </summary>
    Task SetTxFeeAsync(decimal satPerVb, CancellationToken cancellationToken);

    /// <summary><c>estimatesmartfee</c>, or null while bitcoind has no estimate (regtest until it is seeded).</summary>
    Task<FeeEstimate?> EstimateSmartFeeAsync(int confirmationTarget, FeeEstimateMode mode,
                                             CancellationToken cancellationToken);

    Task<int> GetConnectionCountAsync(CancellationToken cancellationToken);

    /// <summary><c>addnode</c> (<paramref name="command"/>: <c>add</c>, <c>remove</c> or <c>onetry</c>).</summary>
    Task AddNodeAsync(string node, string command, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="wallet"/> (loaded at every start) or loads it when it exists; nothing when it is loaded.
    /// </summary>
    Task EnsureWalletAsync(string wallet, CancellationToken cancellationToken);
}