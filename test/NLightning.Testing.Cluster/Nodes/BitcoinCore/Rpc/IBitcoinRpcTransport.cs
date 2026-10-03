using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>
/// How a JSON-RPC call reaches bitcoind: over HTTP from wherever the test process runs (<see cref="HttpRpcTransport"/>,
/// pod IP or Service DNS name) or through <c>bitcoin-cli</c> run in the pod (<see cref="ExecCliRpcTransport"/>, which
/// needs nothing but exec rights). Arguments are always named, so optional ones can be left out on both transports.
/// </summary>
public interface IBitcoinRpcTransport
{
    /// <summary>Where calls go, for messages and logs (<c>http://10.0.0.5:18443/wallet/miner</c>, <c>exec ns/miner-0</c>).</summary>
    string Description { get; }

    /// <summary>
    /// Calls <paramref name="method"/> with named arguments (null values are left out) and returns the result (a JSON
    /// null for a method without one).
    /// </summary>
    /// <exception cref="BitcoinRpcException">An RPC error, or no answer.</exception>
    Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                           CancellationToken cancellationToken);
}