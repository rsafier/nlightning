using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// BOLT 7 node-key signatures (<see cref="ILightningSigner.SignNodeMessage"/>/<see cref="ILightningSigner.VerifyNodeMessage"/>)
/// over <c>channel_update</c>, including one captured from LND.
/// </summary>
public class LocalLightningSignerNodeMessageTests
{
    /// <summary>
    /// A channel_update payload (without the type) that LND 0.20 (the Docker <c>custom_lnd</c> image, node "alice")
    /// sent to our node right after channel_ready, captured on the wire in
    /// <c>NormalOperationFlowTests.Given_NewChannel_When_Idle_Then_PeerStaysConnected</c>.
    /// </summary>
    internal const string LndChannelUpdateHex =
        "69A08F20E845FAA2EFC785E7492793CF548551779FFB3D53CD2D9DD9E4A7F98A155F5486B71ABE26C5209D1778C4BD8849F4E18B5F540C"
      + "8AA62DB384B04DE99E06226E46111A0B59CAAF126043EB5BBF28C34F3A5E332A1FC7B2B73CF188910F0000FD00000100016AB6CAA9010100"
      + "5000000000000003E8000003E800000001000000002FAF0800";

    /// <summary>
    /// The node id of the LND node that signed <see cref="LndChannelUpdateHex"/>.
    /// </summary>
    internal const string LndNodeIdHex = "029fa86deeed231f8ce4a8650bd7001cc39b63ddba32fc407533b17143866e7f83";

    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private readonly Mock<ISecureKeyManager> _secureKeyManagerMock = new();
    private readonly CompactPubKey _nodeId;

    public LocalLightningSignerNodeMessageTests()
    {
        using var key = new Key(s_nodePrivateKey);
        _nodeId = key.PubKey.ToBytes();

        // The real key manager returns a fresh copy per call, which the signer wipes after use
        _secureKeyManagerMock.Setup(x => x.GetNodeKeyPair())
                             .Returns(() => new CryptoKeyPair(s_nodePrivateKey.ToArray(), _nodeId));
    }

    [Fact]
    public void Given_LndChannelUpdate_When_VerifyNodeMessage_Then_SignatureIsValidForLndNodeId()
    {
        // Arrange
        var signer = CreateSigner();
        var payload = ChannelUpdatePayload.Parse(Convert.FromHexString(LndChannelUpdateHex));
        var lndNodeId = new CompactPubKey(Convert.FromHexString(LndNodeIdHex));

        // Act
        var valid = signer.VerifyNodeMessage(payload.GetSignatureHash(), payload.Signature, lndNodeId);

        // Assert
        Assert.True(valid);
        Assert.True(payload.Direction);
        Assert.Equal(ChainConstants.Regtest, payload.ChainHash);
    }

    [Fact]
    public void Given_LndChannelUpdate_When_VerifiedWithNBitcoin_Then_HashIsDoubleSha256OfDataAfterSignature()
    {
        // Arrange: an independent check of the hash, straight from the wire bytes
        var bytes = Convert.FromHexString(LndChannelUpdateHex);
        var payload = ChannelUpdatePayload.Parse(bytes);
        var expectedHash = System.Security.Cryptography.SHA256.HashData(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(ChannelUpdatePayload.SignatureLength)));

        // Act
        var hash = (byte[])payload.GetSignatureHash();
        var valid = new PubKey(Convert.FromHexString(LndNodeIdHex))
           .Verify(new uint256(hash), ParseCompact(bytes.AsSpan(0, 64).ToArray()));

        // Assert
        Assert.Equal(expectedHash, hash);
        Assert.True(valid);
    }

    [Fact]
    public void Given_LndChannelUpdate_When_FieldChangedOrOtherNodeId_Then_VerifyNodeMessageReturnsFalse()
    {
        // Arrange
        var signer = CreateSigner();
        var bytes = Convert.FromHexString(LndChannelUpdateHex);
        bytes[^1] ^= 0x01; // htlc_maximum_msat
        var tampered = ChannelUpdatePayload.Parse(bytes);
        var original = ChannelUpdatePayload.Parse(Convert.FromHexString(LndChannelUpdateHex));

        // Act
        var tamperedValid = signer.VerifyNodeMessage(tampered.GetSignatureHash(), tampered.Signature,
                                                     new CompactPubKey(Convert.FromHexString(LndNodeIdHex)));
        var otherNodeValid = signer.VerifyNodeMessage(original.GetSignatureHash(), original.Signature, _nodeId);

        // Assert
        Assert.False(tamperedValid);
        Assert.False(otherNodeValid);
    }

    [Fact]
    public void Given_UnsignedChannelUpdate_When_SignNodeMessage_Then_SignatureVerifiesWithNodeId()
    {
        // Arrange
        var signer = CreateSigner();
        var unsigned = CreateUnsignedUpdate();

        // Act
        var signature = signer.SignNodeMessage(unsigned.GetSignatureHash());
        var signed = unsigned.WithSignature(signature);

        // Assert
        Assert.Equal(ChannelUpdatePayload.SignatureLength, signature.Value.Length);
        Assert.True(signer.VerifyNodeMessage(signed.GetSignatureHash(), signed.Signature, _nodeId));
        Assert.Equal(unsigned.GetSignedData(), signed.GetSignedData());

        // Independent check with NBitcoin: low-S, and valid for the node key over the double-SHA256
        var ecdsa = ParseCompact(signature.Value);
        Assert.True(ecdsa.IsLowS);
        Assert.True(new PubKey((byte[])_nodeId).Verify(new uint256((byte[])signed.GetSignatureHash()), ecdsa));
    }

    [Fact]
    public void Given_SameHash_When_SignNodeMessageTwice_Then_SignaturesAreEqualAndKeyCopiesAreWiped()
    {
        // Arrange
        var signer = CreateSigner();
        var hash = CreateUnsignedUpdate().GetSignatureHash();
        var copies = new List<byte[]>();
        _secureKeyManagerMock.Setup(x => x.GetNodeKeyPair())
                             .Returns(() =>
                              {
                                  var copy = s_nodePrivateKey.ToArray();
                                  copies.Add(copy);
                                  return new CryptoKeyPair(copy, _nodeId);
                              });

        // Act
        var first = signer.SignNodeMessage(hash);
        var second = signer.SignNodeMessage(hash);

        // Assert: RFC 6979 is deterministic, and the signer zeroes each copy of the node key
        Assert.Equal(first, second);
        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy => Assert.All(copy, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void Given_HighSSignature_When_VerifyNodeMessage_Then_ReturnsTrue()
    {
        // Arrange: BOLT 7 notes a relaying node can replace s with -s; the signature stays valid
        var signer = CreateSigner();
        var hash = CreateUnsignedUpdate().GetSignatureHash();
        Assert.True(SecpECDSASignature.TryCreateFromCompact(signer.SignNodeMessage(hash).Value, out var lowS));
        var (r, s) = lowS!;
        var highS = new byte[64];
        new SecpECDSASignature(r, s.Negate(), true).WriteCompactToSpan(highS);

        // Act
        var valid = signer.VerifyNodeMessage(hash, highS, _nodeId);

        // Assert
        Assert.True(s.Negate().IsHigh);
        Assert.False(ParseCompact(highS).IsLowS);
        Assert.True(valid);
    }

    [Fact]
    public void Given_InvalidSignatureOrNodeId_When_VerifyNodeMessage_Then_ReturnsFalse()
    {
        // Arrange
        var signer = CreateSigner();
        var hash = CreateUnsignedUpdate().GetSignatureHash();
        var signature = signer.SignNodeMessage(hash);
        var notAPoint = new byte[33];
        notAPoint[0] = 0x02;
        var overflowSignature = Enumerable.Repeat((byte)0xFF, 64).ToArray();

        // Act & Assert
        Assert.False(signer.VerifyNodeMessage(hash, signature, new CompactPubKey(notAPoint)));
        Assert.False(signer.VerifyNodeMessage(hash, overflowSignature, _nodeId));
        Assert.False(signer.VerifyNodeMessage(hash, ChannelUpdatePayload.EmptySignature, _nodeId));
    }

    private static ECDSASignature ParseCompact(byte[] compact)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(compact, out var signature));
        return signature;
    }

    private static ChannelUpdatePayload CreateUnsignedUpdate() =>
        new(ChannelUpdatePayload.EmptySignature, ChainConstants.Regtest, new ShortChannelId(253, 1, 1), 1_790_000_000,
            ChannelUpdatePayload.MessageFlagMustBeOne | ChannelUpdatePayload.MessageFlagDontForward, 0, 40, 1_000,
            1_000, 1, 800_000_000);

    private LocalLightningSigner CreateSigner() =>
        new(new Mock<IFundingOutputBuilder>().Object,
            new Mock<IKeyDerivationService>().Object, new Mock<ILogger<LocalLightningSigner>>().Object,
            new NodeOptions(), _secureKeyManagerMock.Object, new Mock<IUtxoMemoryRepository>().Object);
}