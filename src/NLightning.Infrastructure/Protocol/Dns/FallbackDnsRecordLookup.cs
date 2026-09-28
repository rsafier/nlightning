using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Protocol.Dns;

using Domain.Node.Options;

/// <summary>
/// The fallback resolvers of BOLT 10 bootstrap (D-B10-7): a <see cref="DnsClientRecordLookup"/> over
/// <see cref="BootstrapOptions.FallbackNameServers"/>, available when
/// <see cref="BootstrapOptions.UsesFallbackResolvers"/>. Built lazily like the primary lookup, so resolving it does no
/// I/O.
/// </summary>
internal sealed class FallbackDnsRecordLookup : IFallbackDnsRecordLookup
{
    private readonly DnsClientRecordLookup _inner;

    public FallbackDnsRecordLookup(IOptions<NodeOptions> nodeOptions, ILogger<FallbackDnsRecordLookup> logger)
    {
        var options = nodeOptions.Value.Bootstrap;
        IsAvailable = options.UsesFallbackResolvers;
        NameServers = [.. options.FallbackNameServers];
        _inner = new DnsClientRecordLookup(options, NameServers, logger);
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> NameServers { get; }

    /// <summary>Whether the fallback's <c>LookupClient</c> exists yet (tests).</summary>
    internal bool IsClientCreated => _inner.IsClientCreated;

    /// <inheritdoc />
    public Task<DnsLookupResponse> QueryAsync(string name, DnsRecordKind kind, CancellationToken ct) =>
        IsAvailable
            ? _inner.QueryAsync(name, kind, ct)
            : Task.FromResult(DnsLookupResponse.Of(DnsLookupStatus.Other));
}