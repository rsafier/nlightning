namespace NLightning.Domain.Protocol.Onion.ValueObjects;

using Constants;
using Domain.Interfaces;
using Utils.Extensions;

/// <summary>
/// A BOLT 4 <c>onion_packet</c>: version(1) || public_key(33) || hop_payloads(N) || hmac(32).
/// </summary>
/// <remarks>
/// The constructors validate only the total length. The version byte and the public key are deliberately NOT
/// validated here, so that a packet with a bad version or key can still be parsed from <c>update_add_htlc</c> and
/// answered with <c>update_fail_malformed_htlc</c> (<c>invalid_onion_version</c>/<c>invalid_onion_key</c>).
/// Those checks belong to the peeling step.
/// </remarks>
public readonly struct OnionPacket : IValueObject, IEquatable<OnionPacket>
{
    private readonly byte[] _value;

    /// <summary>
    /// The packet version byte (not validated).
    /// </summary>
    public byte Version => _value[0];

    /// <summary>
    /// The 33 raw bytes of the ephemeral public key (not validated).
    /// </summary>
    public ReadOnlyMemory<byte> PublicKey => _value.AsMemory(OnionConstants.VersionLength,
                                                             OnionConstants.PublicKeyLength);

    /// <summary>
    /// The obfuscated hop payloads.
    /// </summary>
    public ReadOnlyMemory<byte> HopPayloads => _value.AsMemory(
        OnionConstants.VersionLength + OnionConstants.PublicKeyLength, HopPayloadsLength);

    /// <summary>
    /// The 32-byte packet HMAC.
    /// </summary>
    public ReadOnlyMemory<byte> Hmac => _value.AsMemory(_value.Length - OnionConstants.HmacLength,
                                                        OnionConstants.HmacLength);

    /// <summary>
    /// The length of the hop_payloads field.
    /// </summary>
    public int HopPayloadsLength => _value.Length - OnionConstants.PacketOverheadLength;

    /// <summary>
    /// The total serialized length of the packet.
    /// </summary>
    public int Length => _value.Length;

    /// <summary>
    /// Parses a packet from its raw bytes.
    /// </summary>
    /// <param name="packet">The serialized packet. The bytes are copied.</param>
    /// <param name="hopPayloadsLength">The expected hop_payloads length (1300 for payments).</param>
    /// <exception cref="ArgumentException">If the total length is not overhead + <paramref name="hopPayloadsLength"/>.</exception>
    public OnionPacket(ReadOnlySpan<byte> packet, int hopPayloadsLength = OnionConstants.HopPayloadsLength)
    {
        ValidateHopPayloadsLength(hopPayloadsLength);

        var expectedLength = OnionConstants.PacketOverheadLength + hopPayloadsLength;
        if (packet.Length != expectedLength)
            throw new ArgumentException($"Onion packet must be {expectedLength} bytes, got {packet.Length}.",
                                        nameof(packet));

        _value = packet.ToArray();
    }

    /// <summary>
    /// Builds a packet from its components.
    /// </summary>
    /// <exception cref="ArgumentException">If any component has an invalid length.</exception>
    public OnionPacket(byte version, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> hopPayloads,
                       ReadOnlySpan<byte> hmac)
    {
        if (publicKey.Length != OnionConstants.PublicKeyLength)
            throw new ArgumentException($"Public key must be {OnionConstants.PublicKeyLength} bytes.",
                                        nameof(publicKey));

        ValidateHopPayloadsLength(hopPayloads.Length);

        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Hmac must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        _value = new byte[OnionConstants.PacketOverheadLength + hopPayloads.Length];
        _value[0] = version;
        publicKey.CopyTo(_value.AsSpan(OnionConstants.VersionLength));
        hopPayloads.CopyTo(_value.AsSpan(OnionConstants.VersionLength + OnionConstants.PublicKeyLength));
        hmac.CopyTo(_value.AsSpan(_value.Length - OnionConstants.HmacLength));
    }

    /// <summary>
    /// Returns a copy of the serialized packet.
    /// </summary>
    public byte[] ToBytes() => (byte[])_value.Clone();

    public static implicit operator ReadOnlySpan<byte>(OnionPacket packet) => packet._value;
    public static implicit operator ReadOnlyMemory<byte>(OnionPacket packet) => packet._value;

    public bool Equals(OnionPacket other)
    {
        if (_value is null || other._value is null)
            return ReferenceEquals(_value, other._value);

        return _value.AsSpan().SequenceEqual(other._value);
    }

    public override bool Equals(object? obj) => obj is OnionPacket other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : _value.GetByteArrayHashCode();

    public static bool operator ==(OnionPacket left, OnionPacket right) => left.Equals(right);

    public static bool operator !=(OnionPacket left, OnionPacket right) => !left.Equals(right);

    private static void ValidateHopPayloadsLength(int hopPayloadsLength)
    {
        if (hopPayloadsLength <= 0)
            throw new ArgumentException("Hop payloads length must be positive.", nameof(hopPayloadsLength));
    }
}