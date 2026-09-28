using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NLightning.Infrastructure.Protocol.Models;

using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Node.ValueObjects;

/// <summary>
/// Represents a peer address.
/// </summary>
public sealed partial class PeerAddress : IEquatable<PeerAddress>
{
    [GeneratedRegex(@"\d+")]
    private static partial Regex OnlyDigitsRegex();

    /// <summary>
    /// Gets the public key.
    /// </summary>
    public CompactPubKey PubKey { get; }

    /// <summary>
    /// Gets the host.
    /// </summary>
    public IPAddress Host { get; }

    /// <summary>
    /// Gets the port.
    /// </summary>
    public int Port { get; }

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

        // Check if the address starts with http
        if (parts[1].StartsWith("http"))
        {
            // split on first // to get the address
            var hostPort = parts[1].Split("//")[1].Split(":");
            Host = System.Net.Dns.GetHostAddresses(hostPort[0])[0];

            // Port may have an extra / at the end. Use regex to keep only the number in the port
            Port = int.Parse(OnlyDigitsRegex().Match(hostPort[1]).Value);
        }
        else
        {
            (Host, Port) = ParseHostPort(parts[1]);
        }
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

        // Check if the address starts with http
        if (address.StartsWith("http"))
        {
            // split on first // to get the address
            var host = address.Split("//")[1];
            Host = System.Net.Dns.GetHostAddresses(host.Split(":")[0])[0];

            // Port may have an extra / at the end. Use regex to keep only the number in the port
            Port = int.Parse(OnlyDigitsRegex().Match(host.Split(":")[1]).Value);
        }
        else
        {
            (Host, Port) = ParseHostPort(address);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAddress"/> class.
    /// </summary>
    /// <param name="pubKey">The public key.</param>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    public PeerAddress(CompactPubKey pubKey, string host, int port)
    {
        PubKey = pubKey;
        Host = IPAddress.Parse(host);
        Port = port;
    }

    /// <summary>
    /// Returns a string that represents the address.
    /// </summary>
    /// <returns>A string in the format of "pubkey@host:port".</returns>
    public override string ToString()
    {
        return Host.AddressFamily == AddressFamily.InterNetworkV6 ? $"{PubKey}@[{Host}]:{Port}" : $"{PubKey}@{Host}:{Port}";
    }

    /// <summary>
    /// Parses <c>ip:port</c>, or <c>[ipv6]:port</c> with the IPv6 address in brackets (NL-113 D-B10-6).
    /// </summary>
    /// <exception cref="FormatException">The address is neither form (an IPv6 address must be in brackets).</exception>
    private static (IPAddress Host, int Port) ParseHostPort(string hostPort)
    {
        string host;
        string port;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0)
                throw new FormatException("Invalid IPv6 address format, should be [ipv6]:port");

            host = hostPort[1..close];
            port = hostPort[(close + 2)..];
            var address = IPAddress.Parse(host);
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Only an IPv6 address goes in brackets");

            return (address, ParsePort(port));
        }

        var parts = hostPort.Split(':');
        if (parts.Length != 2)
            throw new FormatException("Invalid address format, should be host:port (an IPv6 host in brackets)");

        host = parts[0];
        port = parts[1];
        return (IPAddress.Parse(host), ParsePort(port));
    }

    private static int ParsePort(string port)
    {
        var value = int.Parse(port, CultureInfo.InvariantCulture);
        if (value is < 1 or > ushort.MaxValue)
            throw new FormatException($"Invalid port {port}");

        return value;
    }

    public bool Equals(PeerAddress? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return PubKey.Equals(other.PubKey) && Host.Equals(other.Host) && Port == other.Port;
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is PeerAddress other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PubKey, Host, Port);
    }
}