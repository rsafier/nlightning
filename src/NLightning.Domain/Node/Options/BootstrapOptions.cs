using System.Net;

namespace NLightning.Domain.Node.Options;

using Bootstrap;
using Protocol.Constants;
using Protocol.ValueObjects;

/// <summary>
/// BOLT 10 DNS seed bootstrap (NL-113), configuration section <c>Node:Bootstrap</c> (it is
/// <see cref="NodeOptions.Bootstrap"/>). On by default on mainnet only (D-B10-1, reversed by the owner on 2026-09-28:
/// a fresh mainnet node finds its first peers without configuration); off elsewhere. Seed answers are unauthenticated
/// DNS (the BOLT 8 handshake proves the node id) and every peer connected through them is saved as a <c>Peers</c> row.
/// </summary>
public class BootstrapOptions
{
    /// <summary>
    /// The mainnet seeds. BOLT 10 defines no seed list (its examples use <c>lseed.bitcoinstats.com</c>); these are the
    /// seeds LND and CLN ship with, checked on 2026-09-28 (both answer SRV with bech32 node ids).
    /// </summary>
    public static IReadOnlyList<string> MainnetSeeds { get; } = ["nodes.lightning.directory", "nodes.lightning.wiki"];

    /// <summary>The testnet (testnet3) seed LND ships with (checked on 2026-09-28).</summary>
    public static IReadOnlyList<string> TestnetSeeds { get; } = ["test.nodes.lightning.directory"];

    /// <summary>
    /// The testnet4 seed LND ships with (`test4.nodes.lightning.wiki`; checked on 2026-09-30: it answers BOLT 10 SRV
    /// with bech32 node ids, NL-545). Testnet4 itself is not a supported network yet (NL-012); the list is the
    /// plumbing for when it is (a custom registration of the name already picks it up). Bitcoin Core's testnet4 DNS
    /// seeds (`seed.testnet4.bitcoin.sprovoost.nl`, `seed.testnet4.wiz.biz`) are P2P seeds, not BOLT 10, and are not
    /// usable here.
    /// </summary>
    public static IReadOnlyList<string> Testnet4Seeds { get; } = ["test4.nodes.lightning.wiki"];

    /// <summary>
    /// The signet seed LND ships with (`signet.nodes.lightning.wiki`). The root answers BOLT 10 queries but held no
    /// records on 2026-09-30 (NL-545); the seed is kept so a signet node's bootstrap finds the candidates as soon as
    /// they appear. Custom signets (Mutinynet) are signet for Lightning (<c>BitcoinNetwork</c> resolves them to
    /// <see cref="Protocol.ValueObjects.BitcoinNetwork.Signet"/>), so they share this list and would find
    /// default-signet candidates. Bitcoin Core's signet seeds are P2P seeds, not BOLT 10.
    /// </summary>
    public static IReadOnlyList<string> SignetSeeds { get; } = ["signet.nodes.lightning.wiki"];

    /// <summary>The largest <see cref="MaxPerSeed"/>.</summary>
    public const int MaxPerSeedLimit = 100;

    /// <summary>The largest <see cref="MaxDialConcurrency"/>.</summary>
    public const int MaxDialConcurrencyLimit = 8;

    /// <summary>
    /// The public resolvers asked when the system resolvers give no usable answer for a seed (D-B10-7): Cloudflare and
    /// Google, IPv4 (reachable from IPv4-only hosts).
    /// </summary>
    public static IReadOnlyList<string> DefaultFallbackNameServers { get; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// Bootstrap from DNS seeds; unset means on on mainnet and off on every other network (D-B10-1 as reversed on
    /// 2026-09-28). Read the effective value from <see cref="IsEnabledOn"/>.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The effective switch on <paramref name="network"/>: <see cref="Enabled"/> when set, otherwise true on mainnet
    /// only (testnet has a seed but stays off: testnet3 is being replaced by testnet4, whose network support is still
    /// open, NL-012/NL-545).
    /// </summary>
    public bool IsEnabledOn(BitcoinNetwork network) => Enabled ?? network.Name == NetworkConstants.Mainnet;

    /// <summary>
    /// The seed roots to query; unset means the network's own list (<see cref="GetDefaultSeeds"/>). Seeds are used on
    /// a seed network (<see cref="IsSeedNetwork"/>: mainnet, testnet, testnet4, signet) only, unless
    /// <see cref="AllowSeedsOnThisNetwork"/> (D-B10-2).
    /// </summary>
    public List<string>? Seeds { get; set; }

    /// <summary>
    /// Use the configured <see cref="Seeds"/> on a network that has no public seeds (regtest, e.g. a local test
    /// seed). Without it they are ignored there, with a warning.
    /// </summary>
    public bool AllowSeedsOnThisNetwork { get; set; }

    /// <summary>
    /// The seeds older config templates wrote into the obsolete <c>Node:DnsSeedServers</c> key on every network but
    /// signets. The operator never chose them, so such a list is ignored without a warning.
    /// </summary>
    public static IReadOnlyList<string> ObsoleteTemplateSeeds { get; } =
        ["nlseed.nlightn.ing", "nodes.lightning.directory", "lseed.bitcoinstats.com"];

    /// <summary>
    /// Set by the host when the obsolete <c>Node:DnsSeedServers</c> key carries seeds that are not the old template's
    /// (<see cref="ObsoleteTemplateSeeds"/>). The key is never used; the bootstrap service warns once, when enabled,
    /// that it is ignored. Not a configuration key.
    /// </summary>
    public bool ObsoleteSeedsIgnored { get; set; }

    /// <summary>
    /// True when <paramref name="obsoleteSeeds"/> (the <c>Node:DnsSeedServers</c> entries) holds a seed the old
    /// template did not write, i.e. an operator edited the list.
    /// </summary>
    public static bool IsEditedObsoleteSeedList(IEnumerable<string?>? obsoleteSeeds) =>
        obsoleteSeeds?.Where(s => !string.IsNullOrWhiteSpace(s))
                      .Any(s => !ObsoleteTemplateSeeds.Contains(s!.Trim().TrimEnd('.'),
                                                                StringComparer.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// The DNS servers to ask, <c>ip[:port]</c> (IPv6 in brackets with a port); empty means the system resolvers.
    /// </summary>
    public List<string> NameServers { get; set; } = [];

    /// <summary>
    /// Ask <see cref="FallbackNameServers"/> for a seed the system resolvers gave no candidate for (SERVFAIL, timeout,
    /// no records). On by default (D-B10-7): home routers and some ISP resolvers fail the seeds' SRV answers, and
    /// the system resolver learns of the seed query anyway. Used only when <see cref="NameServers"/> is empty: an
    /// operator who names resolvers gets exactly those.
    /// </summary>
    public bool FallbackToPublicResolvers { get; set; } = true;

    /// <summary>
    /// The resolvers of the fallback, <c>ip[:port]</c>; <see cref="DefaultFallbackNameServers"/> by default.
    /// </summary>
    public List<string> FallbackNameServers { get; set; } = [.. DefaultFallbackNameServers];

    /// <summary>
    /// True when a seed without candidates from the system resolvers is asked again through
    /// <see cref="FallbackNameServers"/>: <see cref="FallbackToPublicResolvers"/>, no <see cref="NameServers"/> and a
    /// fallback list.
    /// </summary>
    public bool UsesFallbackResolvers =>
        FallbackToPublicResolvers && NameServers.Count == 0 && FallbackNameServers.Count > 0;

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

    /// <summary>
    /// The time one connection (TCP, BOLT 8 handshake, init) gets; the dial is cancelled when it runs out. Longer than
    /// the default <c>Node:NetworkTimeout</c> (15 s) of the TCP connect alone, so a slow honest peer is not cut off.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The time a dial gets: <see cref="ConnectTimeout"/>, but never less than <paramref name="networkTimeout"/>
    /// (<c>Node:NetworkTimeout</c>, the TCP connect alone), so an operator who raised that one is not cut off by the
    /// mainnet default of the bootstrap.
    /// </summary>
    public TimeSpan GetEffectiveConnectTimeout(TimeSpan networkTimeout) =>
        ConnectTimeout >= networkTimeout ? ConnectTimeout : networkTimeout;

    /// <summary>
    /// The wait before another run of the initial phase while the node stays under <see cref="MinPeers"/>.
    /// </summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The most runs of the initial phase (every <see cref="RetryInterval"/>); the peer-count keeper then takes over
    /// (<see cref="MaintenanceInterval"/>, NL-547), so this bounds how long the start retries at its own pace, not how
    /// long the node keeps its peers.
    /// </summary>
    public int MaxRuns { get; set; } = 12;

    /// <summary>
    /// How often the peer-count keeper checks, after the initial phase, that at least <see cref="MinPeers"/> peers are
    /// connected, for the process lifetime (NL-547). It is also the first backoff after a top-up that leaves the node
    /// short.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest wait between two top-ups of the keeper while they leave the node below <see cref="MinPeers"/>: the
    /// wait starts at <see cref="MaintenanceInterval"/> and doubles after each such top-up up to this; it resets once
    /// enough peers are connected. At least <see cref="MaintenanceInterval"/>.
    /// </summary>
    public TimeSpan MaxMaintenanceBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an endpoint whose dial failed is skipped (graph and seed candidates alike) before it may be dialed
    /// again (NL-547).
    /// </summary>
    public TimeSpan FailedEndpointTtl { get; set; } = TimeSpan.FromHours(1);

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

    /// <summary>
    /// True when <paramref name="network"/> has public DNS seeds (mainnet, testnet, testnet4 and signet, NL-545).
    /// </summary>
    public static bool IsSeedNetwork(BitcoinNetwork network) =>
        network.Name is NetworkConstants.Mainnet or NetworkConstants.Testnet or NetworkConstants.Testnet4
            or NetworkConstants.Signet;

    /// <summary>
    /// The network's own seeds: two on mainnet, one on testnet, one on testnet4 (LND's, live on 2026-09-30) and one on
    /// signet (LND's; the root held no records on 2026-09-30), none on regtest (NL-545).
    /// </summary>
    public static IReadOnlyList<string> GetDefaultSeeds(BitcoinNetwork network) => network.Name switch
    {
        NetworkConstants.Mainnet => MainnetSeeds,
        NetworkConstants.Testnet => TestnetSeeds,
        NetworkConstants.Testnet4 => Testnet4Seeds,
        NetworkConstants.Signet => SignetSeeds,
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
        CheckPositiveTime(MaintenanceInterval, nameof(MaintenanceInterval));
        CheckPositiveTime(FailedEndpointTtl, nameof(FailedEndpointTtl));
        if (MaxMaintenanceBackoff < MaintenanceInterval)
            errors.Add($"{prefix}{nameof(MaxMaintenanceBackoff)} must be at least {nameof(MaintenanceInterval)}.");
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

        foreach (var nameServer in FallbackNameServers)
            if (!TryParseNameServer(nameServer, out _))
                errors.Add($"{prefix}{nameof(FallbackNameServers)} '{nameServer}' is not ip[:port].");

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