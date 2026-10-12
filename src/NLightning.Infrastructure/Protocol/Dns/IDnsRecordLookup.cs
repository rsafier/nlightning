using System.Net;

namespace NLightning.Infrastructure.Protocol.Dns;

/// <summary>
/// The raw DNS queries of BOLT 10 bootstrap (NL-113), over BCL types only so the seed client can be tested without a
/// resolver. Implemented by <see cref="DnsClientRecordLookup"/>.
/// </summary>
public interface IDnsRecordLookup
{
    /// <summary>
    /// Queries <paramref name="name"/> for records of <paramref name="kind"/>. DNS failures (NXDOMAIN, SERVFAIL,
    /// timeout) come back as the response's status; only the caller's cancellation throws.
    /// </summary>
    Task<DnsLookupResponse> QueryAsync(string name, DnsRecordKind kind, CancellationToken ct);
}

/// <summary>A DNS response, reduced to what BOLT 10 reads.</summary>
/// <param name="Status">The response code, or <see cref="DnsLookupStatus.Timeout"/> when no server answered.</param>
/// <param name="Srv">The SRV answers.</param>
/// <param name="Addresses">The A or AAAA answers.</param>
/// <param name="Additional">The A and AAAA records of the additional section (glue).</param>
public sealed record DnsLookupResponse(
    DnsLookupStatus Status,
    IReadOnlyList<DnsSrv> Srv,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<DnsGlue> Additional)
{
    /// <summary>A response without records.</summary>
    public static DnsLookupResponse Of(DnsLookupStatus status) => new(status, [], [], []);
}

/// <summary>An SRV record; <paramref name="Target"/> has no final dot.</summary>
public sealed record DnsSrv(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>An A or AAAA record of the additional section; <paramref name="Name"/> has no final dot.</summary>
public sealed record DnsGlue(string Name, IPAddress Address);

/// <summary>The record types BOLT 10 bootstrap asks for.</summary>
public enum DnsRecordKind
{
    Srv,
    A,
    Aaaa
}

/// <summary>A DNS response code, as far as BOLT 10 bootstrap cares.</summary>
public enum DnsLookupStatus
{
    NoError,
    NxDomain,
    ServFail,
    Timeout,
    Refused,
    Other
}