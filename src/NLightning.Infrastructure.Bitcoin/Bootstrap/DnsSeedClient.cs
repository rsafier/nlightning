using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Bootstrap;

using Domain.Crypto.ValueObjects;
using Domain.Node.Bootstrap;
using Domain.Node.Constants;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Infrastructure.Protocol.Dns;

/// <summary>
/// The BOLT 10 DNS seed client (NL-113): SRV on the seed root (then on <c>_nodes._tcp.&lt;root&gt;</c>), the node id
/// from each target's first label (bech32, HRP <c>ln</c>), the target's addresses from the glue of the additional
/// section or from A/AAAA queries, the port from the SRV record.
/// </summary>
/// <remarks>
/// Every answer is unauthenticated DNS: node ids must decode to curve points, addresses must be routable
/// (<see cref="SeedAddressFilter"/>) and of a family we asked for, and the BOLT 8 handshake is what proves the node
/// id. A bare A/AAAA answer on the root carries no node id and is not used. The whole seed runs under
/// <see cref="BootstrapOptions.PerSeedTimeout"/>; only the caller's cancellation throws.
/// </remarks>
internal sealed class DnsSeedClient : IDnsSeedClient
{
    /// <summary>The most A/AAAA lookups of one seed's targets at the same time (NL-546).</summary>
    internal const int MaxConcurrentAddressLookups = 8;

    private readonly IDnsRecordLookup _lookup;
    private readonly IFallbackDnsRecordLookup? _fallback;
    private readonly ITorDnsRecordLookup? _torLookup;
    private readonly ILogger<DnsSeedClient> _logger;
    private readonly BootstrapOptions _options;
    private readonly bool _torOnly;

    public DnsSeedClient(IDnsRecordLookup lookup, IOptions<NodeOptions> nodeOptions, ILogger<DnsSeedClient> logger,
                         IFallbackDnsRecordLookup? fallback = null, ITorDnsRecordLookup? torLookup = null)
    {
        _lookup = lookup;
        _fallback = fallback;
        _torLookup = torLookup;
        _logger = logger;
        _options = nodeOptions.Value.Bootstrap;
        _torOnly = nodeOptions.Value.Tor.IsTorOnly;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The system (or configured) resolvers first; when they give no candidate and the fallback is available
    /// (<see cref="BootstrapOptions.UsesFallbackResolvers"/>, D-B10-7) the seed is asked again, with its own
    /// <see cref="BootstrapOptions.PerSeedTimeout"/>, through <see cref="BootstrapOptions.FallbackNameServers"/>.
    /// In Tor-only mode none of that clearnet DNS happens: the seed is asked through
    /// <see cref="BootstrapOptions.TorNameServer"/> over Tor's SOCKS port (NL-571), or not at all when that resolver
    /// is switched off.
    /// </remarks>
    public async Task<DnsSeedResult> QuerySeedAsync(string seedRoot, DnsSeedAddressTypes families, int maxResults,
                                                    CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedRoot);
        var root = seedRoot.Trim().TrimEnd('.').ToLowerInvariant();
        if (_torOnly)
        {
            // Tor-only: no query ever leaves through the clearnet resolvers, with or without a Tor resolver
            return _torLookup is { IsAvailable: true } tor
                ? await QuerySeedWithAsync(tor, root, families, maxResults, ct)
                : NotAsked(root);
        }

        var result = await QuerySeedWithAsync(_lookup, root, families, maxResults, ct);
        if (result.Candidates.Count > 0 || maxResults <= 0 || _fallback is not { IsAvailable: true } fallback)
            return result;

        _logger.LogInformation("Seed {Seed}: the system resolver answered {Outcome} with no candidate; asking {Servers}",
                               root, result.Outcome, string.Join(", ", fallback.NameServers));
        var fallbackResult = await QuerySeedWithAsync(fallback, root, families, maxResults, ct);
        return fallbackResult with { UsedFallbackResolver = true, SystemResolverOutcome = result.Outcome };
    }

    /// <inheritdoc />
    /// <remarks>
    /// BOLT 10's assisted location (NL-541): the query name is the node's virtual host under the seed
    /// (<c>l&lt;bech32 node id&gt;.&lt;root&gt;</c>, <see cref="DnsSeedQuery.VirtualHost"/>). Its SRV answer carries
    /// the (virtual host, port) tuples of a node on non-default ports, its A/AAAA answers the addresses (BOLT 10
    /// answers those only for nodes on the default port). The fallback resolvers (D-B10-7) are asked when the system
    /// resolvers give no candidate at all. Live seeds checked 2026-09-30 (nodes.lightning.directory) answer the
    /// addresses but not the per-node SRV, so a node whose port moved is found at the default port; the caller may
    /// also try the port it already knows on the located address.
    /// </remarks>
    public async Task<DnsSeedNodeLocation> LocateNodeAsync(string seedRoot, CompactPubKey nodeId,
                                                           DnsSeedAddressTypes families, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedRoot);
        var root = seedRoot.Trim().TrimEnd('.').ToLowerInvariant();
        var location = await LocateNodeWithAsync(_lookup, root, nodeId, families, ct);
        if (location.Candidates.Count > 0 || _fallback is not { IsAvailable: true } fallback)
            return location;

        _logger.LogInformation("Seed {Seed}: the system resolver located node {Node} as {Outcome}; asking {Servers}",
                               root, nodeId, location.Outcome, string.Join(", ", fallback.NameServers));
        var fallbackLocation = await LocateNodeWithAsync(fallback, root, nodeId, families, ct);
        return fallbackLocation with { UsedFallbackResolver = true, SystemResolverOutcome = location.Outcome };
    }

    /// <summary>
    /// The result for a seed that is not asked: Tor-only mode without a usable Tor resolver
    /// (<c>Node:Bootstrap:TorNameServer</c> empty).
    /// </summary>
    private DnsSeedResult NotAsked(string root)
    {
        _logger.LogDebug("Seed {Seed}: not asked (Tor-only mode without a Tor resolver)", root);
        return new DnsSeedResult(root, DnsSeedOutcome.Error, [], 0);
    }

    private async Task<DnsSeedResult> QuerySeedWithAsync(IDnsRecordLookup lookup, string root,
                                                         DnsSeedAddressTypes families, int maxResults,
                                                         CancellationToken ct)
    {
        var candidates = new List<SeedPeerCandidate>();
        var rejected = 0;
        if (maxResults <= 0)
            return new DnsSeedResult(root, DnsSeedOutcome.Empty, candidates, rejected);

        using var seedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        seedCts.CancelAfter(_options.PerSeedTimeout);
        var token = seedCts.Token;
        try
        {
            var (outcome, srvResponse) = await QuerySrvAsync(lookup, root, families, maxResults, token);
            if (srvResponse is null)
                return new DnsSeedResult(root, outcome, candidates, rejected);

            var seen = new HashSet<(CompactPubKey, IPAddress, ushort)>();

            // One record per (target, port): a node listening on two ports comes back as two tuples of one virtual
            // host (BOLT 10), and both are kept; the target's addresses are looked up once
            var records = srvResponse.Srv
                                     .Where(r => !string.IsNullOrWhiteSpace(r.Target))
                                     .DistinctBy(r => (r.Target.TrimEnd('.').ToLowerInvariant(), r.Port))
                                     .ToArray();
            Random.Shared.Shuffle(records);

            // The targets' addresses are looked up in concurrent batches (NL-546): a seed without glue answers 25
            // targets, i.e. 50 A/AAAA queries, which took about 8.5 s one after the other over TCP on mainnet. A
            // batch is never larger than the candidates still wanted, so a small cap costs no extra queries
            var addressesByTarget =
                new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                if (candidates.Count >= maxResults)
                    break;

                var target = record.Target.TrimEnd('.');

                // The node id is the target's first label, taken literally (targets may sit under another root)
                var label = target.Split('.')[0];
                if (!LightningNodeIdBech32.TryDecode(label, out var nodeId, out var reason))
                {
                    rejected++;
                    _logger.LogDebug("Seed {Seed}: dropped SRV target {Target}: {Reason}", root, target, reason);
                    continue;
                }

                if (!addressesByTarget.ContainsKey(target) && !token.IsCancellationRequested)
                {
                    var batch = records.Skip(index)
                                       .Select(r => r.Target.TrimEnd('.'))
                                       .Where(t => !addressesByTarget.ContainsKey(t)
                                                && LightningNodeIdBech32.TryDecode(t.Split('.')[0], out _, out _))
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .Take(Math.Min(MaxConcurrentAddressLookups, maxResults - candidates.Count))
                                       .ToArray();
                    foreach (var (resolvedTarget, resolvedAddresses) in
                             await ResolveTargetsAsync(lookup, root, batch, srvResponse, families, token))
                        addressesByTarget[resolvedTarget] = resolvedAddresses;
                }

                // Not resolved: the seed's time ran out first
                if (!addressesByTarget.TryGetValue(target, out var addresses))
                    continue;

                foreach (var found in addresses)
                {
                    if (candidates.Count >= maxResults)
                        break;

                    var address = found.IsIPv4MappedToIPv6 ? found.MapToIPv4() : found;
                    if (!IsWantedFamily(address, families))
                    {
                        rejected++;
                        _logger.LogDebug("Seed {Seed}: dropped {Address} of {Target}: family not asked for", root,
                                         address, target);
                        continue;
                    }

                    if (!SeedAddressFilter.IsUsable(address, record.Port, _options.AllowNonRoutableAddresses,
                                                    out reason))
                    {
                        rejected++;
                        _logger.LogDebug("Seed {Seed}: dropped {Address}:{Port} of {Target}: {Reason}", root, address,
                                         record.Port, target, reason);
                        continue;
                    }

                    if (seen.Add((nodeId, address, record.Port)))
                        candidates.Add(new SeedPeerCandidate(nodeId, address, record.Port, root));
                }
            }

            if (token.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogDebug("Seed {Seed} timed out after {Timeout} with {Count} candidates", root,
                                 _options.PerSeedTimeout, candidates.Count);
                return new DnsSeedResult(root, DnsSeedOutcome.Timeout, candidates, rejected);
            }

            return new DnsSeedResult(root, DnsSeedOutcome.Ok, candidates, rejected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Seed {Seed} timed out after {Timeout}", root, _options.PerSeedTimeout);
            return new DnsSeedResult(root, DnsSeedOutcome.Timeout, candidates, rejected);
        }
    }

    /// <summary>
    /// One node query under <see cref="BootstrapOptions.PerSeedTimeout"/>: the virtual host's SRV answer (the ports
    /// of the node, only its own records count), then its A/AAAA answers per family. The outcome is
    /// <see cref="DnsSeedOutcome.Ok"/> when the seed answered records for the node,
    /// <see cref="DnsSeedOutcome.Empty"/> for its empty reply (it does not know the node), and the resolvers' failure
    /// otherwise. A per-seed timeout is a <see cref="DnsSeedOutcome.Timeout"/> with whatever was answered; only the
    /// caller's cancellation throws.
    /// </summary>
    private async Task<DnsSeedNodeLocation> LocateNodeWithAsync(IDnsRecordLookup lookup, string root,
                                                                CompactPubKey nodeId, DnsSeedAddressTypes families,
                                                                CancellationToken callerCt)
    {
        var candidates = new List<SeedPeerCandidate>();
        var rejected = 0;
        using var seedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        seedCts.CancelAfter(_options.PerSeedTimeout);
        var ct = seedCts.Token;
        try
        {
            var label = LightningNodeIdBech32.Encode(nodeId);
            var host = DnsSeedQuery.VirtualHost(label, root);
            if (!DnsSeedQuery.IsValidDnsName(host, out var reason))
                throw new ArgumentException($"Invalid node query name '{host}': {reason}", "seedRoot");

            // The node's own SRV records give the ports; a foreign target (another node's virtual host) is rejected
            var status = DnsLookupStatus.NoError;
            var ownSrvRecords = 0;
            var ports = new List<ushort>();
            var srvResponse = await lookup.QueryAsync(host, DnsRecordKind.Srv, ct);
            if (srvResponse.Status == DnsLookupStatus.NoError)
            {
                foreach (var record in srvResponse.Srv)
                {
                    if (string.IsNullOrWhiteSpace(record.Target)
                     || !LightningNodeIdBech32.TryDecode(record.Target.TrimEnd('.').Split('.')[0], out var recordId,
                                                         out _)
                     || recordId != nodeId)
                    {
                        rejected++;
                        _logger.LogDebug("Seed {Seed}: node query: dropped SRV target {Target}: not node {Node}", root,
                                         record.Target, label);
                        continue;
                    }

                    ownSrvRecords++;
                    if (record.Port == 0)
                    {
                        rejected++;
                        _logger.LogDebug("Seed {Seed}: node query: dropped SRV port 0 of {Node}", root, label);
                        continue;
                    }

                    if (!ports.Contains(record.Port))
                        ports.Add(record.Port);
                }
            }
            else
            {
                status = srvResponse.Status;
            }

            var addresses = new List<IPAddress>();
            if (families.HasFlag(DnsSeedAddressTypes.IPv4))
            {
                var (ipv4Status, ipv4Addresses) =
                    await QueryNodeAddressesAsync(lookup, root, label, host, DnsRecordKind.A, status, ct);
                status = ipv4Status;
                addresses.AddRange(ipv4Addresses);
            }

            if (families.HasFlag(DnsSeedAddressTypes.IPv6))
            {
                var (ipv6Status, ipv6Addresses) =
                    await QueryNodeAddressesAsync(lookup, root, label, host, DnsRecordKind.Aaaa, status, ct);
                status = ipv6Status;
                addresses.AddRange(ipv6Addresses);
            }

            if (ct.IsCancellationRequested)
            {
                callerCt.ThrowIfCancellationRequested();
                _logger.LogDebug("Seed {Seed}: node query for {Node} timed out after {Timeout}", root, label,
                                 _options.PerSeedTimeout);
                return new DnsSeedNodeLocation(root, DnsSeedOutcome.Timeout, candidates, rejected);
            }

            // The SRV ports when the seed answered them, else the default port: the A/AAAA answer is for nodes on it
            var wantedPorts = ports;
            if (wantedPorts.Count == 0)
                wantedPorts = [(ushort)NodeConstants.DefaultPort];
            foreach (var address in addresses)
            {
                var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (!IsWantedFamily(ip, families))
                {
                    rejected++;
                    _logger.LogDebug("Seed {Seed}: node query: dropped {Address} of {Node}: family not asked for",
                                     root, ip, label);
                    continue;
                }

                foreach (var port in wantedPorts)
                {
                    if (!SeedAddressFilter.IsUsable(ip, port, _options.AllowNonRoutableAddresses, out reason))
                    {
                        rejected++;
                        _logger.LogDebug("Seed {Seed}: node query: dropped {Address}:{Port} of {Node}: {Reason}", root,
                                         ip, port, label, reason);
                        continue;
                    }

                    if (candidates.All(c => c.Address != ip || c.Port != port))
                        candidates.Add(new SeedPeerCandidate(nodeId, ip, port, root));
                }
            }

            var answered = ownSrvRecords > 0 || addresses.Count > 0;
            return new DnsSeedNodeLocation(root, answered ? DnsSeedOutcome.Ok : MapStatus(status), candidates,
                                           rejected);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            _logger.LogDebug("Seed {Seed}: node query for {Node} timed out after {Timeout}", root, nodeId,
                             _options.PerSeedTimeout);
            return new DnsSeedNodeLocation(root, DnsSeedOutcome.Timeout, candidates, rejected);
        }
    }

    /// <summary>
    /// The addresses of a node query's A or AAAA answer; the status stays at the resolvers' first failure when
    /// <paramref name="status"/> already carries one.
    /// </summary>
    private async Task<(DnsLookupStatus Status, IReadOnlyList<IPAddress> Addresses)> QueryNodeAddressesAsync(
        IDnsRecordLookup lookup, string root, string node, string host, DnsRecordKind kind, DnsLookupStatus status,
        CancellationToken ct)
    {
        var response = await lookup.QueryAsync(host, kind, ct);
        if (response.Status == DnsLookupStatus.NoError)
            return (status, response.Addresses);

        _logger.LogDebug("Seed {Seed}: node query for {Node}: {Kind} answered {Status}", root, node, kind,
                         response.Status);
        return (status == DnsLookupStatus.NoError ? response.Status : status, []);
    }

    /// <summary>
    /// The SRV answer to use: with <see cref="BootstrapOptions.UseQueryConditions"/> the
    /// <c>n&lt;count&gt;.a&lt;families&gt;.r0</c> query first (the <c>n</c> condition asks for
    /// <paramref name="maxResults"/> records, BOLT 10's default being 25), then the bare root, then
    /// <c>_nodes._tcp.&lt;root&gt;</c>. Null (with the outcome) when none answered records.
    /// </summary>
    private async Task<(DnsSeedOutcome Outcome, DnsLookupResponse? Response)> QuerySrvAsync(
        IDnsRecordLookup lookup, string root, DnsSeedAddressTypes families, int maxResults, CancellationToken ct)
    {
        var names = new List<string>(3);
        if (_options.UseQueryConditions)
            names.Add(new DnsSeedQuery(root, Realm: 0, AddressTypes: (byte)families, Count: maxResults)
                         .ToHostName());
        names.Add(root);
        names.Add(DnsSeedQuery.SrvAlias(root));

        // The outcome of a seed without records is the bare root's answer: the conditional query is only a
        // preference, and the _nodes._tcp alias only a fallback
        var outcome = DnsSeedOutcome.Empty;
        foreach (var name in names)
        {
            var response = await lookup.QueryAsync(name, DnsRecordKind.Srv, ct);
            if (response.Status == DnsLookupStatus.NoError && response.Srv.Count > 0)
                return (DnsSeedOutcome.Ok, response);

            _logger.LogDebug("Seed {Seed}: SRV {Name} answered {Status} with {Count} records", root, name,
                             response.Status, response.Srv.Count);
            if (name == root)
                outcome = MapStatus(response.Status);
        }

        return (outcome, null);
    }

    /// <summary>
    /// The addresses of <paramref name="targets"/>, looked up concurrently (at most
    /// <see cref="MaxConcurrentAddressLookups"/>, the batch's size). A target not resolved when <paramref name="ct"/>
    /// (the seed's time) runs out is left out; the caller checks its own cancellation.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<IPAddress>>> ResolveTargetsAsync(
        IDnsRecordLookup lookup, string root, IReadOnlyList<string> targets, DnsLookupResponse srvResponse,
        DnsSeedAddressTypes families, CancellationToken ct)
    {
        var resolved = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(MaxConcurrentAddressLookups);
        await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var addresses = await GetAddressesAsync(lookup, root, target, srvResponse, families, ct);
                    lock (resolved)
                        resolved[target] = addresses;
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The seed's time ran out: this target is left out
            }
        }));

        return resolved;
    }

    private async Task<IReadOnlyList<IPAddress>> GetAddressesAsync(IDnsRecordLookup lookup, string root, string target,
                                                                   DnsLookupResponse srvResponse,
                                                                   DnsSeedAddressTypes families, CancellationToken ct)
    {
        var glue = srvResponse.Additional
                              .Where(g => string.Equals(g.Name.TrimEnd('.'), target,
                                                        StringComparison.OrdinalIgnoreCase))
                              .Select(g => g.Address)
                              .Where(a => IsWantedFamily(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a, families))
                              .ToList();
        if (glue.Count > 0)
            return glue;

        var addresses = new List<IPAddress>();
        if (families.HasFlag(DnsSeedAddressTypes.IPv4))
            addresses.AddRange(await QueryAddressesAsync(lookup, root, target, DnsRecordKind.A, ct));
        if (families.HasFlag(DnsSeedAddressTypes.IPv6))
            addresses.AddRange(await QueryAddressesAsync(lookup, root, target, DnsRecordKind.Aaaa, ct));
        return addresses;
    }

    private async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(IDnsRecordLookup lookup, string root, string target,
                                                                     DnsRecordKind kind,
                                                                     CancellationToken ct)
    {
        var response = await lookup.QueryAsync(target, kind, ct);
        if (response.Status == DnsLookupStatus.NoError)
            return response.Addresses;

        _logger.LogDebug("Seed {Seed}: {Kind} {Target} answered {Status}", root, kind, target, response.Status);
        return [];
    }

    private static bool IsWantedFamily(IPAddress address, DnsSeedAddressTypes families) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => families.HasFlag(DnsSeedAddressTypes.IPv4),
        AddressFamily.InterNetworkV6 => families.HasFlag(DnsSeedAddressTypes.IPv6),
        _ => false
    };

    private static DnsSeedOutcome MapStatus(DnsLookupStatus status) => status switch
    {
        DnsLookupStatus.NoError => DnsSeedOutcome.Empty,
        DnsLookupStatus.NxDomain => DnsSeedOutcome.NxDomain,
        DnsLookupStatus.ServFail or DnsLookupStatus.Refused => DnsSeedOutcome.ServerFailure,
        DnsLookupStatus.Timeout => DnsSeedOutcome.Timeout,
        _ => DnsSeedOutcome.Error
    };
}