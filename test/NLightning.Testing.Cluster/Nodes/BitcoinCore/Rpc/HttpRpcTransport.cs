using System.Net;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>
/// JSON-RPC over HTTP with NBitcoin's <see cref="RPCClient"/> (as the Docker fixtures' <c>RegtestBitcoinEndpoint</c>).
/// The host is read on every call, so a pod IP that changed after a restart is picked up.
/// </summary>
public sealed class HttpRpcTransport : IBitcoinRpcTransport
{
    private readonly Func<string> _host;
    private readonly int _port;
    private readonly NetworkCredential _credentials;
    private readonly string? _wallet;
    private readonly Lock _lock = new();
    private string? _clientHost;
    private RPCClient? _client;

    /// <param name="host">The host (IP or DNS name) to call, read on every call.</param>
    /// <param name="port">The RPC port.</param>
    /// <param name="credentials">The <c>-rpcuser</c>/<c>-rpcpassword</c> pair.</param>
    /// <param name="wallet">The wallet calls go to (<c>/wallet/&lt;name&gt;</c>), or null for the node's endpoint.</param>
    public HttpRpcTransport(Func<string> host, int port, NetworkCredential credentials, string? wallet)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _wallet = wallet;
    }

    public string Description => $"{BuildUri(_host(), _port)}{(_wallet is null ? string.Empty : $"/wallet/{_wallet}")}";

    /// <summary>An NBitcoin client on the current host (wallet context applied), for code that wants NBitcoin's API.</summary>
    public RPCClient GetClient()
    {
        var host = _host();
        lock (_lock)
        {
            if (_client is not null && _clientHost == host)
                return _client;

            var client = new RPCClient($"{_credentials.UserName}:{_credentials.Password}", BuildUri(host, _port),
                                       Network.RegTest);
            _client = _wallet is null ? client : client.SetWalletContext(_wallet);
            _clientHost = host;
            return _client;
        }
    }

    public async Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                                        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in namedArgs ?? new Dictionary<string, object?>())
        {
            if (value is not null)
                args[name] = value;
        }

        try
        {
            var response = await GetClient().SendCommandWithNamedArgsAsync(method, args, cancellationToken)
                                            .ConfigureAwait(false);
            return response.Result ?? JValue.CreateNull();
        }
        catch (RPCException e)
        {
            throw new BitcoinRpcException(method, (int)e.RPCCode, e.Message, e);
        }
        catch (HttpRequestException e)
        {
            throw new BitcoinRpcException(method, null, $"{Description} not reachable: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BitcoinRpcException(method, null, $"{Description} timed out", e);
        }
    }

    /// <summary><c>http://host:port</c>, with an IPv6 address in brackets.</summary>
    internal static string BuildUri(string host, int port) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"http://[{host}]:{port}"
            : $"http://{host}:{port}";
}