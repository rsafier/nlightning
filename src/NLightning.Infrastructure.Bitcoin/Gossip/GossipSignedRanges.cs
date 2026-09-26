using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Models;

/// <summary>
/// The BOLT 7 signed ranges of raw gossip payloads (the message bytes after the 2-byte type), turned into
/// <see cref="GossipSignatureCheck"/>s for <see cref="GossipSignatureVerifier.VerifyAll"/>. For captured wire bytes
/// (vectors, G0-T5) and tests: the typed Domain payloads (<c>GetSignatureHash()</c>, D1) are the codec the node uses.
/// </summary>
/// <remarks>
/// Each <c>TryGet…Checks</c> reads only the fields it needs and returns false for a payload too short for them; it does
/// not validate anything else. Trailing bytes are part of the signed range.
/// </remarks>
internal static class GossipSignedRanges
{
    private const int SignatureLength = CryptoConstants.MaxSignatureSize;
    private const int KeyLength = CryptoConstants.CompactPubkeyLen;
    private const int ChainHashLength = 32;
    private const int ShortChannelIdLength = 8;

    /// <summary>
    /// channel_announcement (256): node_signature_1, node_signature_2, bitcoin_signature_1, bitcoin_signature_2 over
    /// SHA256d(payload[256..]), by node_id_1, node_id_2, bitcoin_key_1 and bitcoin_key_2, in that order.
    /// </summary>
    public static bool TryGetChannelAnnouncementChecks(ReadOnlySpan<byte> payload,
                                                       out GossipSignatureCheck[] checks)
    {
        checks = [];
        const int signedStart = 4 * SignatureLength;
        if (payload.Length < signedStart + 2)
            return false;

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(payload[signedStart..]);
        var nodeId1Offset = signedStart + 2 + featuresLength + ChainHashLength + ShortChannelIdLength;
        if (payload.Length < nodeId1Offset + 4 * KeyLength)
            return false;

        var hash = DoubleSha256(payload[signedStart..]);
        checks = new GossipSignatureCheck[4];
        for (var i = 0; i < 4; i++)
        {
            // node_signature_1/2 pair with node_id_1/2, bitcoin_signature_1/2 with bitcoin_key_1/2: same index order
            var signature = payload.Slice(i * SignatureLength, SignatureLength).ToArray();
            if (!TryGetKey(payload.Slice(nodeId1Offset + i * KeyLength, KeyLength), out var key))
                return false;

            checks[i] = new GossipSignatureCheck(hash, signature, key);
        }

        return true;
    }

    /// <summary>
    /// node_announcement (257): signature over SHA256d(payload[64..]) by node_id (after features and timestamp).
    /// </summary>
    public static bool TryGetNodeAnnouncementCheck(ReadOnlySpan<byte> payload, out GossipSignatureCheck check)
    {
        check = default;
        if (payload.Length < SignatureLength + 2)
            return false;

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(payload[SignatureLength..]);
        var nodeIdOffset = SignatureLength + 2 + featuresLength + 4;
        if (payload.Length < nodeIdOffset + KeyLength
         || !TryGetKey(payload.Slice(nodeIdOffset, KeyLength), out var nodeId))
            return false;

        check = new GossipSignatureCheck(DoubleSha256(payload[SignatureLength..]),
                                         payload[..SignatureLength].ToArray(), nodeId);
        return true;
    }

    /// <summary>
    /// channel_update (258): signature over SHA256d(payload[64..]) by <paramref name="nodeId"/>, the node id of the
    /// update's direction (from the channel_announcement: node_id_1 for direction 0, node_id_2 for 1).
    /// </summary>
    public static bool TryGetChannelUpdateCheck(ReadOnlySpan<byte> payload, CompactPubKey nodeId,
                                                out GossipSignatureCheck check)
    {
        check = default;
        if (payload.Length < SignatureLength + ChainHashLength + ShortChannelIdLength)
            return false;

        check = new GossipSignatureCheck(DoubleSha256(payload[SignatureLength..]),
                                         payload[..SignatureLength].ToArray(), nodeId);
        return true;
    }

    /// <summary>
    /// announcement_signatures (259: channel_id, short_channel_id, node_signature, bitcoin_signature): both signatures
    /// are over the channel_announcement's hash <paramref name="channelAnnouncementHash"/>, by the sender's node id and
    /// the sender's bitcoin (funding) key.
    /// </summary>
    public static bool TryGetAnnouncementSignaturesChecks(ReadOnlySpan<byte> payload, Hash channelAnnouncementHash,
                                                          CompactPubKey nodeId, CompactPubKey bitcoinKey,
                                                          out GossipSignatureCheck[] checks)
    {
        checks = [];
        const int nodeSignatureOffset = 32 + ShortChannelIdLength;
        if (payload.Length < nodeSignatureOffset + 2 * SignatureLength)
            return false;

        checks =
        [
            new GossipSignatureCheck(channelAnnouncementHash,
                                     payload.Slice(nodeSignatureOffset, SignatureLength).ToArray(), nodeId),
            new GossipSignatureCheck(channelAnnouncementHash,
                                     payload.Slice(nodeSignatureOffset + SignatureLength, SignatureLength).ToArray(),
                                     bitcoinKey)
        ];
        return true;
    }

    /// <summary>SHA256(SHA256(data)), the hash every BOLT 7 signature covers.</summary>
    public static Hash DoubleSha256(ReadOnlySpan<byte> data)
    {
        Span<byte> first = stackalloc byte[CryptoConstants.Sha256HashLen];
        SHA256.HashData(data, first);
        return SHA256.HashData(first);
    }

    private static bool TryGetKey(ReadOnlySpan<byte> bytes, out CompactPubKey key)
    {
        key = default;
        if (bytes[0] is not (0x02 or 0x03))
            return false;

        key = bytes;
        return true;
    }
}