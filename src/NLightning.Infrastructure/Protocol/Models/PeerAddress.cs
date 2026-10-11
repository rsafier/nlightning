using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NLightning.Infrastructure.Protocol.Models;

using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Gossip.Addresses;
using NLightning.Domain.Node.ValueObjects;

/// <summary>
/// Represents a peer address: a node id and where to dial it, an IPv4 or IPv6 address, a Tor v3 <c>.onion</c> name or
/// a DNS host name.
/// </summary>
/// <remarks>
/// A host name is kept as written and never resolved here: a direct dial resolves it when it connects, and a dial
/// through Tor hands it to the SOCKS5 proxy, so a Tor-only node never asks a local resolver (or leaks an onion name to
/// one).
/// </remarks>
public sealed partial class PeerAddress : IEquatable<PeerAddress>
{
    [GeneratedRegex(@"\d+")]
    private static partial Regex OnlyDigitsRegex();

    /// <summary>
    /// Gets the public key.
    /// </summary>
    public CompactPubKey PubKey { get; }

    /// <summary>
    /// Gets the host: an IP address as text (IPv6 without brackets), a lower-case <c>&lt;56 chars&gt;.onion</c> name or
    /// a DNS host name.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the IP address of an <see cref="AddressDescriptorType.IPv4"/> or <see cref="AddressDescriptorType.IPv6"/>
    /// host; null for an onion or DNS host.
    /// </summary>
    public IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets the port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets what the host is: <see cref="AddressDescriptorType.IPv4"/>, <see cref="AddressDescriptorType.IPv6"/>,
    /// <see cref="AddressDescriptorType.TorV3"/> or <see cref="AddressDescriptorType.Dns"/>.
    /// </summary>
    public AddressDescriptorType Type { get; }

    /// <summary>
    /// True for a Tor v3 onion service.
    /// </summary>
    public bool IsOnion => Type == AddressDescriptorType.TorV3;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddress"/> class.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <remarks>
    /// The address is in the format of "pubkey@host:port".
    /// </remarks>
    public PeerAddress(string address)
    {
        var parts = address.Split('@');
        if (parts.Length != 2)
            throw new FormatException("Invalid address format, should be pubkey@host:port");

        PubKey = new CompactPubKey(Convert.FromHexString(parts[0]));
        (Host, IpAddress, Port, Type) = IsUrl(parts[1]) ? ParseHttp(parts[1]) : ParseHostPort(parts[1]);
    }

    public PeerAddress(PeerAddressInfo peerAddressInfo) : this(peerAddressInfo.Address)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddress"/> class.
    /// </summary>
    /// <param name="pubKey">The public key.</param>
    /// <param name="address">The address.</param>
    /// <remarks>
    /// The address is in the format of "http://host:port" or "host:port".
    /// </remarks>
    public PeerAddress(CompactPubKey pubKey, string address)
    {
        PubKey = pubKey;
        (Host, IpAddress, Port, Type) = IsUrl(address) ? ParseHttp(address) : ParseHostPort(address);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddress"/> class.
    /// </summary>
    /// <param name="pubKey">The public key.</param>
    /// <param name="host">The host: an IP address, a <c>.onion</c> name or a DNS host name.</param>
    /// <param name="port">The port.</param>
    public PeerAddress(CompactPubKey pubKey, string host, int port)
    {
        PubKey = pubKey;
        (Host, IpAddress, Type) = ParseHost(host);
        Port = port;
    }

    /// <summary>
    /// Returns a string that represents the address.
    /// </summary>
    /// <returns>A string in the format of "pubkey@host:port".</returns>
    public override string ToString()
    {
        return Type == AddressDescriptorType.IPv6 ? $"{PubKey}@[{Host}]:{Port}" : $"{PubKey}@{Host}:{Port}";
    }

    /// <summary>
    /// True for an <c>http://</c> or <c>https://</c> URL (scheme case-insensitive); a host name that merely starts with
    /// "http" (<c>httpnode.example.com:9735</c>) is not one (NL-586).
    /// </summary>
    private static bool IsUrl(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
     || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>http://host:port/</c>: the scheme, a path and a trailing slash are dropped.
    /// </summary>
    private static (string, IPAddress?, int, AddressDescriptorType) ParseHttp(string address)
    {
        // Everything after the scheme's "//"
        var hostPort = address[(address.IndexOf("//", StringComparison.Ordinal) + 2)..];
        var separator = hostPort.LastIndexOf(':');
        if (separator <= 0)
            throw new FormatException("Invalid address format, should be http://host:port");

        var host = hostPort[..separator];
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        // Port may have an extra / at the end. Use regex to keep only the number in the port
        var (text, ip, type) = ParseHost(host);
        return (text, ip, ParsePort(OnlyDigitsRegex().Match(hostPort[(separator + 1)..]).Value), type);
    }

    /// <summary>
    /// Parses <c>host:port</c>, or <c>[ipv6]:port</c> with the IPv6 address in brackets (NL-113 D-B10-6). The host is
    /// an IP address, a Tor v3 <c>.onion</c> name or a DNS host name.
    /// </summary>
    /// <exception cref="FormatException">The address is neither form (an IPv6 address must be in brackets).</exception>
    private static (string Host, IPAddress? IpAddress, int Port, AddressDescriptorType Type) ParseHostPort(
        string hostPort)
    {
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0)
                throw new FormatException("Invalid IPv6 address format, should be [ipv6]:port");

            var address = IPAddress.Parse(hostPort[1..close]);
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Only an IPv6 address goes in brackets");

            return (address.ToString(), address, ParsePort(hostPort[(close + 2)..]), AddressDescriptorType.IPv6);
        }

        var parts = hostPort.Split(':');
        if (parts.Length != 2)
            throw new FormatException("Invalid address format, should be host:port (an IPv6 host in brackets)");

        var (host, ip, type) = ParseHost(parts[0]);
        return (host, ip, ParsePort(parts[1]), type);
    }

    /// <summary>
    /// Reads a host without a port: an IP address, a Tor v3 <c>.onion</c> name (version and checksum checked) or a DNS
    /// host name (ASCII letters, digits, '-', '_' and '.').
    /// </summary>
    private static (string Host, IPAddress? IpAddress, AddressDescriptorType Type) ParseHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new FormatException("The host is empty");

        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetworkV6
                       ? (ip.ToString(), ip, AddressDescriptorType.IPv6)
                       : (ip.ToString(), ip, AddressDescriptorType.IPv4);

        if (host.Contains(':'))
            throw new FormatException($"'{host}' is not a host; an IPv6 address goes in brackets ([ipv6]:port)");

        if (OnionV3Address.IsOnionHost(host))
        {
            if (!OnionV3Address.TryParse(host, out var onion, out var error))
                throw new FormatException(error);

            return (OnionV3Address.ToHostName(onion), null, AddressDescriptorType.TorV3);
        }

        if (host.Length > 255 || Uri.CheckHostName(host) != UriHostNameType.Dns
                              || host.Any(c => c > 0x7F))
            throw new FormatException($"'{host}' is not an IP address, a .onion name or a DNS host name");

        return (host, null, AddressDescriptorType.Dns);
    }

    private static int ParsePort(string port)
    {
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
         || value is < 1 or > ushort.MaxValue)
            throw new FormatException($"Invalid port {port}");

        return value;
    }

    public bool Equals(PeerAddress? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return PubKey.Equals(other.PubKey) && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
                                           && Port == other.Port;
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is PeerAddress other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PubKey, StringComparer.OrdinalIgnoreCase.GetHashCode(Host), Port);
    }
}