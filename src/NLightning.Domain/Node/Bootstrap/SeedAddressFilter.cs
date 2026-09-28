using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// Drops addresses a BOLT 10 DNS seed should never hand out: an unspecified, loopback, private, shared, link-local,
/// multicast, reserved, documentation, benchmarking or IETF special-purpose address, or port 0. Seed answers are
/// unauthenticated DNS, so an address inside our own network must not make us dial it.
/// </summary>
/// <remarks>
/// An IPv6 address that embeds an IPv4 address (IPv4-mapped <c>::ffff:0:0/96</c>, NAT64 <c>64:ff9b::/96</c>, 6to4
/// <c>2002::/16</c>) is judged by its IPv4 address; IPv4-compatible <c>::/96</c>, local-use NAT64
/// <c>64:ff9b:1::/48</c>, discard-only <c>100::/64</c> and site-local <c>fec0::/10</c> are refused.
/// </remarks>
public static class SeedAddressFilter
{
    /// <summary>
    /// True when <paramref name="address"/>:<paramref name="port"/> may be dialed. An IPv4-mapped IPv6 address is
    /// judged as its IPv4 address.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="port">The port (the SRV record's).</param>
    /// <param name="allowNonRoutable">Accept private, loopback, link-local and documentation ranges (local test seeds).
    /// Port 0, unspecified and multicast addresses are refused either way.</param>
    /// <param name="reason">Why the address was refused; empty when it is usable.</param>
    public static bool IsUsable(IPAddress address, ushort port, bool allowNonRoutable, out string reason)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (port == 0)
        {
            reason = "port 0";
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        reason = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => CheckIPv4(address.GetAddressBytes(), allowNonRoutable),
            AddressFamily.InterNetworkV6 => CheckIPv6(address.GetAddressBytes(), allowNonRoutable),
            _ => "not an IP address"
        };
        return reason.Length == 0;
    }

    private static string CheckIPv4(byte[] b, bool allowNonRoutable)
    {
        if (b[0] == 0)
            return "unspecified IPv4 (0.0.0.0/8)";
        if (b[0] >= 224)
            return "multicast or reserved IPv4 (224.0.0.0/4 and above)";
        if (allowNonRoutable)
            return string.Empty;

        return b switch
        {
            [127, ..] => "loopback IPv4 (127.0.0.0/8)",
            [10, ..] => "private IPv4 (10.0.0.0/8)",
            [172, >= 16 and <= 31, ..] => "private IPv4 (172.16.0.0/12)",
            [192, 168, ..] => "private IPv4 (192.168.0.0/16)",
            [100, >= 64 and <= 127, ..] => "shared IPv4 (100.64.0.0/10)",
            [169, 254, ..] => "link-local IPv4 (169.254.0.0/16)",
            [192, 0, 0, ..] => "IETF protocol assignments IPv4 (192.0.0.0/24)",
            [192, 0, 2, ..] => "documentation IPv4 (192.0.2.0/24)",
            [198, 51, 100, ..] => "documentation IPv4 (198.51.100.0/24)",
            [203, 0, 113, ..] => "documentation IPv4 (203.0.113.0/24)",
            [198, 18 or 19, ..] => "benchmarking IPv4 (198.18.0.0/15)",
            _ => string.Empty
        };
    }

    private static string CheckIPv6(byte[] b, bool allowNonRoutable)
    {
        if (b.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            return "unspecified IPv6 (::)";
        if (b[0] == 0xff)
            return "multicast IPv6 (ff00::/8)";

        // An embedded IPv4 address is judged as IPv4, so a NAT64 or 6to4 gateway cannot reach what IPv4 refuses
        if (b is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..])
            return Embedded("NAT64 IPv6 (64:ff9b::/96)", CheckIPv4(b[12..16], allowNonRoutable));
        if (b is [0x20, 0x02, ..])
            return Embedded("6to4 IPv6 (2002::/16)", CheckIPv4(b[2..6], allowNonRoutable));
        if (allowNonRoutable)
            return string.Empty;

        if (b.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && b[15] == 1)
            return "loopback IPv6 (::1)";
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)
            return "link-local IPv6 (fe80::/10)";
        if ((b[0] & 0xfe) == 0xfc)
            return "unique local IPv6 (fc00::/7)";
        if (b is [0x20, 0x01, 0x0d, 0xb8, ..])
            return "documentation IPv6 (2001:db8::/32)";
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            return "IPv4-compatible IPv6 (::/96)";
        if (b is [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01, ..])
            return "local-use NAT64 IPv6 (64:ff9b:1::/48)";
        if (b is [0x01, 0x00, 0, 0, 0, 0, 0, 0, ..])
            return "discard-only IPv6 (100::/64)";
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0)
            return "site-local IPv6 (fec0::/10)";
        return string.Empty;

        static string Embedded(string prefix, string ipv4Reason) =>
            ipv4Reason.Length == 0 ? string.Empty : $"{prefix} embedding {ipv4Reason}";
    }
}