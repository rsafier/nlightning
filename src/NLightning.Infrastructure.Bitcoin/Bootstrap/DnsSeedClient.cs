using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Bootstrap;

using Domain.Crypto.ValueObjects;
using Domain.Node.Bootstrap;
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
    private readonly IDnsRecordLookup _lookup;
    private readonly ILogger<DnsSeedClient> _logger;
    private readonly BootstrapOptions _options;

    public DnsSeedClient(IDnsRecordLookup lookup, IOptions<NodeOptions> nodeOptions, ILogger<DnsSeedClient> logger)
    {
        _lookup = lookup;
        _logger = logger;
        _options = nodeOptions.Value.Bootstrap;
    }

    /// <inheritdoc />
    public async Task<DnsSeedResult> QuerySeedAsync(string seedRoot, DnsSeedAddressTypes families, int maxResults,
                                                    CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedRoot);
        var root = seedRoot.Trim().TrimEnd('.').ToLowerInvariant();
        var candidates = new List<SeedPeerCandidate>();
        var rejected = 0;
        if (maxResults <= 0)
            return new DnsSeedResult(root, DnsSeedOutcome.Empty, candidates, rejected);

        using var seedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        seedCts.CancelAfter(_options.PerSeedTimeout);
        var token = seedCts.Token;
        try
        {
            var (outcome, srvResponse) = await QuerySrvAsync(root, families, token);
            if (srvResponse is null)
                return new DnsSeedResult(root, outcome, candidates, rejected);

            var seen = new HashSet<(CompactPubKey, IPAddress, ushort)>();
            var records = srvResponse.Srv
                                     .Where(r => !string.IsNullOrWhiteSpace(r.Target))
                                     .DistinctBy(r => r.Target.TrimEnd('.').ToLowerInvariant())
                                     .ToArray();
            Random.Shared.Shuffle(records);
            foreach (var record in records)
            {
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

                var addresses = await GetAddressesAsync(root, target, srvResponse, families, token);
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

            return new DnsSeedResult(root, DnsSeedOutcome.Ok, candidates, rejected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Seed {Seed} timed out after {Timeout}", root, _options.PerSeedTimeout);
            return new DnsSeedResult(root, DnsSeedOutcome.Timeout, candidates, rejected);
        }
    }

    /// <summary>
    /// The SRV answer to use: with <see cref="BootstrapOptions.UseQueryConditions"/> the <c>r0.a&lt;n&gt;</c> query
    /// first, then the bare root, then <c>_nodes._tcp.&lt;root&gt;</c>. Null (with the outcome) when none answered
    /// records.
    /// </summary>
    private async Task<(DnsSeedOutcome Outcome, DnsLookupResponse? Response)> QuerySrvAsync(
        string root, DnsSeedAddressTypes families, CancellationToken ct)
    {
        var names = new List<string>(3);
        if (_options.UseQueryConditions)
            names.Add(new DnsSeedQuery(root, Realm: 0, AddressTypes: (byte)families).ToHostName());
        names.Add(root);
        names.Add(DnsSeedQuery.SrvAlias(root));

        // The outcome of a seed without records is the bare root's answer: the conditional query is only a
        // preference, and the _nodes._tcp alias only a fallback
        var outcome = DnsSeedOutcome.Empty;
        foreach (var name in names)
        {
            var response = await _lookup.QueryAsync(name, DnsRecordKind.Srv, ct);
            if (response.Status == DnsLookupStatus.NoError && response.Srv.Count > 0)
                return (DnsSeedOutcome.Ok, response);

            _logger.LogDebug("Seed {Seed}: SRV {Name} answered {Status} with {Count} records", root, name,
                             response.Status, response.Srv.Count);
            if (name == root)
                outcome = MapStatus(response.Status);
        }

        return (outcome, null);
    }

    private async Task<IReadOnlyList<IPAddress>> GetAddressesAsync(string root, string target,
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
            addresses.AddRange(await QueryAddressesAsync(root, target, DnsRecordKind.A, ct));
        if (families.HasFlag(DnsSeedAddressTypes.IPv6))
            addresses.AddRange(await QueryAddressesAsync(root, target, DnsRecordKind.Aaaa, ct));
        return addresses;
    }

    private async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string root, string target, DnsRecordKind kind,
                                                                     CancellationToken ct)
    {
        var response = await _lookup.QueryAsync(target, kind, ct);
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