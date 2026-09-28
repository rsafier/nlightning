using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Protocol.Dns;

using Domain.Node.Options;

/// <summary>
/// <see cref="IDnsRecordLookup"/> over the DnsClient <see cref="LookupClient"/> (NL-113). The client is built on the
/// first query, never in the constructor, so resolving the service does no I/O (tests stay hermetic).
/// </summary>
/// <remarks>
/// Over TCP by default (<see cref="BootstrapOptions.Transport"/>); UDP asks with EDNS0 (4096 bytes) and retries over
/// TCP on a truncated answer. No cache: a seed's answer is used once. A failing name server passes the query to the
/// next one. Names come back without their final dot; nothing is validated here.
/// </remarks>
internal sealed class DnsClientRecordLookup : IDnsRecordLookup
{
    private readonly ILogger<DnsClientRecordLookup> _logger;
    private readonly BootstrapOptions _options;
    private readonly Lazy<LookupClient> _client;

    public DnsClientRecordLookup(IOptions<NodeOptions> nodeOptions, ILogger<DnsClientRecordLookup> logger)
    {
        _logger = logger;
        _options = nodeOptions.Value.Bootstrap;
        _client = new Lazy<LookupClient>(() => new LookupClient(BuildOptions(_options)),
                                         LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether the <see cref="LookupClient"/> exists yet (tests).</summary>
    internal bool IsClientCreated => _client.IsValueCreated;

    /// <inheritdoc />
    public async Task<DnsLookupResponse> QueryAsync(string name, DnsRecordKind kind, CancellationToken ct)
    {
        var queryType = kind switch
        {
            DnsRecordKind.Srv => QueryType.SRV,
            DnsRecordKind.A => QueryType.A,
            DnsRecordKind.Aaaa => QueryType.AAAA,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        IDnsQueryResponse response;
        try
        {
            response = await _client.Value.QueryAsync(name, queryType, QueryClass.IN, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DnsResponseException e) when (e.Code == DnsResponseCode.ConnectionTimeout)
        {
            return DnsLookupResponse.Of(DnsLookupStatus.Timeout);
        }
        catch (DnsResponseException e)
        {
            _logger.LogDebug(e, "DNS query {Name} {Type} failed", name, queryType);
            return DnsLookupResponse.Of(MapCode(e.Code));
        }
        catch (OperationCanceledException)
        {
            return DnsLookupResponse.Of(DnsLookupStatus.Timeout);
        }

        return Map(response);
    }

    /// <summary>The DnsClient settings of <paramref name="options"/> (no I/O; also read by tests).</summary>
    internal static LookupClientOptions BuildOptions(BootstrapOptions options)
    {
        var servers = options.NameServers
                             .Select(s => BootstrapOptions.TryParseNameServer(s, out var endPoint)
                                              ? endPoint
                                              : throw new ArgumentException($"Invalid name server '{s}'"))
                             .ToArray();
        var clientOptions = servers.Length > 0 ? new LookupClientOptions(servers) : new LookupClientOptions();
        clientOptions.UseTcpOnly = options.Transport == DnsSeedTransport.Tcp;
        clientOptions.UseTcpFallback = true;
        clientOptions.ExtendedDnsBufferSize = 4096;
        clientOptions.Timeout = options.QueryTimeout;
        clientOptions.Retries = 1;
        clientOptions.UseCache = false;
        clientOptions.ThrowDnsErrors = false;
        clientOptions.ContinueOnDnsError = true;
        return clientOptions;
    }

    private static DnsLookupResponse Map(IDnsQueryResponse response)
    {
        var status = MapCode((DnsResponseCode)response.Header.ResponseCode);
        var srv = response.Answers.SrvRecords()
                          .Select(r => new DnsSrv(r.Priority, r.Weight, r.Port, TrimDot(r.Target.Value)))
                          .ToList();
        var addresses = response.Answers.OfType<AddressRecord>().Select(r => r.Address).ToList();
        var glue = response.Additionals.OfType<AddressRecord>()
                           .Select(r => new DnsGlue(TrimDot(r.DomainName.Value), r.Address))
                           .ToList();
        return new DnsLookupResponse(status, srv, addresses, glue);
    }

    private static DnsLookupStatus MapCode(DnsResponseCode code) => code switch
    {
        DnsResponseCode.NoError => DnsLookupStatus.NoError,
        DnsResponseCode.NotExistentDomain => DnsLookupStatus.NxDomain,
        DnsResponseCode.ServerFailure => DnsLookupStatus.ServFail,
        DnsResponseCode.Refused => DnsLookupStatus.Refused,
        DnsResponseCode.ConnectionTimeout => DnsLookupStatus.Timeout,
        _ => DnsLookupStatus.Other
    };

    private static string TrimDot(string name) => name.TrimEnd('.');
}