using System.Buffers.Binary;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Models;
using Domain.Protocol.Payloads;
using Signers;

/// <summary>
/// BOLT 7 signatures (G0-T3) over the spec's signed ranges, double-SHA256: channel_announcement (4 signatures over
/// the bytes after them), node_announcement and channel_update (over the bytes after the signature) and
/// announcement_signatures (both over the channel_announcement's hash).
/// </summary>
public class GossipSignatureVerifierTests
{
    private static readonly byte[] s_node1Secret = Secret(0x11);
    private static readonly byte[] s_node2Secret = Secret(0x22);
    private static readonly byte[] s_bitcoin1Secret = Secret(0x33);
    private static readonly byte[] s_bitcoin2Secret = Secret(0x44);
    private static readonly byte[] s_otherSecret = Secret(0x55);

    private readonly GossipSignatureVerifier _verifier = new();

    #region channel_announcement (256)

    [Fact]
    public void Given_SignedChannelAnnouncement_When_VerifyAll_Then_AllFourSignaturesVerify()
    {
        // Arrange
        var payload = CreateChannelAnnouncement();

        // Act
        var parsed = GossipSignedRanges.TryGetChannelAnnouncementChecks(payload, out var checks);

        // Assert: one hash, the double-SHA256 of everything after the 4 signatures (incl. the trailing bytes)
        Assert.True(parsed);
        Assert.Equal(4, checks.Length);
        var expectedHash = SHA256.HashData(SHA256.HashData(payload.AsSpan(256)));
        Assert.All(checks, c => Assert.Equal(expectedHash, (byte[])c.MessageHash));
        Assert.Equal(PubKey(s_node1Secret), checks[0].PublicKey);
        Assert.Equal(PubKey(s_node2Secret), checks[1].PublicKey);
        Assert.Equal(PubKey(s_bitcoin1Secret), checks[2].PublicKey);
        Assert.Equal(PubKey(s_bitcoin2Secret), checks[3].PublicKey);
        Assert.All(checks, c => Assert.True(_verifier.Verify(c.MessageHash, c.Signature, c.PublicKey)));
        Assert.True(_verifier.VerifyAll(checks));
    }

    [Theory]
    [InlineData(258)] // features
    [InlineData(262)] // chain_hash
    [InlineData(292)] // short_channel_id
    [InlineData(301)] // node_id_1
    [InlineData(-1)] // the last trailing (unknown) byte: signed too
    public void Given_ChannelAnnouncementByteChangedInSignedRange_When_VerifyAll_Then_False(int offset)
    {
        // Arrange
        var payload = CreateChannelAnnouncement();
        var index = offset < 0 ? payload.Length + offset : offset;
        payload[index] ^= 0x01;

        // Act
        var parsed = GossipSignedRanges.TryGetChannelAnnouncementChecks(payload, out var checks);

        // Assert: a changed key byte may not even be a point; either way nothing verifies
        Assert.False(parsed && _verifier.VerifyAll(checks));
    }

    [Fact]
    public void Given_ChannelAnnouncementWithSwappedNodeSignatures_When_VerifyAll_Then_False()
    {
        // Arrange
        var payload = CreateChannelAnnouncement();
        var nodeSignature1 = payload[..64];
        payload.AsSpan(64, 64).CopyTo(payload);
        nodeSignature1.CopyTo(payload, 64);

        // Act
        Assert.True(GossipSignedRanges.TryGetChannelAnnouncementChecks(payload, out var checks));
        var valid = _verifier.VerifyAll(checks);

        // Assert: the bitcoin signatures still verify, the node ones do not
        Assert.False(valid);
        Assert.False(_verifier.Verify(checks[0].MessageHash, checks[0].Signature, checks[0].PublicKey));
        Assert.False(_verifier.Verify(checks[1].MessageHash, checks[1].Signature, checks[1].PublicKey));
        Assert.True(_verifier.Verify(checks[2].MessageHash, checks[2].Signature, checks[2].PublicKey));
        Assert.True(_verifier.Verify(checks[3].MessageHash, checks[3].Signature, checks[3].PublicKey));
    }

    [Fact]
    public void Given_ChannelAnnouncementSignedByOtherBitcoinKey_When_VerifyAll_Then_False()
    {
        // Arrange: bitcoin_signature_2 made with a key that is not bitcoin_key_2
        var payload = CreateChannelAnnouncement(bitcoin2Signer: s_otherSecret);

        // Act
        Assert.True(GossipSignedRanges.TryGetChannelAnnouncementChecks(payload, out var checks));

        // Assert
        Assert.False(_verifier.VerifyAll(checks));
        Assert.False(_verifier.Verify(checks[3].MessageHash, checks[3].Signature, checks[3].PublicKey));
    }

    [Fact]
    public void Given_ChannelAnnouncementSignatureChanged_When_Verify_Then_OtherSignaturesUnaffected()
    {
        // Arrange: signatures are outside the signed range, so one broken signature does not change the hash
        var payload = CreateChannelAnnouncement();
        payload[64 + 10] ^= 0x01; // inside node_signature_2

        // Act
        Assert.True(GossipSignedRanges.TryGetChannelAnnouncementChecks(payload, out var checks));

        // Assert
        Assert.True(_verifier.Verify(checks[0].MessageHash, checks[0].Signature, checks[0].PublicKey));
        Assert.False(_verifier.Verify(checks[1].MessageHash, checks[1].Signature, checks[1].PublicKey));
        Assert.False(_verifier.VerifyAll(checks));
    }

    [Fact]
    public void Given_TruncatedChannelAnnouncement_When_TryGetChecks_Then_False()
    {
        // Arrange: cut inside bitcoin_key_2, and a features length running past the end
        var payload = CreateChannelAnnouncement(trailing: []);
        var truncated = payload[..^1];
        var overlong = CreateChannelAnnouncement();
        BinaryPrimitives.WriteUInt16BigEndian(overlong.AsSpan(256), 0xFFFF);

        // Act & Assert
        Assert.False(GossipSignedRanges.TryGetChannelAnnouncementChecks(truncated, out _));
        Assert.False(GossipSignedRanges.TryGetChannelAnnouncementChecks(overlong, out _));
        Assert.False(GossipSignedRanges.TryGetChannelAnnouncementChecks(new byte[257], out _));
    }

    #endregion

    #region node_announcement (257)

    [Fact]
    public void Given_SignedNodeAnnouncement_When_Verify_Then_True()
    {
        // Arrange
        var payload = CreateNodeAnnouncement(s_node1Secret);

        // Act
        var parsed = GossipSignedRanges.TryGetNodeAnnouncementCheck(payload, out var check);

        // Assert: signed range is everything after the signature
        Assert.True(parsed);
        Assert.Equal(SHA256.HashData(SHA256.HashData(payload.AsSpan(64))), (byte[])check.MessageHash);
        Assert.Equal(PubKey(s_node1Secret), check.PublicKey);
        Assert.True(_verifier.Verify(check.MessageHash, check.Signature, check.PublicKey));
        Assert.True(_verifier.VerifyAll([check]));
    }

    [Fact]
    public void Given_NodeAnnouncementWithChangedAliasOrAddress_When_Verify_Then_False()
    {
        // Arrange
        var aliasChanged = CreateNodeAnnouncement(s_node1Secret);
        aliasChanged[64 + 2 + 2 + 4 + 33 + 3] ^= 0x20; // first alias byte
        var addressChanged = CreateNodeAnnouncement(s_node1Secret);
        addressChanged[^3] ^= 0x01; // inside the IPv4 address

        // Act
        Assert.True(GossipSignedRanges.TryGetNodeAnnouncementCheck(aliasChanged, out var aliasCheck));
        Assert.True(GossipSignedRanges.TryGetNodeAnnouncementCheck(addressChanged, out var addressCheck));

        // Assert
        Assert.False(_verifier.Verify(aliasCheck.MessageHash, aliasCheck.Signature, aliasCheck.PublicKey));
        Assert.False(_verifier.Verify(addressCheck.MessageHash, addressCheck.Signature, addressCheck.PublicKey));
    }

    [Fact]
    public void Given_NodeAnnouncementSignedByAnotherNode_When_Verify_Then_False()
    {
        // Arrange: announces node 1 but is signed by another key
        var payload = CreateNodeAnnouncement(s_node1Secret, s_otherSecret);

        // Act
        Assert.True(GossipSignedRanges.TryGetNodeAnnouncementCheck(payload, out var check));

        // Assert
        Assert.False(_verifier.Verify(check.MessageHash, check.Signature, check.PublicKey));
    }

    [Fact]
    public void Given_TruncatedNodeAnnouncement_When_TryGetCheck_Then_False()
    {
        // Arrange: cut inside node_id
        var payload = CreateNodeAnnouncement(s_node1Secret);

        // Act & Assert
        Assert.False(GossipSignedRanges.TryGetNodeAnnouncementCheck(payload[..(64 + 2 + 2 + 4 + 32)], out _));
        Assert.False(GossipSignedRanges.TryGetNodeAnnouncementCheck(new byte[65], out _));
    }

    #endregion

    #region channel_update (258)

    [Fact]
    public void Given_LndChannelUpdate_When_Verify_Then_TrueForLndNodeIdOnly()
    {
        // Arrange: a channel_update LND 0.20 sent on the wire, and the signature hash of the typed payload
        var payload = Convert.FromHexString(LocalLightningSignerNodeMessageTests.LndChannelUpdateHex);
        var lndNodeId = new CompactPubKey(Convert.FromHexString(LocalLightningSignerNodeMessageTests.LndNodeIdHex));
        var typedHash = ChannelUpdatePayload.Parse(payload).GetSignatureHash();

        // Act
        Assert.True(GossipSignedRanges.TryGetChannelUpdateCheck(payload, lndNodeId, out var check));
        Assert.True(GossipSignedRanges.TryGetChannelUpdateCheck(payload, PubKey(s_node1Secret), out var otherCheck));

        // Assert: the raw signed range is the typed payload's hash, and only LND's key verifies it
        Assert.Equal((byte[])typedHash, (byte[])check.MessageHash);
        Assert.True(_verifier.Verify(check.MessageHash, check.Signature, check.PublicKey));
        Assert.False(_verifier.Verify(otherCheck.MessageHash, otherCheck.Signature, otherCheck.PublicKey));
    }

    [Fact]
    public void Given_LndChannelUpdateWithChangedFee_When_Verify_Then_False()
    {
        // Arrange: the last byte is htlc_maximum_msat's
        var payload = Convert.FromHexString(LocalLightningSignerNodeMessageTests.LndChannelUpdateHex);
        payload[^1] ^= 0x01;
        var lndNodeId = new CompactPubKey(Convert.FromHexString(LocalLightningSignerNodeMessageTests.LndNodeIdHex));

        // Act
        Assert.True(GossipSignedRanges.TryGetChannelUpdateCheck(payload, lndNodeId, out var check));

        // Assert
        Assert.False(_verifier.Verify(check.MessageHash, check.Signature, check.PublicKey));
    }

    [Fact]
    public void Given_ShortChannelUpdate_When_TryGetCheck_Then_False()
    {
        // Act & Assert
        Assert.False(GossipSignedRanges.TryGetChannelUpdateCheck(new byte[64 + 39], PubKey(s_node1Secret), out _));
    }

    #endregion

    #region announcement_signatures (259)

    [Fact]
    public void Given_AnnouncementSignaturesForTheAnnouncement_When_VerifyAll_Then_True()
    {
        // Arrange: node 2's half of the announcement (node_signature_2, bitcoin_signature_2)
        var announcement = CreateChannelAnnouncement();
        var announcementHash = GossipSignedRanges.DoubleSha256(announcement.AsSpan(256));
        var payload = CreateAnnouncementSignatures(announcementHash, s_node2Secret, s_bitcoin2Secret);

        // Act
        var parsed = GossipSignedRanges.TryGetAnnouncementSignaturesChecks(payload, announcementHash,
                                                                           PubKey(s_node2Secret),
                                                                           PubKey(s_bitcoin2Secret), out var checks);

        // Assert: and they are the same signatures the full announcement carries (RFC 6979)
        Assert.True(parsed);
        Assert.True(_verifier.VerifyAll(checks));
        Assert.Equal(announcement[64..128], checks[0].Signature.Value);
        Assert.Equal(announcement[192..256], checks[1].Signature.Value);
    }

    [Fact]
    public void Given_AnnouncementSignaturesOverAnotherAnnouncement_When_VerifyAll_Then_False()
    {
        // Arrange: signed over an announcement with another SCID
        var signedAnnouncement = CreateChannelAnnouncement();
        signedAnnouncement[299] ^= 0x01; // last short_channel_id byte (output index)
        var signedHash = GossipSignedRanges.DoubleSha256(signedAnnouncement.AsSpan(256));
        var ourHash = GossipSignedRanges.DoubleSha256(CreateChannelAnnouncement().AsSpan(256));
        var payload = CreateAnnouncementSignatures(signedHash, s_node2Secret, s_bitcoin2Secret);

        // Act
        Assert.True(GossipSignedRanges.TryGetAnnouncementSignaturesChecks(payload, ourHash, PubKey(s_node2Secret),
                                                                          PubKey(s_bitcoin2Secret), out var checks));

        // Assert
        Assert.False(_verifier.VerifyAll(checks));
    }

    [Fact]
    public void Given_AnnouncementSignaturesWithSwappedSignatures_When_VerifyAll_Then_False()
    {
        // Arrange: bitcoin signature in the node signature's place and back
        var hash = GossipSignedRanges.DoubleSha256(CreateChannelAnnouncement().AsSpan(256));
        var payload = CreateAnnouncementSignatures(hash, s_bitcoin2Secret, s_node2Secret);

        // Act
        Assert.True(GossipSignedRanges.TryGetAnnouncementSignaturesChecks(payload, hash, PubKey(s_node2Secret),
                                                                          PubKey(s_bitcoin2Secret), out var checks));

        // Assert
        Assert.False(_verifier.VerifyAll(checks));
        Assert.False(GossipSignedRanges.TryGetAnnouncementSignaturesChecks(payload[..167], hash,
                                                                           PubKey(s_node2Secret),
                                                                           PubKey(s_bitcoin2Secret), out _));
    }

    #endregion

    #region Verifier edge cases

    [Fact]
    public void Given_HighSSignature_When_Verify_Then_True()
    {
        // Arrange: BOLT 7 relays may replace s with -s
        var hash = GossipSignedRanges.DoubleSha256("gossip"u8);
        var lowS = Sign(s_node1Secret, hash);
        Assert.True(SecpECDSASignature.TryCreateFromCompact(lowS, out var parsed));
        var (r, s) = parsed!;
        var highS = new byte[64];
        new SecpECDSASignature(r, s.Negate(), true).WriteCompactToSpan(highS);

        // Act
        var valid = _verifier.Verify(hash, highS, PubKey(s_node1Secret));

        // Assert
        Assert.True(s.Negate().IsHigh);
        Assert.True(valid);
    }

    [Fact]
    public void Given_MalformedInputs_When_Verify_Then_FalseWithoutThrowing()
    {
        // Arrange
        var hash = GossipSignedRanges.DoubleSha256("gossip"u8);
        var signature = Sign(s_node1Secret, hash);
        var notAPoint = new byte[33];
        notAPoint[0] = 0x02;
        var overflow = Enumerable.Repeat((byte)0xFF, 64).ToArray();

        // Act & Assert
        Assert.True(_verifier.Verify(hash, signature, PubKey(s_node1Secret)));
        Assert.False(_verifier.Verify(hash, signature, new CompactPubKey(notAPoint)));
        Assert.False(_verifier.Verify(hash, overflow, PubKey(s_node1Secret)));
        Assert.False(_verifier.Verify(hash, new byte[64], PubKey(s_node1Secret)));
        Assert.False(_verifier.Verify(default, signature, PubKey(s_node1Secret)));
        Assert.False(_verifier.Verify(hash, signature, default));
        Assert.False(_verifier.Verify(hash, null!, PubKey(s_node1Secret)));
    }

    [Fact]
    public void Given_EmptyOrPartlyInvalidBatch_When_VerifyAll_Then_False()
    {
        // Arrange
        var hash = GossipSignedRanges.DoubleSha256("gossip"u8);
        var good = new GossipSignatureCheck(hash, Sign(s_node1Secret, hash), PubKey(s_node1Secret));
        var bad = new GossipSignatureCheck(hash, Sign(s_node1Secret, hash), PubKey(s_node2Secret));

        // Act & Assert
        Assert.False(_verifier.VerifyAll([]));
        Assert.True(_verifier.VerifyAll([good, good]));
        Assert.False(_verifier.VerifyAll([good, bad]));
        Assert.False(_verifier.VerifyAll([bad, good]));
    }

    [Fact]
    public void Given_Data_When_DoubleSha256_Then_EqualsNBitcoinDoubleSha256()
    {
        // Arrange
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        // Act
        var hash = GossipSignedRanges.DoubleSha256(data);

        // Assert
        Assert.Equal(Hashes.DoubleSHA256RawBytes(data, 0, data.Length), (byte[])hash);
    }

    #endregion

    /// <summary>
    /// A channel_announcement payload (no type): the 4 signatures, features (2 bytes), the regtest chain hash, a SCID,
    /// node_id_1/2, bitcoin_key_1/2 and <paramref name="trailing"/> unknown bytes, all signed.
    /// </summary>
    private static byte[] CreateChannelAnnouncement(byte[]? bitcoin2Signer = null, byte[]? trailing = null)
    {
        trailing ??= [0xAA, 0xBB, 0xCC];
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x00, 0x02, 0x01, 0x00 }); // len 2, features
        body.AddRange(Convert.FromHexString("06226E46111A0B59CAAF126043EB5BBF28C34F3A5E332A1FC7B2B73CF188910F"));
        body.AddRange(new byte[] { 0x00, 0x00, 0x65, 0x00, 0x00, 0x01, 0x00, 0x01 }); // 101x1x1
        body.AddRange((byte[])PubKey(s_node1Secret));
        body.AddRange((byte[])PubKey(s_node2Secret));
        body.AddRange((byte[])PubKey(s_bitcoin1Secret));
        body.AddRange((byte[])PubKey(s_bitcoin2Secret));
        body.AddRange(trailing);

        var signed = body.ToArray();
        var hash = Hash256(signed);
        return
        [
            .. Sign(s_node1Secret, hash), .. Sign(s_node2Secret, hash), .. Sign(s_bitcoin1Secret, hash),
            .. Sign(bitcoin2Signer ?? s_bitcoin2Secret, hash), .. signed
        ];
    }

    /// <summary>A node_announcement payload for <paramref name="nodeSecret"/>'s node id, with one IPv4 address.</summary>
    private static byte[] CreateNodeAnnouncement(byte[] nodeSecret, byte[]? signer = null)
    {
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x00, 0x02, 0x02, 0x00 }); // len 2, features
        body.AddRange(new byte[] { 0x68, 0xF5, 0x0E, 0x40 }); // timestamp
        body.AddRange((byte[])PubKey(nodeSecret));
        body.AddRange(new byte[] { 0x3B, 0x99, 0xE3 }); // rgb_color
        var alias = new byte[32];
        "nltg"u8.CopyTo(alias);
        body.AddRange(alias);
        body.AddRange(new byte[] { 0x00, 0x07, 0x01, 0x7F, 0x00, 0x00, 0x01, 0x26, 0x07 }); // 127.0.0.1:9735

        var signed = body.ToArray();
        return [.. Sign(signer ?? nodeSecret, Hash256(signed)), .. signed];
    }

    /// <summary>announcement_signatures: channel_id, short_channel_id, node_signature, bitcoin_signature.</summary>
    private static byte[] CreateAnnouncementSignatures(Hash announcementHash, byte[] nodeSigner, byte[] bitcoinSigner)
    {
        var channelId = Enumerable.Repeat((byte)0xC1, 32).ToArray();
        byte[] shortChannelId = [0x00, 0x00, 0x65, 0x00, 0x00, 0x01, 0x00, 0x01];
        return
        [
            .. channelId, .. shortChannelId, .. Sign(nodeSigner, announcementHash),
            .. Sign(bitcoinSigner, announcementHash)
        ];
    }

    private static byte[] Hash256(byte[] data) => SHA256.HashData(SHA256.HashData(data));

    private static byte[] Sign(byte[] secret, Hash hash)
    {
        Assert.True(ECPrivKey.TryCreate(secret, Context.Instance, out var key));
        Assert.True(key!.TrySignECDSA((byte[])hash, out var signature));
        var compact = new byte[64];
        signature!.WriteCompactToSpan(compact);
        return compact;
    }

    private static CompactPubKey PubKey(byte[] secret)
    {
        Assert.True(ECPrivKey.TryCreate(secret, Context.Instance, out var key));
        var bytes = new byte[33];
        key!.CreatePubKey().WriteToSpan(true, bytes, out _);
        return new CompactPubKey(bytes);
    }

    private static byte[] Secret(byte fill) => Enumerable.Repeat(fill, 32).ToArray();
}