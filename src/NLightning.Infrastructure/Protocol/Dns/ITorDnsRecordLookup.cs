namespace NLightning.Infrastructure.Protocol.Dns;

/// <summary>
/// The resolver a Tor-only node asks the BOLT 10 seeds through (NL-571,
/// <c>Node:Bootstrap:TorNameServer</c>): DNS over TCP through Tor's SOCKS5 port, since Tor's own resolution does
/// A/AAAA only while the seeds answer SRV. Implemented by <see cref="TorSocksDnsRecordLookup"/>.
/// </summary>
public interface ITorDnsRecordLookup : IDnsRecordLookup
{
    /// <summary>
    /// True when the seeds may be asked through Tor: Tor is on and <c>Node:Bootstrap:TorNameServer</c> parses. When
    /// false every query answers <see cref="DnsLookupStatus.Other"/>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The resolver as configured, <c>host[:port]</c> (for the logs).</summary>
    string NameServer { get; }
}