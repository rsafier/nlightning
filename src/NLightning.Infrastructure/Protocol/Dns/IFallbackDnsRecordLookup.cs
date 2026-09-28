namespace NLightning.Infrastructure.Protocol.Dns;

/// <summary>
/// The resolvers BOLT 10 bootstrap asks for a seed the system resolvers gave no candidate for (D-B10-7,
/// <c>Node:Bootstrap:FallbackNameServers</c>). Implemented by <see cref="FallbackDnsRecordLookup"/>.
/// </summary>
public interface IFallbackDnsRecordLookup : IDnsRecordLookup
{
    /// <summary>
    /// True when the fallback may be used: <c>Node:Bootstrap:FallbackToPublicResolvers</c>, no configured
    /// <c>NameServers</c> and a fallback list. When false every query answers <see cref="DnsLookupStatus.Other"/>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The fallback resolvers, <c>ip[:port]</c> (for the logs).</summary>
    IReadOnlyList<string> NameServers { get; }
}