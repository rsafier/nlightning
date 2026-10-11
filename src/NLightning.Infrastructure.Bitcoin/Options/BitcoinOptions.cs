namespace NLightning.Infrastructure.Bitcoin.Options;

using Domain.Protocol.ValueObjects;

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

    /// <summary>
    /// How the chain monitor learns about new blocks (NL-1094): <see cref="ChainNotificationMode.Zmq"/> (default) or
    /// <see cref="ChainNotificationMode.Poll"/> (RPC only; <see cref="ZmqHost"/> and the ZMQ ports are not needed).
    /// </summary>
    public ChainNotificationMode Notifications { get; set; } = ChainNotificationMode.Zmq;

    /// <summary>Required with <see cref="ChainNotificationMode.Zmq"/>, ignored with <see cref="ChainNotificationMode.Poll"/>.</summary>
    public string? ZmqHost { get; set; }

    /// <summary>Required with <see cref="ChainNotificationMode.Zmq"/>, ignored with <see cref="ChainNotificationMode.Poll"/>.</summary>
    public int ZmqBlockPort { get; set; }

    /// <summary>
    /// bitcoind's ZMQ <c>rawtx</c> port, read when <see cref="WatchMempool"/> is on. 0 (unset) is accepted for
    /// configuration files written before BOLT 5 O8.
    /// </summary>
    public int ZmqTxPort { get; set; }

    /// <summary>
    /// Watches the mempool for unconfirmed spends of watched outputs (BOLT 5 plan O8: preimages and revoked commitments
    /// seen in the mempool). Blocks alone are enough for correctness, so this only lowers latency. With
    /// <see cref="ChainNotificationMode.Zmq"/> it subscribes to bitcoind's ZMQ <c>rawtx</c> on <see cref="ZmqTxPort"/>;
    /// with <see cref="ChainNotificationMode.Poll"/> it asks <c>gettxspendingprevout</c> (Bitcoin Core 24+, rbitcoin)
    /// for the watched outputs on every poll (NL-1094). Unset (null) means on with ZMQ and off with polling
    /// (<see cref="IsMempoolWatched"/>).
    /// </summary>
    public bool? WatchMempool { get; set; }

    /// <summary>
    /// How often <see cref="ChainNotificationMode.Poll"/> reads bitcoind's tip (and, with <see cref="WatchMempool"/>,
    /// the mempool spends of the watched outputs). Unset (null) means <see cref="DefaultPollInterval"/> for the network:
    /// 2 s on regtest, 5 s on the test networks (Mutinynet mines every 30 s), 10 s on mainnet. Between
    /// <see cref="MinPollInterval"/> and <see cref="MaxPollInterval"/>. Ignored with <see cref="ChainNotificationMode.Zmq"/>.
    /// </summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>The shortest <see cref="PollInterval"/> accepted.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest <see cref="PollInterval"/> accepted (a tenth of a mainnet block interval).</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often the chain monitor asks bitcoind for its tip over RPC, besides following ZMQ <c>rawblock</c>. A ZMQ
    /// subscriber gets only what is published after its subscription reached bitcoind, so a block mined between the
    /// start's RPC catch-up and the subscription, or while the ZMQ connection is down or being set up again, is never
    /// announced; when two polls in a row find the monitor behind the tip without it moving, it catches up over RPC
    /// and logs a warning. Default 30 s; zero turns the poll off (a lost block then waits for the next one).
    /// </summary>
    public TimeSpan TipPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>True when the mempool is watched: <see cref="WatchMempool"/>, or by default with ZMQ only.</summary>
    public bool IsMempoolWatched => WatchMempool ?? Notifications == ChainNotificationMode.Zmq;

    /// <summary>
    /// The poll interval <see cref="ChainNotificationMode.Poll"/> uses on <paramref name="network"/>:
    /// <see cref="PollInterval"/>, or <see cref="DefaultPollInterval"/>.
    /// </summary>
    public TimeSpan GetPollInterval(BitcoinNetwork network) => PollInterval ?? DefaultPollInterval(network);

    /// <summary>The poll interval when <see cref="PollInterval"/> is unset: 2 s regtest, 10 s mainnet, 5 s otherwise.</summary>
    public static TimeSpan DefaultPollInterval(BitcoinNetwork network) =>
        network == BitcoinNetwork.Regtest ? TimeSpan.FromSeconds(2)
        : network == BitcoinNetwork.Mainnet ? TimeSpan.FromSeconds(10)
        : TimeSpan.FromSeconds(5);

    /// <summary>
    /// The settings the node cannot run without: a usable RPC endpoint (<see cref="IsUsableRpcEndpoint"/>), the RPC user
    /// and password, a known notification mode, with ZMQ the ZMQ host and block port, a ZMQ tx port in range, a tip poll
    /// interval that is not negative and, when set, a poll interval between <see cref="MinPollInterval"/> and
    /// <see cref="MaxPollInterval"/>.
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

        if (!Enum.IsDefined(Notifications))
            errors.Add($"{SectionName}:{nameof(Notifications)} must be Zmq or Poll.");

        if (Notifications == ChainNotificationMode.Zmq)
        {
            if (string.IsNullOrWhiteSpace(ZmqHost))
                errors.Add($"{SectionName}:{nameof(ZmqHost)} is required (or set {SectionName}:{nameof(Notifications)} "
                         + "to Poll for a node without ZMQ).");

            if (ZmqBlockPort is < 1 or > 65535)
                errors.Add($"{SectionName}:{nameof(ZmqBlockPort)} must be a port between 1 and 65535.");
        }
        else if (ZmqBlockPort is < 0 or > 65535)
        {
            errors.Add($"{SectionName}:{nameof(ZmqBlockPort)} must be a port between 1 and 65535 (0 = unset).");
        }

        if (ZmqTxPort is < 0 or > 65535)
            errors.Add($"{SectionName}:{nameof(ZmqTxPort)} must be a port between 1 and 65535 (0 = unset).");

        if (TipPollInterval < TimeSpan.Zero)
            errors.Add($"{SectionName}:{nameof(TipPollInterval)} must not be negative (0 turns the tip poll off).");

        if (PollInterval is { } poll && (poll < MinPollInterval || poll > MaxPollInterval))
            errors.Add($"{SectionName}:{nameof(PollInterval)} must be between {MinPollInterval:c} and "
                     + $"{MaxPollInterval:c} (unset = the network's default).");

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