namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>
/// A bitcoind RPC call failed: an RPC error (with its <see cref="Code"/>) or no answer at all (<see cref="Code"/> null,
/// e.g. the server is not reachable from where the call was made).
/// </summary>
public sealed class BitcoinRpcException(string method, int? code, string message, Exception? innerException = null)
    : Exception(code is null ? $"{method}: {message}" : $"{method}: error {code}: {message}", innerException)
{
    /// <summary>The RPC method that failed.</summary>
    public string Method { get; } = method;

    /// <summary>bitcoind's RPC error code (<see cref="BitcoinRpcErrorCodes"/>), or null when there was no RPC answer.</summary>
    public int? Code { get; } = code;

    /// <summary>bitcoind's error message, or the transport's.</summary>
    public string RpcMessage { get; } = message;
}

/// <summary>
/// The bitcoind RPC error codes the harness handles (<c>src/rpc/protocol.h</c>).
/// </summary>
public static class BitcoinRpcErrorCodes
{
    /// <summary><c>RPC_WALLET_ERROR</c> (e.g. <c>createwallet</c> of a wallet that exists).</summary>
    public const int WalletError = -4;

    /// <summary><c>RPC_INVALID_ADDRESS_OR_KEY</c>: also "no such transaction / block".</summary>
    public const int InvalidAddressOrKey = -5;

    /// <summary><c>RPC_WALLET_NOT_FOUND</c>.</summary>
    public const int WalletNotFound = -18;

    /// <summary><c>RPC_IN_WARMUP</c>: the node is still starting.</summary>
    public const int InWarmup = -28;

    /// <summary><c>RPC_WALLET_ALREADY_LOADED</c>.</summary>
    public const int WalletAlreadyLoaded = -35;

    /// <summary><c>RPC_METHOD_NOT_FOUND</c> (e.g. <c>settxfee</c> on Bitcoin Core 31).</summary>
    public const int MethodNotFound = -32601;
}