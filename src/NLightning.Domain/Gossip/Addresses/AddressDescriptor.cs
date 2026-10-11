using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NLightning.Domain.Gossip.Addresses;

/// <summary>
/// One BOLT 7 <c>address descriptor</c>: a type, the address bytes (without the type byte, the DNS length byte and the
/// port) and a port. Immutable; the bytes are copied in and out.
/// </summary>
/// <remarks>
/// <para>Address byte lengths per type: IPv4 4, IPv6 16, Tor v2 10, Tor v3 35, DNS 1-255 ASCII bytes. The wire form
/// (<see cref="AddressDescriptorCodec"/>) adds the type byte, the DNS length byte and the big-endian port.</para>
/// <para>Port 0 is representable (a receiver must be able to see and then ignore it), but
/// <see cref="AddressDescriptorCodec.EncodeList"/> refuses it (BOLT 7 "MUST NOT create an address descriptor with
/// port equal to 0").</para>
/// </remarks>
public sealed class AddressDescriptor : IEquatable<AddressDescriptor>
{
    /// <summary>The address length of an IPv4 descriptor.</summary>
    public const int IPv4AddressLength = 4;

    /// <summary>The address length of an IPv6 descriptor.</summary>
    public const int IPv6AddressLength = 16;

    /// <summary>The address length of a (deprecated) Tor v2 descriptor.</summary>
    public const int TorV2AddressLength = 10;

    /// <summary>The address length of a Tor v3 descriptor (<c>pubkey(32) || checksum(2) || version(1)</c>).</summary>
    public const int TorV3AddressLength = 35;

    /// <summary>The longest DNS hostname (its length is one byte on the wire).</summary>
    public const int MaxDnsHostnameLength = 255;

    private const string OnionSuffix = ".onion";

    private readonly byte[] _address;

    /// <summary>
    /// Creates a descriptor from its raw address bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The type is not 1-5, or the bytes do not fit the type (wrong length, an
    /// empty hostname or one with a character other than ASCII letters, digits, '-', '_' and '.').</exception>
    public AddressDescriptor(AddressDescriptorType type, ReadOnlySpan<byte> address, ushort port)
    {
        if (!TryValidate(type, address, out var error))
            throw new ArgumentException(error, nameof(address));

        Type = type;
        _address = address.ToArray();
        Port = port;
    }

    /// <summary>
    /// The descriptor type.
    /// </summary>
    public AddressDescriptorType Type { get; }

    /// <summary>
    /// The address bytes (a copy): 4 or 16 IP bytes, the 10 or 35 onion bytes, or the ASCII hostname.
    /// </summary>
    public byte[] Address => _address.ToArray();

    /// <summary>
    /// The TCP port.
    /// </summary>
    public ushort Port { get; }

    /// <summary>
    /// The host as text: dotted IPv4, IPv6 (RFC 5952 via <see cref="IPAddress"/>), <c>&lt;base32&gt;.onion</c> for
    /// Tor, or the hostname.
    /// </summary>
    public string Host => Type switch
    {
        AddressDescriptorType.IPv4 or AddressDescriptorType.IPv6 => new IPAddress(_address).ToString(),
        AddressDescriptorType.TorV2 or AddressDescriptorType.TorV3 => OnionBase32.Encode(_address) + OnionSuffix,
        _ => Encoding.ASCII.GetString(_address)
    };

    /// <summary>
    /// The number of wire bytes, including the type byte (7, 19, 13, 38 or <c>4 + hostname length</c>).
    /// </summary>
    public int EncodedLength => 1 + (Type == AddressDescriptorType.Dns ? 1 : 0) + _address.Length + 2;

    /// <summary>
    /// Creates an IPv4 or IPv6 descriptor (an IPv4-mapped IPv6 address stays IPv6).
    /// </summary>
    public static AddressDescriptor FromIpAddress(IPAddress ipAddress, ushort port)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => new AddressDescriptor(AddressDescriptorType.IPv4,
                                                                ipAddress.GetAddressBytes(), port),
            AddressFamily.InterNetworkV6 when ipAddress.ScopeId == 0 =>
                new AddressDescriptor(AddressDescriptorType.IPv6, ipAddress.GetAddressBytes(), port),
            _ => throw new ArgumentException($"Unsupported address {ipAddress}.", nameof(ipAddress))
        };
    }

    /// <summary>
    /// Creates a DNS descriptor. A non-ASCII (internationalized) name is converted to Punycode first, as BOLT 7
    /// requires.
    /// </summary>
    public static AddressDescriptor FromDnsHostname(string hostname, ushort port)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname);
        var ascii = new IdnMapping().GetAscii(hostname);
        return new AddressDescriptor(AddressDescriptorType.Dns, Encoding.ASCII.GetBytes(ascii), port);
    }

    /// <summary>
    /// Creates a descriptor of <paramref name="type"/> from a host string: an IP literal for types 1/2, a
    /// <c>&lt;base32&gt;.onion</c> name (the suffix is optional) for 3/4, or a hostname for 5. For Tor v3 the legacy
    /// 70-character hex form of the 35 bytes is also accepted.
    /// </summary>
    /// <exception cref="ArgumentException">The host does not parse as that type.</exception>
    public static AddressDescriptor FromHost(AddressDescriptorType type, string host, ushort port)
    {
        ArgumentNullException.ThrowIfNull(host);
        switch (type)
        {
            case AddressDescriptorType.IPv4:
            case AddressDescriptorType.IPv6:
                {
                    if (!IPAddress.TryParse(host, out var ip))
                        throw new ArgumentException($"'{host}' is not an IP address.", nameof(host));

                    var expected = type == AddressDescriptorType.IPv4
                                       ? AddressFamily.InterNetwork
                                       : AddressFamily.InterNetworkV6;
                    if (ip.AddressFamily != expected)
                        throw new ArgumentException($"'{host}' is not an {type} address.", nameof(host));

                    return FromIpAddress(ip, port);
                }
            case AddressDescriptorType.TorV2:
            case AddressDescriptorType.TorV3:
                {
                    var length = type == AddressDescriptorType.TorV2 ? TorV2AddressLength : TorV3AddressLength;
                    var name = host.EndsWith(OnionSuffix, StringComparison.OrdinalIgnoreCase)
                                   ? host[..^OnionSuffix.Length]
                                   : host;
                    var bytes = OnionBase32.TryDecode(name, length);
                    if (bytes is null && type == AddressDescriptorType.TorV3 && name.Length == 2 * length)
                        bytes = TryFromHex(name);

                    if (bytes is null)
                        throw new ArgumentException($"'{host}' is not a {type} onion address.", nameof(host));

                    return new AddressDescriptor(type, bytes, port);
                }
            case AddressDescriptorType.Dns:
                return FromDnsHostname(host, port);
            default:
                throw new ArgumentException($"Unknown address descriptor type {(byte)type}.", nameof(type));
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> is one of the five BOLT 7 types.
    /// </summary>
    public static bool IsKnownType(byte type) => type is >= 1 and <= 5;

    /// <summary>
    /// Checks that <paramref name="address"/> fits <paramref name="type"/>.
    /// </summary>
    public static bool TryValidate(AddressDescriptorType type, ReadOnlySpan<byte> address, out string? error)
    {
        error = type switch
        {
            AddressDescriptorType.IPv4 when address.Length != IPv4AddressLength => "An IPv4 address is 4 bytes.",
            AddressDescriptorType.IPv6 when address.Length != IPv6AddressLength => "An IPv6 address is 16 bytes.",
            AddressDescriptorType.TorV2 when address.Length != TorV2AddressLength =>
                "A Tor v2 onion address is 10 bytes.",
            AddressDescriptorType.TorV3 when address.Length != TorV3AddressLength =>
                "A Tor v3 onion address is 35 bytes.",
            AddressDescriptorType.Dns when address.Length is 0 or > MaxDnsHostnameLength =>
                "A DNS hostname is 1 to 255 bytes.",
            AddressDescriptorType.Dns when !IsHostname(address) =>
                "A DNS hostname must be ASCII (Punycode) letters, digits, '-', '_' and '.'.",
            AddressDescriptorType.IPv4 or AddressDescriptorType.IPv6 or AddressDescriptorType.TorV2
                or AddressDescriptorType.TorV3 or AddressDescriptorType.Dns => null,
            _ => $"Unknown address descriptor type {(byte)type}."
        };

        return error is null;
    }

    /// <summary>
    /// <c>host:port</c>, with the IPv6 host in brackets.
    /// </summary>
    public override string ToString() =>
        Type == AddressDescriptorType.IPv6 ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public bool Equals(AddressDescriptor? other) =>
        other is not null && Type == other.Type && Port == other.Port && _address.AsSpan().SequenceEqual(other._address);

    public override bool Equals(object? obj) => obj is AddressDescriptor other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Port);
        hash.AddBytes(_address);
        return hash.ToHashCode();
    }

    /// <summary>
    /// BOLT 7 hostnames are ASCII (Punycode for internationalized names). Only the LDH characters (letters, digits,
    /// '-') plus '.' and '_' are accepted, so control characters, spaces, '/' and ':' never reach a log line or a
    /// connect string.
    /// </summary>
    private static bool IsHostname(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is not ((>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')
                          or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'.' or (byte)'_'))
                return false;

        return true;
    }

    private static byte[]? TryFromHex(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}