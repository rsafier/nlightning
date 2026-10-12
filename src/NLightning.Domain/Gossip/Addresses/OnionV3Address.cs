using System.Text;

namespace NLightning.Domain.Gossip.Addresses;

using Crypto.Hashes;

/// <summary>
/// Tor v3 onion service addresses (Tor rend-spec-v3 §6 "Encoding onion addresses"):
/// <c>base32(PUBKEY || CHECKSUM || VERSION) + ".onion"</c>, with <c>PUBKEY</c> the 32-byte ed25519 identity key,
/// <c>VERSION</c> 3 and <c>CHECKSUM = SHA3-256(".onion checksum" || PUBKEY || VERSION)[:2]</c>. The 35 bytes are what a
/// BOLT 7 Tor v3 address descriptor carries.
/// </summary>
public static class OnionV3Address
{
    /// <summary>The DNS suffix of every onion address.</summary>
    public const string Suffix = ".onion";

    /// <summary>The version byte of a v3 address.</summary>
    public const byte Version = 3;

    /// <summary>The length of the ed25519 public key.</summary>
    public const int PublicKeyLength = 32;

    /// <summary>The length of the host name without <see cref="Suffix"/> (56 base32 characters).</summary>
    public const int HostLabelLength = 56;

    private static readonly byte[] s_checksumPrefix = Encoding.ASCII.GetBytes(".onion checksum");

    /// <summary>
    /// The 35 address bytes of the service with ed25519 public key <paramref name="publicKey"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    public static byte[] FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength)
            throw new ArgumentException($"An ed25519 public key is {PublicKeyLength} bytes.", nameof(publicKey));

        var address = new byte[AddressDescriptor.TorV3AddressLength];
        publicKey.CopyTo(address);
        var checksum = Checksum(publicKey);
        address[32] = checksum[0];
        address[33] = checksum[1];
        address[34] = Version;
        return address;
    }

    /// <summary>
    /// True when <paramref name="address"/> is 35 bytes with version 3 and a matching checksum. Tor refuses any other
    /// v3 name, so dialing one only costs a circuit.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> address)
    {
        if (address.Length != AddressDescriptor.TorV3AddressLength || address[34] != Version)
            return false;

        var checksum = Checksum(address[..PublicKeyLength]);
        return address[32] == checksum[0] && address[33] == checksum[1];
    }

    /// <summary>
    /// The host name <c>&lt;56 base32 characters&gt;.onion</c> (lower case) of <paramref name="address"/>.
    /// </summary>
    public static string ToHostName(ReadOnlySpan<byte> address)
    {
        if (address.Length != AddressDescriptor.TorV3AddressLength)
            throw new ArgumentException($"A Tor v3 address is {AddressDescriptor.TorV3AddressLength} bytes.",
                                        nameof(address));

        return OnionBase32.Encode(address) + Suffix;
    }

    /// <summary>
    /// True when <paramref name="host"/> ends in <c>.onion</c> (case-insensitive), whatever comes before it.
    /// </summary>
    public static bool IsOnionHost(string? host) =>
        host is not null && host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a v3 onion host name, with or without the <c>.onion</c> suffix (case-insensitive), and checks its
    /// version and checksum.
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <param name="address">The 35 address bytes when valid.</param>
    /// <param name="error">Why it is not a valid v3 address; null when it is.</param>
    public static bool TryParse(string? host, out byte[] address, out string? error)
    {
        address = [];
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "the onion host is empty";
            return false;
        }

        var label = IsOnionHost(host) ? host[..^Suffix.Length] : host;
        if (label.Length == 16)
        {
            error = $"'{host}' is a Tor v2 onion address; Tor removed v2 onion services in 0.4.6";
            return false;
        }

        if (label.Length != HostLabelLength)
        {
            error = $"'{host}' is not a Tor v3 onion address ({HostLabelLength} base32 characters before .onion)";
            return false;
        }

        var bytes = OnionBase32.TryDecode(label, AddressDescriptor.TorV3AddressLength);
        if (bytes is null)
        {
            error = $"'{host}' is not base32";
            return false;
        }

        if (bytes[34] != Version)
        {
            error = $"'{host}' has onion address version {bytes[34]}, not {Version}";
            return false;
        }

        if (!IsValid(bytes))
        {
            error = $"'{host}' has a bad checksum (mistyped?)";
            return false;
        }

        address = bytes;
        error = null;
        return true;
    }

    private static byte[] Checksum(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> input = stackalloc byte[s_checksumPrefix.Length + PublicKeyLength + 1];
        s_checksumPrefix.CopyTo(input);
        publicKey.CopyTo(input[s_checksumPrefix.Length..]);
        input[^1] = Version;
        return Sha3.Hash256(input);
    }
}