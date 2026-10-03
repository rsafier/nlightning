namespace NLightning.Domain.Gossip.Addresses;

/// <summary>
/// The BOLT 7 <c>address descriptor</c> types (<c>node_announcement.addresses</c>, BOLT 1 <c>init.remote_addr</c>).
/// </summary>
public enum AddressDescriptorType : byte
{
    /// <summary>
    /// <c>[4:ipv4_addr][2:port]</c> (length 6).
    /// </summary>
    IPv4 = 1,

    /// <summary>
    /// <c>[16:ipv6_addr][2:port]</c> (length 18).
    /// </summary>
    IPv6 = 2,

    /// <summary>
    /// Deprecated Tor v2 onion service: <c>[10:onion_addr][2:port]</c> (length 12). Decoded, never announced, and
    /// ignored by receivers (BOLT 7 "SHOULD ignore Tor v2 onion services").
    /// </summary>
    TorV2 = 3,

    /// <summary>
    /// Tor v3 onion service: <c>[35:onion_addr][2:port]</c> (length 37); the 35 bytes are
    /// <c>pubkey(32) || checksum(2) || version(1)</c>.
    /// </summary>
    TorV3 = 4,

    /// <summary>
    /// DNS hostname: <c>[1:hostname_len][hostname_len:hostname][2:port]</c> (length up to 258); ASCII only.
    /// </summary>
    Dns = 5
}