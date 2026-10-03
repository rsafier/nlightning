using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>
/// JSON-RPC through <c>bitcoin-cli</c> run in the node's own container (Kubernetes exec). It needs no route from the test
/// process to the pod, so it works wherever the run's namespace can be exec'd into; each call is one exec (tens of
/// milliseconds on OrbStack), so prefer <see cref="HttpRpcTransport"/> when the pod is routable.
/// </summary>
public sealed class ExecCliRpcTransport : IBitcoinRpcTransport
{
    private readonly INodeHandle _node;
    private readonly int _rpcPort;
    private readonly string _user;
    private readonly string _password;
    private readonly string? _wallet;

    public ExecCliRpcTransport(INodeHandle node, int rpcPort, string user, string password, string? wallet)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _rpcPort = rpcPort;
        _user = user;
        _password = password;
        _wallet = wallet;
    }

    public string Description =>
        $"exec {_node.Namespace}/{_node.PodName}{(_wallet is null ? string.Empty : $" -rpcwallet={_wallet}")}";

    public async Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                                        CancellationToken cancellationToken)
    {
        var command = BitcoinCli.BuildCommand("regtest", _rpcPort, _user, _password, _wallet, method, namedArgs);
        var result = await _node.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        return BitcoinCli.ParseResult(method, result);
    }
}