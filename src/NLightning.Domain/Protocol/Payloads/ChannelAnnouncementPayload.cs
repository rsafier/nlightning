using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload of the channel_announcement message (BOLT 7, type 256).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: <c>node_signature_1(64) || node_signature_2(64) || bitcoin_signature_1(64) || bitcoin_signature_2(64) ||
/// u16 len || features(len) || chain_hash(32) || short_channel_id(8) || node_id_1(33) || node_id_2(33) ||
/// bitcoin_key_1(33) || bitcoin_key_2(33)</c>, optionally followed by unknown fields (<see cref="ExtraData"/>). All
/// four signatures cover the double-SHA256 of everything after them (from offset <see cref="SignaturesLength"/> to the
/// end), <b>including</b> the unknown fields, so they are kept verbatim.
/// </para>
/// <para>
/// Like <see cref="ChannelUpdatePayload"/>, this class is the single codec of the message body (<see cref="GetBytes"/>,
/// <see cref="GetSignedData"/>, <see cref="Parse"/>; plan decision D1): the signature hash is needed in the
/// Domain/Application layers and the wire serializer only frames it, so wire, hash and relay bytes always agree.
/// <see cref="Features"/> is kept as the raw wire bytes (big-endian bit field, leading zeros included) so a re-encode is
/// byte-identical. Semantic rules (key ordering, chain, features, the funding output) are not checked here.
/// </para>
/// </remarks>
public sealed class ChannelAnnouncementPayload : IMessagePayload
{
    /// <summary>
    /// The length of one signature field.
    /// </summary>
    public const int SignatureLength = CryptoConstants.MaxSignatureSize;

    /// <summary>
    /// The length of the four signatures, i.e. the offset where the signed data starts.
    /// </summary>
    public const int SignaturesLength = 4 * SignatureLength;

    /// <summary>
    /// The length of the known fields after the signatures when <c>features</c> is empty (the minimum length of
    /// <see cref="GetSignedData"/>).
    /// </summary>
    public const int MinSignedFieldsLength =
        sizeof(ushort) + CryptoConstants.Sha256HashLen + ShortChannelId.Length + 4 * CryptoConstants.CompactPubkeyLen;

    /// <summary>
    /// The minimum length of a <c>channel_announcement</c> payload (without the message type).
    /// </summary>
    public const int MinLength = SignaturesLength + MinSignedFieldsLength;

    private readonly byte[] _nodeSignature1;
    private readonly byte[] _nodeSignature2;
    private readonly byte[] _bitcoinSignature1;
    private readonly byte[] _bitcoinSignature2;
    private readonly byte[] _features;
    private readonly byte[] _extraData;

    /// <summary>
    /// The signature of <see cref="GetSignatureHash"/> by <see cref="NodeId1"/>. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature NodeSignature1 => new(_nodeSignature1.ToArray());

    /// <summary>
    /// The signature of <see cref="GetSignatureHash"/> by <see cref="NodeId2"/>. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature NodeSignature2 => new(_nodeSignature2.ToArray());

    /// <summary>
    /// The signature of <see cref="GetSignatureHash"/> by <see cref="BitcoinKey1"/>. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature BitcoinSignature1 => new(_bitcoinSignature1.ToArray());

    /// <summary>
    /// The signature of <see cref="GetSignatureHash"/> by <see cref="BitcoinKey2"/>. Every read returns a fresh copy.
    /// </summary>
    public CompactSignature BitcoinSignature2 => new(_bitcoinSignature2.ToArray());

    /// <summary>
    /// The channel's feature bits as raw wire bytes (big-endian bit field, possibly empty).
    /// </summary>
    public ReadOnlyMemory<byte> Features => _features;

    /// <summary>
    /// The chain the channel is on.
    /// </summary>
    public ChainHash ChainHash { get; }

    /// <summary>
    /// The real short channel id of the funding output.
    /// </summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>
    /// The lexicographically lesser node id of the two channel ends (BOLT 7).
    /// </summary>
    public CompactPubKey NodeId1 { get; }

    /// <summary>
    /// The lexicographically greater node id of the two channel ends.
    /// </summary>
    public CompactPubKey NodeId2 { get; }

    /// <summary>
    /// The funding pubkey of <see cref="NodeId1"/>.
    /// </summary>
    public CompactPubKey BitcoinKey1 { get; }

    /// <summary>
    /// The funding pubkey of <see cref="NodeId2"/>.
    /// </summary>
    public CompactPubKey BitcoinKey2 { get; }

    /// <summary>
    /// Unknown fields after <c>bitcoin_key_2</c>, kept verbatim because the signatures cover them.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData => _extraData;

    /// <summary>
    /// Creates a <c>channel_announcement</c> payload.
    /// </summary>
    /// <param name="nodeSignature1">The 64-byte signature by <paramref name="nodeId1"/> (or
    /// <see cref="EmptySignature"/> before signing).</param>
    /// <param name="nodeSignature2">The 64-byte signature by <paramref name="nodeId2"/>.</param>
    /// <param name="bitcoinSignature1">The 64-byte signature by <paramref name="bitcoinKey1"/>.</param>
    /// <param name="bitcoinSignature2">The 64-byte signature by <paramref name="bitcoinKey2"/>.</param>
    /// <param name="features">The raw feature bytes (at most 65,535).</param>
    /// <param name="chainHash">The chain hash.</param>
    /// <param name="shortChannelId">The short channel id.</param>
    /// <param name="nodeId1">The lesser node id.</param>
    /// <param name="nodeId2">The greater node id.</param>
    /// <param name="bitcoinKey1">The funding pubkey of <paramref name="nodeId1"/>.</param>
    /// <param name="bitcoinKey2">The funding pubkey of <paramref name="nodeId2"/>.</param>
    /// <param name="extraData">Unknown trailing fields (normally empty).</param>
    /// <exception cref="ArgumentException">A signature is not 64 bytes, or the features are longer than 65,535
    /// bytes.</exception>
    public ChannelAnnouncementPayload(CompactSignature nodeSignature1, CompactSignature nodeSignature2,
                                      CompactSignature bitcoinSignature1, CompactSignature bitcoinSignature2,
                                      ReadOnlyMemory<byte> features, ChainHash chainHash,
                                      ShortChannelId shortChannelId, CompactPubKey nodeId1, CompactPubKey nodeId2,
                                      CompactPubKey bitcoinKey1, CompactPubKey bitcoinKey2,
                                      ReadOnlyMemory<byte> extraData = default)
    {
        _nodeSignature1 = CopySignature(nodeSignature1, nameof(nodeSignature1));
        _nodeSignature2 = CopySignature(nodeSignature2, nameof(nodeSignature2));
        _bitcoinSignature1 = CopySignature(bitcoinSignature1, nameof(bitcoinSignature1));
        _bitcoinSignature2 = CopySignature(bitcoinSignature2, nameof(bitcoinSignature2));
        if (features.Length > ushort.MaxValue)
            throw new ArgumentException($"channel_announcement features are at most {ushort.MaxValue} bytes.",
                                        nameof(features));

        _features = features.ToArray();
        ChainHash = chainHash;
        ShortChannelId = shortChannelId;
        NodeId1 = nodeId1;
        NodeId2 = nodeId2;
        BitcoinKey1 = bitcoinKey1;
        BitcoinKey2 = bitcoinKey2;
        _extraData = extraData.ToArray();
    }

    /// <summary>
    /// A 64-byte all-zero signature, the placeholder of an announcement that is not signed yet.
    /// </summary>
    public static CompactSignature EmptySignature => new(new byte[SignatureLength]);

    /// <summary>
    /// Returns a copy of this payload with the four signatures replaced.
    /// </summary>
    public ChannelAnnouncementPayload WithSignatures(CompactSignature nodeSignature1, CompactSignature nodeSignature2,
                                                     CompactSignature bitcoinSignature1,
                                                     CompactSignature bitcoinSignature2) =>
        new(nodeSignature1, nodeSignature2, bitcoinSignature1, bitcoinSignature2, _features, ChainHash, ShortChannelId,
            NodeId1, NodeId2, BitcoinKey1, BitcoinKey2, _extraData);

    /// <summary>
    /// The bytes the four signatures cover: every field after <c>bitcoin_signature_2</c>, including
    /// <see cref="ExtraData"/>.
    /// </summary>
    public byte[] GetSignedData()
    {
        var data = new byte[MinSignedFieldsLength + _features.Length + _extraData.Length];
        WriteSignedData(data);
        return data;
    }

    /// <summary>
    /// The hash every signature signs: <c>SHA256(SHA256(</c><see cref="GetSignedData"/><c>))</c>.
    /// </summary>
    public Hash GetSignatureHash()
    {
        return SHA256.HashData(SHA256.HashData(GetSignedData()));
    }

    /// <summary>
    /// The wire bytes of the payload (without the message type): the four signatures, then
    /// <see cref="GetSignedData"/>.
    /// </summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[MinLength + _features.Length + _extraData.Length];
        _nodeSignature1.CopyTo(bytes, 0);
        _nodeSignature2.CopyTo(bytes, SignatureLength);
        _bitcoinSignature1.CopyTo(bytes, 2 * SignatureLength);
        _bitcoinSignature2.CopyTo(bytes, 3 * SignatureLength);
        WriteSignedData(bytes.AsSpan(SignaturesLength));
        return bytes;
    }

    /// <summary>
    /// Parses a <c>channel_announcement</c> payload (without the message type). Bytes after <c>bitcoin_key_2</c> are
    /// kept as <see cref="ExtraData"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is too short for its fields (including the declared
    /// <c>len</c> of <c>features</c>), or a key is not a compressed point encoding (02/03 prefix).</exception>
    public static ChannelAnnouncementPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinLength)
            throw new ArgumentException(
                $"A channel_announcement payload is at least {MinLength} bytes, got {payload.Length}.",
                nameof(payload));

        var offset = 0;
        var nodeSignature1 = ReadSignature(payload, ref offset);
        var nodeSignature2 = ReadSignature(payload, ref offset);
        var bitcoinSignature1 = ReadSignature(payload, ref offset);
        var bitcoinSignature2 = ReadSignature(payload, ref offset);

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += sizeof(ushort);
        if (payload.Length < MinLength + featuresLength)
            throw new ArgumentException(
                $"A channel_announcement with {featuresLength} feature bytes is at least {MinLength + featuresLength} bytes, got {payload.Length}.",
                nameof(payload));

        var features = payload.Slice(offset, featuresLength).ToArray();
        offset += featuresLength;
        var chainHash = new ChainHash(payload.Slice(offset, CryptoConstants.Sha256HashLen));
        offset += CryptoConstants.Sha256HashLen;
        var shortChannelId = new ShortChannelId(payload.Slice(offset, ShortChannelId.Length).ToArray());
        offset += ShortChannelId.Length;
        var nodeId1 = ReadPubKey(payload, ref offset);
        var nodeId2 = ReadPubKey(payload, ref offset);
        var bitcoinKey1 = ReadPubKey(payload, ref offset);
        var bitcoinKey2 = ReadPubKey(payload, ref offset);

        return new ChannelAnnouncementPayload(nodeSignature1, nodeSignature2, bitcoinSignature1, bitcoinSignature2,
                                              features, chainHash, shortChannelId, nodeId1, nodeId2, bitcoinKey1,
                                              bitcoinKey2, payload[offset..].ToArray());
    }

    /// <summary>
    /// Parses a <c>channel_announcement</c> payload (without the message type).
    /// </summary>
    /// <returns><c>false</c> if the payload is malformed (see <see cref="Parse"/>).</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload,
                                [NotNullWhen(true)] out ChannelAnnouncementPayload? channelAnnouncement)
    {
        try
        {
            channelAnnouncement = Parse(payload);
            return true;
        }
        catch (ArgumentException)
        {
            channelAnnouncement = null;
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
        ChainHash.Value.CopyTo(destination[offset..]);
        offset += CryptoConstants.Sha256HashLen;
        ((ReadOnlySpan<byte>)ShortChannelId).CopyTo(destination[offset..]);
        offset += ShortChannelId.Length;
        foreach (var key in (ReadOnlySpan<CompactPubKey>)[NodeId1, NodeId2, BitcoinKey1, BitcoinKey2])
        {
            ((ReadOnlySpan<byte>)key).CopyTo(destination[offset..]);
            offset += CryptoConstants.CompactPubkeyLen;
        }

        _extraData.CopyTo(destination[offset..]);
    }

    private static byte[] CopySignature(CompactSignature signature, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(signature, parameterName);
        if (signature.Value.Length != SignatureLength)
            throw new ArgumentException($"A channel_announcement signature is {SignatureLength} bytes.",
                                        parameterName);

        return signature.Value.ToArray();
    }

    private static CompactSignature ReadSignature(ReadOnlySpan<byte> payload, ref int offset)
    {
        var signature = new CompactSignature(payload.Slice(offset, SignatureLength).ToArray());
        offset += SignatureLength;
        return signature;
    }

    private static CompactPubKey ReadPubKey(ReadOnlySpan<byte> payload, ref int offset)
    {
        var key = new CompactPubKey(payload.Slice(offset, CryptoConstants.CompactPubkeyLen).ToArray());
        offset += CryptoConstants.CompactPubkeyLen;
        return key;
    }
}