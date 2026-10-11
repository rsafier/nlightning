using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Protocol.Payloads;

using Crypto.Constants;
using Crypto.ValueObjects;
using Interfaces;

/// <summary>
/// Represents the payload of the node_announcement message (BOLT 7, type 257).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: <c>signature(64) || u16 flen || features(flen) || u32 timestamp || node_id(33) || rgb_color(3) ||
/// alias(32) || u16 addrlen || addresses(addrlen)</c>, optionally followed by unknown fields (<see cref="ExtraData"/>).
/// The signature covers the double-SHA256 of everything after it, <b>including</b> the unknown fields, so they are kept
/// verbatim.
/// </para>
/// <para>
/// The single codec of the message body, like <see cref="ChannelUpdatePayload"/> (plan decision D1).
/// <see cref="Features"/>, <see cref="Alias"/> and <see cref="Addresses"/> are the raw wire bytes, so a re-encode is
/// byte-identical: the address descriptors are decoded by the gossip layer (BOLT 7 has receiver rules for unknown
/// types, port 0 and repeated DNS entries that must not make the whole message unparsable), and the alias is not
/// required to be valid UTF-8 on receipt. Semantic rules (timestamps, descriptor order, a known node) are not checked
/// here.
/// </para>
/// </remarks>
public sealed class NodeAnnouncementPayload : IMessagePayload
{
    /// <summary>
    /// The length of the <c>signature</c> field (the offset where the signed data starts).
    /// </summary>
    public const int SignatureLength = CryptoConstants.MaxSignatureSize;

    /// <summary>
    /// The length of <c>rgb_color</c>.
    /// </summary>
    public const int RgbColorLength = 3;

    /// <summary>
    /// The length of <c>alias</c>.
    /// </summary>
    public const int AliasLength = 32;

    /// <summary>
    /// The length of the known fields after the signature when <c>features</c> and <c>addresses</c> are empty (the
    /// minimum length of <see cref="GetSignedData"/>).
    /// </summary>
    public const int MinSignedFieldsLength =
        sizeof(ushort) + sizeof(uint) + CryptoConstants.CompactPubkeyLen + RgbColorLength + AliasLength + sizeof(ushort);

    /// <summary>
    /// The minimum length of a <c>node_announcement</c> payload (without the message type).
    /// </summary>
    public const int MinLength = SignatureLength + MinSignedFieldsLength;

    private readonly byte[] _signature;
    private readonly byte[] _features;
    private readonly byte[] _rgbColor;
    private readonly byte[] _alias;
    private readonly byte[] _addresses;
    private readonly byte[] _extraData;

    /// <summary>
    /// The node's signature of <see cref="GetSignatureHash"/>. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature Signature => new(_signature.ToArray());

    /// <summary>
    /// The node's feature bits as raw wire bytes (big-endian bit field, possibly empty).
    /// </summary>
    public ReadOnlyMemory<byte> Features => _features;

    /// <summary>
    /// The announcement's timestamp; a newer announcement of the same node has a greater one.
    /// </summary>
    public uint Timestamp { get; }

    /// <summary>
    /// The announcing node.
    /// </summary>
    public CompactPubKey NodeId { get; }

    /// <summary>
    /// The 3-byte <c>rgb_color</c>.
    /// </summary>
    public ReadOnlyMemory<byte> RgbColor => _rgbColor;

    /// <summary>
    /// The raw 32-byte <c>alias</c> (UTF-8, zero padded by a compliant sender).
    /// </summary>
    public ReadOnlyMemory<byte> Alias => _alias;

    /// <summary>
    /// The raw <c>addresses</c> field: a sequence of address descriptors (type byte, then type-specific data).
    /// </summary>
    public ReadOnlyMemory<byte> Addresses => _addresses;

    /// <summary>
    /// Unknown fields after <c>addresses</c>, kept verbatim because the signature covers them.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData => _extraData;

    /// <summary>
    /// Creates a <c>node_announcement</c> payload.
    /// </summary>
    /// <param name="signature">The 64-byte signature (or <see cref="EmptySignature"/> before signing).</param>
    /// <param name="features">The raw feature bytes (at most 65,535).</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="nodeId">The node id.</param>
    /// <param name="rgbColor">The 3-byte color.</param>
    /// <param name="alias">The 32-byte alias.</param>
    /// <param name="addresses">The raw address descriptors (at most 65,535 bytes).</param>
    /// <param name="extraData">Unknown trailing fields (normally empty).</param>
    /// <exception cref="ArgumentException">A field has the wrong length.</exception>
    public NodeAnnouncementPayload(CompactSignature signature, ReadOnlyMemory<byte> features, uint timestamp,
                                   CompactPubKey nodeId, ReadOnlyMemory<byte> rgbColor, ReadOnlyMemory<byte> alias,
                                   ReadOnlyMemory<byte> addresses, ReadOnlyMemory<byte> extraData = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != SignatureLength)
            throw new ArgumentException($"A node_announcement signature is {SignatureLength} bytes.",
                                        nameof(signature));
        if (features.Length > ushort.MaxValue)
            throw new ArgumentException($"node_announcement features are at most {ushort.MaxValue} bytes.",
                                        nameof(features));
        if (rgbColor.Length != RgbColorLength)
            throw new ArgumentException($"rgb_color is {RgbColorLength} bytes.", nameof(rgbColor));
        if (alias.Length != AliasLength)
            throw new ArgumentException($"alias is {AliasLength} bytes.", nameof(alias));
        if (addresses.Length > ushort.MaxValue)
            throw new ArgumentException($"node_announcement addresses are at most {ushort.MaxValue} bytes.",
                                        nameof(addresses));

        _signature = signature.Value.ToArray();
        _features = features.ToArray();
        Timestamp = timestamp;
        NodeId = nodeId;
        _rgbColor = rgbColor.ToArray();
        _alias = alias.ToArray();
        _addresses = addresses.ToArray();
        _extraData = extraData.ToArray();
    }

    /// <summary>
    /// A 64-byte all-zero signature, the placeholder of an announcement that is not signed yet.
    /// </summary>
    public static CompactSignature EmptySignature => new(new byte[SignatureLength]);

    /// <summary>
    /// Returns a copy of this payload with <paramref name="signature"/>.
    /// </summary>
    public NodeAnnouncementPayload WithSignature(CompactSignature signature) =>
        new(signature, _features, Timestamp, NodeId, _rgbColor, _alias, _addresses, _extraData);

    /// <summary>
    /// The alias as text: the UTF-8 decoding of <see cref="Alias"/> up to its first zero byte (invalid sequences become
    /// U+FFFD). For display only; use <see cref="Alias"/> for anything that is compared or signed.
    /// </summary>
    public string GetAliasText()
    {
        var length = Array.IndexOf(_alias, (byte)0);
        return Encoding.UTF8.GetString(_alias, 0, length < 0 ? _alias.Length : length);
    }

    /// <summary>
    /// Encodes <paramref name="alias"/> as the 32-byte <c>alias</c> field: UTF-8, zero padded (BOLT 7).
    /// </summary>
    /// <exception cref="ArgumentException">The UTF-8 encoding is longer than 32 bytes.</exception>
    public static byte[] EncodeAlias(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        var encoded = Encoding.UTF8.GetBytes(alias);
        if (encoded.Length > AliasLength)
            throw new ArgumentException($"An alias is at most {AliasLength} UTF-8 bytes, got {encoded.Length}.",
                                        nameof(alias));

        var field = new byte[AliasLength];
        encoded.CopyTo(field, 0);
        return field;
    }

    /// <summary>
    /// The bytes the signature covers: every field after <c>signature</c>, including <see cref="ExtraData"/>.
    /// </summary>
    public byte[] GetSignedData()
    {
        var data = new byte[MinSignedFieldsLength + _features.Length + _addresses.Length + _extraData.Length];
        WriteSignedData(data);
        return data;
    }

    /// <summary>
    /// The hash to sign or verify with the node key: <c>SHA256(SHA256(</c><see cref="GetSignedData"/><c>))</c>.
    /// </summary>
    public Hash GetSignatureHash()
    {
        return SHA256.HashData(SHA256.HashData(GetSignedData()));
    }

    /// <summary>
    /// The wire bytes of the payload (without the message type): <c>signature || </c><see cref="GetSignedData"/>.
    /// </summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[MinLength + _features.Length + _addresses.Length + _extraData.Length];
        _signature.CopyTo(bytes, 0);
        WriteSignedData(bytes.AsSpan(SignatureLength));
        return bytes;
    }

    /// <summary>
    /// Parses a <c>node_announcement</c> payload (without the message type). Bytes after <c>addresses</c> are kept as
    /// <see cref="ExtraData"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is too short for its fields (including the declared
    /// <c>flen</c>/<c>addrlen</c>), or <c>node_id</c> is not a compressed point encoding (02/03 prefix).</exception>
    public static NodeAnnouncementPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinLength)
            throw new ArgumentException(
                $"A node_announcement payload is at least {MinLength} bytes, got {payload.Length}.", nameof(payload));

        var offset = 0;
        var signature = new CompactSignature(payload.Slice(offset, SignatureLength).ToArray());
        offset += SignatureLength;

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += sizeof(ushort);
        if (payload.Length < MinLength + featuresLength)
            throw new ArgumentException(
                $"A node_announcement with {featuresLength} feature bytes is at least {MinLength + featuresLength} bytes, got {payload.Length}.",
                nameof(payload));

        var features = payload.Slice(offset, featuresLength).ToArray();
        offset += featuresLength;
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += sizeof(uint);
        var nodeId = new CompactPubKey(payload.Slice(offset, CryptoConstants.CompactPubkeyLen).ToArray());
        offset += CryptoConstants.CompactPubkeyLen;
        var rgbColor = payload.Slice(offset, RgbColorLength).ToArray();
        offset += RgbColorLength;
        var alias = payload.Slice(offset, AliasLength).ToArray();
        offset += AliasLength;

        var addressesLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += sizeof(ushort);
        if (payload.Length - offset < addressesLength)
            throw new ArgumentException(
                $"A node_announcement declares {addressesLength} address bytes, but only {payload.Length - offset} remain.",
                nameof(payload));

        var addresses = payload.Slice(offset, addressesLength).ToArray();
        offset += addressesLength;

        return new NodeAnnouncementPayload(signature, features, timestamp, nodeId, rgbColor, alias, addresses,
                                           payload[offset..].ToArray());
    }

    /// <summary>
    /// Parses a <c>node_announcement</c> payload (without the message type).
    /// </summary>
    /// <returns><c>false</c> if the payload is malformed (see <see cref="Parse"/>).</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload,
                                [NotNullWhen(true)] out NodeAnnouncementPayload? nodeAnnouncement)
    {
        try
        {
            nodeAnnouncement = Parse(payload);
            return true;
        }
        catch (ArgumentException)
        {
            nodeAnnouncement = null;
            return false;
        }
    }

    private void WriteSignedData(Span<byte> destination)
    {
        var offset = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)_features.Length);
        offset += sizeof(ushort);
        _features.CopyTo(destination[offset..]);
        offset += _features.Length;
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], Timestamp);
        offset += sizeof(uint);
        ((ReadOnlySpan<byte>)NodeId).CopyTo(destination[offset..]);
        offset += CryptoConstants.CompactPubkeyLen;
        _rgbColor.CopyTo(destination[offset..]);
        offset += RgbColorLength;
        _alias.CopyTo(destination[offset..]);
        offset += AliasLength;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)_addresses.Length);
        offset += sizeof(ushort);
        _addresses.CopyTo(destination[offset..]);
        offset += _addresses.Length;
        _extraData.CopyTo(destination[offset..]);
    }
}