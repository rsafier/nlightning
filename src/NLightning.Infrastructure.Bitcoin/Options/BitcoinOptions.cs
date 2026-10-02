namespace NLightning.Infrastructure.Bitcoin.Options;

/// <summary>
/// The bitcoind connection (configuration section <c>Bitcoin</c>).
/// </summary>
/// <remarks>
/// The members are not <c>required</c>: the configuration binding source generator (on in every build of the src
/// projects, NativeAOT included, NL-338) cannot construct a type with required members, so the daemon checks them at
/// start with <see cref="GetValidationErrors"/> instead. An unset value stays null, as it did with the reflection
/// binder.
/// </remarks>
public class BitcoinOptions
{
    public const string SectionName = "Bitcoin";

    public string? RpcEndpoint { get; set; }
    public string? RpcUser { get; set; }
    public string? RpcPassword { get; set; }
    public string? ZmqHost { get; set; }
    public int ZmqBlockPort { get; set; }

    /// <summary>
    /// bitcoind's ZMQ <c>rawtx</c> port, read when <see cref="WatchMempool"/> is on. 0 (unset) is accepted for
    /// configuration files written before BOLT 5 O8.
    /// </summary>
    public int ZmqTxPort { get; set; }

    /// <summary>
    /// Subscribes to bitcoind's ZMQ <c>rawtx</c> on <see cref="ZmqTxPort"/> to react to unconfirmed spends of watched
    /// outputs (BOLT 5 plan O8: preimages and revoked commitments seen in the mempool). Blocks alone are enough for
    /// correctness, so this only lowers latency. Default true.
    /// </summary>
    public bool WatchMempool { get; set; } = true;

    /// <summary>
    /// The settings the node cannot run without: a usable RPC endpoint (<see cref="IsUsableRpcEndpoint"/>), the RPC user
    /// and password, the ZMQ host and block port, and a ZMQ tx port in range.
    /// </summary>
    /// <returns>One message per problem, empty when the section is usable.</returns>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (!IsUsableRpcEndpoint(RpcEndpoint))
            errors.Add($"{SectionName}:{nameof(RpcEndpoint)} must be an http or https URL or a host[:port] (empty means "
                     + "127.0.0.1 on the network's RPC port).");

        if (string.IsNullOrEmpty(RpcUser))
            errors.Add($"{SectionName}:{nameof(RpcUser)} is required.");

        if (string.IsNullOrEmpty(RpcPassword))
            errors.Add($"{SectionName}:{nameof(RpcPassword)} is required.");

        if (string.IsNullOrWhiteSpace(ZmqHost))
            errors.Add($"{SectionName}:{nameof(ZmqHost)} is required.");

        if (ZmqBlockPort is < 1 or > 65535)
            errors.Add($"{SectionName}:{nameof(ZmqBlockPort)} must be a port between 1 and 65535.");

        if (ZmqTxPort is < 0 or > 65535)
            errors.Add($"{SectionName}:{nameof(ZmqTxPort)} must be a port between 1 and 65535 (0 = unset).");

        return errors;
    }

    /// <summary>
    /// True for what NBitcoin's <c>RPCClient</c> accepts as its endpoint (NL-740: configurations written before the
    /// start-up check keep starting): an absolute <c>http://</c> or <c>https://</c> URL, a scheme-less <c>host</c> or
    /// <c>host:port</c> (the client adds <c>http://</c>), or nothing (127.0.0.1 on the network's default RPC port).
    /// Another scheme, or a value that is no host, is refused.
    /// </summary>
    public static bool IsUsableRpcEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(url.Host);

        return Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var hostPort)
            && !string.IsNullOrEmpty(hostPort.Host);
    }
}