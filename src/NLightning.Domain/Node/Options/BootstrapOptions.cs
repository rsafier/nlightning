using System.Net;

namespace NLightning.Domain.Node.Options;

using Bootstrap;
using Protocol.Constants;
using Protocol.ValueObjects;

/// <summary>
/// BOLT 10 DNS seed bootstrap (NL-113), configuration section <c>Node:Bootstrap</c> (it is
/// <see cref="NodeOptions.Bootstrap"/>). Off by default (D-B10-1): seed answers are unauthenticated DNS and every peer
/// connected through them is saved as a permanent <c>Peers</c> row.
/// </summary>
public class BootstrapOptions
{
    /// <summary>The mainnet seeds (BOLT 10 lists these; both answer SRV with bech32 node ids).</summary>
    public static IReadOnlyList<string> MainnetSeeds { get; } = ["nodes.lightning.directory", "nodes.lightning.wiki"];

    /// <summary>The testnet (testnet3) seeds.</summary>
    public static IReadOnlyList<string> TestnetSeeds { get; } = ["test.nodes.lightning.directory"];

    /// <summary>The largest <see cref="MaxPerSeed"/>.</summary>
    public const int MaxPerSeedLimit = 100;

    /// <summary>The largest <see cref="MaxDialConcurrency"/>.</summary>
    public const int MaxDialConcurrencyLimit = 8;

    /// <summary>Bootstrap from DNS seeds; unset means off. Read the effective value from <see cref="IsEnabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>The effective switch: <see cref="Enabled"/> when set, otherwise false.</summary>
    public bool IsEnabled => Enabled ?? false;

    /// <summary>
    /// The seed roots to query; unset means the network's own list (<see cref="GetDefaultSeeds"/>). Seeds are used on
    /// mainnet and testnet only, unless <see cref="AllowSeedsOnThisNetwork"/> (D-B10-2).
    /// </summary>
    public List<string>? Seeds { get; set; }

    /// <summary>
    /// Use the configured <see cref="Seeds"/> on a network that has no public seeds (regtest, signets, e.g. a local
    /// test seed). Without it they are ignored there, with a warning.
    /// </summary>
    public bool AllowSeedsOnThisNetwork { get; set; }

    /// <summary>
    /// Set when <see cref="Seeds"/> was copied from the obsolete <c>Node:DnsSeedServers</c> key; the bootstrap
    /// service logs a warning. Not a configuration key.
    /// </summary>
    public bool SeedsFromObsoleteKey { get; set; }

    /// <summary>
    /// The DNS servers to ask, <c>ip[:port]</c> (IPv6 in brackets with a port); empty means the system resolvers.
    /// </summary>
    public List<string> NameServers { get; set; } = [];

    /// <summary>How DNS queries travel; TCP by default (D-B10-3: 25 SRV records do not fit in 512 bytes).</summary>
    public DnsSeedTransport Transport { get; set; } = DnsSeedTransport.Tcp;

    /// <summary>Bootstrap runs only while fewer peers than this are connected.</summary>
    public int MinPeers { get; set; } = 3;

    /// <summary>The most connections one bootstrap run makes.</summary>
    public int MaxPeersFromBootstrap { get; set; } = 8;

    /// <summary>The most candidates taken from one seed.</summary>
    public int MaxPerSeed { get; set; } = 25;

    /// <summary>The most dials at the same time.</summary>
    public int MaxDialConcurrency { get; set; } = 2;

    /// <summary>The time one seed gets, all its queries included.</summary>
    public TimeSpan PerSeedTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The time one DNS query gets.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The time one connection (TCP and BOLT 8 handshake, init) gets.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The wait before another run while the node stays under <see cref="MinPeers"/>.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most runs; the loop then stops.</summary>
    public int MaxRuns { get; set; } = 12;

    /// <summary>The wait before the first run, so saved peers connect first. Zero is allowed.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The address families asked for.</summary>
    public DnsSeedAddressTypes AddressFamilies { get; set; } = DnsSeedAddressTypes.Both;

    /// <summary>Accept private, loopback and other non-routable addresses from seeds (local test seeds only).</summary>
    public bool AllowNonRoutableAddresses { get; set; }

    /// <summary>
    /// Ask with the <c>r0.a&lt;families&gt;</c> conditions first. Off by default: live seeds answer nothing to them.
    /// </summary>
    public bool UseQueryConditions { get; set; }

    /// <summary>True when <paramref name="network"/> has public DNS seeds (mainnet and testnet).</summary>
    public static bool IsSeedNetwork(BitcoinNetwork network) =>
        network.Name is NetworkConstants.Mainnet or NetworkConstants.Testnet;

    /// <summary>The network's own seeds: two on mainnet, one on testnet, none elsewhere.</summary>
    public static IReadOnlyList<string> GetDefaultSeeds(BitcoinNetwork network) => network.Name switch
    {
        NetworkConstants.Mainnet => MainnetSeeds,
        NetworkConstants.Testnet => TestnetSeeds,
        _ => []
    };

    /// <summary>
    /// The seeds to query on <paramref name="network"/>: the configured <see cref="Seeds"/> (or the network default
    /// when unset) on a seed network or with <see cref="AllowSeedsOnThisNetwork"/>; none otherwise.
    /// </summary>
    /// <param name="network">The node's network.</param>
    /// <param name="ignoredConfigured">True when configured seeds were ignored because the network has none.</param>
    public IReadOnlyList<string> GetEffectiveSeeds(BitcoinNetwork network, out bool ignoredConfigured)
    {
        ignoredConfigured = false;
        var configured = Seeds?.Where(s => !string.IsNullOrWhiteSpace(s))
                               .Select(s => s.Trim().TrimEnd('.').ToLowerInvariant())
                               .Distinct()
                               .ToList();
        if (IsSeedNetwork(network) || AllowSeedsOnThisNetwork)
            return configured ?? GetDefaultSeeds(network);

        ignoredConfigured = configured is { Count: > 0 };
        return [];
    }

    /// <summary>The configuration errors, empty when valid (<see cref="NodeOptions.GetValidationErrors"/>).</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        const string prefix = "Bootstrap:";
        var errors = new List<string>();
        CheckPositive(MinPeers, nameof(MinPeers));
        CheckPositive(MaxPeersFromBootstrap, nameof(MaxPeersFromBootstrap));
        CheckPositive(MaxPerSeed, nameof(MaxPerSeed));
        CheckPositive(MaxDialConcurrency, nameof(MaxDialConcurrency));
        CheckPositive(MaxRuns, nameof(MaxRuns));
        if (MaxPerSeed > MaxPerSeedLimit)
            errors.Add($"{prefix}{nameof(MaxPerSeed)} must be at most {MaxPerSeedLimit}.");
        if (MaxDialConcurrency > MaxDialConcurrencyLimit)
            errors.Add($"{prefix}{nameof(MaxDialConcurrency)} must be at most {MaxDialConcurrencyLimit}.");

        CheckPositiveTime(PerSeedTimeout, nameof(PerSeedTimeout));
        CheckPositiveTime(QueryTimeout, nameof(QueryTimeout));
        CheckPositiveTime(ConnectTimeout, nameof(ConnectTimeout));
        CheckPositiveTime(RetryInterval, nameof(RetryInterval));
        if (StartupDelay < TimeSpan.Zero)
            errors.Add($"{prefix}{nameof(StartupDelay)} must not be negative.");

        if (!Enum.IsDefined(Transport))
            errors.Add($"{prefix}{nameof(Transport)} must be Tcp or UdpWithTcpFallback.");
        if (AddressFamilies is not (DnsSeedAddressTypes.IPv4 or DnsSeedAddressTypes.IPv6 or DnsSeedAddressTypes.Both))
            errors.Add($"{prefix}{nameof(AddressFamilies)} must be IPv4, IPv6 or Both.");

        foreach (var seed in Seeds ?? [])
            if (!DnsSeedQuery.IsValidDnsName(seed?.Trim(), out var reason))
                errors.Add($"{prefix}{nameof(Seeds)} '{seed}' is not a DNS name: {reason}.");

        foreach (var nameServer in NameServers)
            if (!TryParseNameServer(nameServer, out _))
                errors.Add($"{prefix}{nameof(NameServers)} '{nameServer}' is not ip[:port].");

        return errors;

        void CheckPositive(int value, string name)
        {
            if (value <= 0)
                errors.Add($"{prefix}{name} must be positive.");
        }

        void CheckPositiveTime(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
                errors.Add($"{prefix}{name} must be positive.");
        }
    }

    /// <summary>
    /// Parses a name server as <c>ip</c>, <c>ip:port</c> or <c>[ipv6]:port</c>; port 53 when none is given.
    /// </summary>
    public static bool TryParseNameServer(string? value, out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // IPEndPoint.TryParse reads ip, ip:port and [ipv6]:port; a missing port comes back as 0
        if (!IPEndPoint.TryParse(value.Trim(), out var parsed))
            return false;

        if (parsed.Port == 0)
            parsed.Port = 53;

        endPoint = parsed;
        return true;
    }
}

/// <summary>How the BOLT 10 DNS queries travel.</summary>
public enum DnsSeedTransport
{
    /// <summary>TCP only (the default, as LND).</summary>
    Tcp,

    /// <summary>UDP with EDNS0 (4096-byte buffer), retried over TCP when the answer is truncated.</summary>
    UdpWithTcpFallback
}