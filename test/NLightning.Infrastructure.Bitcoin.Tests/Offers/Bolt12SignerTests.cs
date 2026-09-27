using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Offers;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Offers.Constants;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Contexts;
using Infrastructure.Bitcoin.Offers;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// BOLT 12 plan B1: BIP-340 signatures over the tagged Merkle root (B1-T1, against <c>bolt12/signature-test.json</c>),
/// the signer's node, payer and blinded-recipient keys (B1-T2, B1-T3), and that no secret leaves the signer.
/// </summary>
public class Bolt12SignerTests
{
    // signature-test.json, invoice_request case: Bob (0x42 x 32) is the payer
    private static readonly byte[] s_bobKey = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly CompactPubKey s_bobId =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    // route-blinding-test.json (BOLT 4), unblind case, hop Eve: her node key, the path_key she receives, the blinded
    // private key it gives and the matching blinded_node_id of the generate case
    private static readonly byte[] s_eveKey = Enumerable.Repeat((byte)0x45, 32).ToArray();
    private static readonly CompactPubKey s_evePathKey =
        new(Convert.FromHexString("03e09038ee76e50f444b19abf0a555e8697e035f62937168b80adf0931b31ce52a"));
    private static readonly byte[] s_eveBlindedKey =
        Convert.FromHexString("ff4e07da8d92838bedd019ce532eb990ed73b574e54a67862a1df81b40c0d2af");
    private static readonly CompactPubKey s_eveBlindedNodeId =
        new(Convert.FromHexString("021982a48086cb8984427d3727fe35a03d396b234f0701f5249daa12e8105c8dae"));

    private static readonly Hash s_someRoot = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly List<byte[]> _handedOutNodeKeys = [];

    [Fact]
    public void Given_SignatureTestVector_When_TaggedHashComputed_Then_EqualsVectorDigest()
    {
        // Arrange
        var vector = SignatureVector.Load();

        // Act
        var digest = Bolt12TaggedHash.Compute(vector.Tag, (byte[])vector.MerkleRoot);

        // Assert
        Assert.Equal(Bolt12Constants.InvoiceRequestSignatureTag, vector.Tag);
        Assert.Equal(vector.Digest, digest);
    }

    [Fact]
    public void Given_SignatureTestVector_When_Verified_Then_SignatureIsValidForPayerId()
    {
        // Arrange
        var vector = SignatureVector.Load();
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));

        // Act
        var valid = bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId, vector.Signature);

        // Assert
        Assert.True(valid);
    }

    [Fact]
    public void Given_SignatureTestVector_When_SignedWithTheVectorKey_Then_SignatureBytesEqualVector()
    {
        // Arrange: the vector signs deterministically (BIP-340 with 32 zero bytes of aux randomness, as CLN). This is
        // the BIP-340 path every key kind of the signer shares; a derived payer key is never 0x42, so the payer
        // derivation is covered by the formula and sign/verify tests instead
        var vector = SignatureVector.Load();
        Assert.True(NLightningCryptoContext.Instance.TryCreateECPrivKey(s_bobKey, out var bobKey));

        // Act
        byte[] signature;
        using (bobKey)
            signature = Bolt12TaggedHash.SignBip340(bobKey!, vector.Tag, (byte[])vector.MerkleRoot);

        // Assert
        Assert.Equal(vector.Signature, signature);
    }

    [Fact]
    public void Given_SignatureTestVector_When_VerifiedWithWrongTagKeyRootOrBit_Then_False()
    {
        // Arrange
        var vector = SignatureVector.Load();
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));
        var otherKey = new CompactPubKey(new Key(Enumerable.Repeat((byte)0x41, 32).ToArray()).PubKey.ToBytes());
        var otherRoot = ((byte[])vector.MerkleRoot).ToArray();
        otherRoot[0] ^= 0x01;

        // Act & Assert
        Assert.False(bolt12Signer.Verify(Bolt12Constants.InvoiceSignatureTag, vector.MerkleRoot, s_bobId,
                                         vector.Signature));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, otherKey, vector.Signature));
        Assert.False(bolt12Signer.Verify(vector.Tag, otherRoot, s_bobId, vector.Signature));
        for (var bit = 0; bit < 512; bit += 37)
        {
            var flipped = vector.Signature.ToArray();
            flipped[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId, flipped));
        }
    }

    [Fact]
    public void Given_PayerIdWithOtherParity_When_Verified_Then_ParityIsIgnored()
    {
        // Arrange: BIP-340 keys are x-only, so 02 || x verifies what 03 || x signed
        var vector = SignatureVector.Load();
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));
        var otherParity = ((byte[])s_bobId).ToArray();
        otherParity[0] = 0x02;

        // Act
        var valid = bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, otherParity, vector.Signature);

        // Assert
        Assert.True(valid);
    }

    [Fact]
    public void Given_MalformedInputs_When_Verified_Then_FalseWithoutThrowing()
    {
        // Arrange
        var vector = SignatureVector.Load();
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));
        var notAPoint = Convert.FromHexString("02" + new string('f', 64));
        var overflowSignature = Enumerable.Repeat((byte)0xFF, 64).ToArray();

        // Act & Assert
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId, vector.Signature[..63]));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId,
                                         vector.Signature.Concat(new byte[] { 0 }).ToArray()));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId, ReadOnlyMemory<byte>.Empty));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, new CompactPubKey(notAPoint),
                                         vector.Signature));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, s_bobId, overflowSignature));
        Assert.False(bolt12Signer.Verify(vector.Tag, default, s_bobId, vector.Signature));
        Assert.False(bolt12Signer.Verify(vector.Tag, vector.MerkleRoot, default, vector.Signature));
        Assert.False(bolt12Signer.Verify(null!, vector.MerkleRoot, s_bobId, vector.Signature));
    }

    [Fact]
    public void Given_NodeKey_When_SignAsNode_Then_SignatureVerifiesUnderNodeId()
    {
        // Arrange
        var signer = CreateSigner(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var bolt12Signer = new Bolt12Signer(signer);

        // Act
        var signature = bolt12Signer.SignAsNode(Bolt12Constants.InvoiceSignatureTag, s_someRoot);

        // Assert
        Assert.Equal(64, signature.Length);
        Assert.True(bolt12Signer.Verify(Bolt12Constants.InvoiceSignatureTag, s_someRoot, signer.GetNodePublicKey(),
                                        signature));
        Assert.Equal(signature, bolt12Signer.SignAsNode(Bolt12Constants.InvoiceSignatureTag, s_someRoot));
    }

    [Fact]
    public void Given_Metadata_When_DerivePayerId_Then_DeterministicAndIndependentOfOtherMetadataAndNodeId()
    {
        // Arrange
        var signer = CreateSigner(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var bolt12Signer = new Bolt12Signer(signer);
        var metadata = Convert.FromHexString("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        var otherMetadata = metadata.ToArray();
        otherMetadata[^1] ^= 0x01;

        // Act
        var payerId = bolt12Signer.DerivePayerId(metadata);
        var again = bolt12Signer.DerivePayerId(metadata.ToArray());
        var otherPayerId = bolt12Signer.DerivePayerId(otherMetadata);

        // Assert
        Assert.Equal(payerId, again);
        Assert.NotEqual(payerId, otherPayerId);
        Assert.NotEqual(signer.GetNodePublicKey(), payerId);
        Assert.NotEqual(signer.GetNodePublicKey(), otherPayerId);
    }

    [Fact]
    public void Given_SameMetadata_When_DerivedByAnotherNode_Then_PayerIdsDiffer()
    {
        // Arrange: the payer key depends on the node secret, so the metadata alone does not give it away
        var metadata = new byte[8];

        // Act
        var ours = CreateSigner(Enumerable.Repeat((byte)0x11, 32).ToArray()).GetBolt12PayerId(metadata);
        var theirs = CreateSigner(Enumerable.Repeat((byte)0x12, 32).ToArray()).GetBolt12PayerId(metadata);

        // Assert
        Assert.NotEqual(ours, theirs);
    }

    [Fact]
    public void Given_Metadata_When_DerivePayerId_Then_FollowsThePlanFormula()
    {
        // Arrange: offers_secret = HMAC-SHA256(node_key, "nltg_bolt12"),
        // payer_key = HMAC-SHA256(offers_secret, "nltg_bolt12_payer" || invreq_metadata) (plan §3.6)
        var nodeKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var metadata = Convert.FromHexString("deadbeefcafebabe");
        var offersSecret = HMACSHA256.HashData(nodeKey, "nltg_bolt12"u8);
        var payerSecret = HMACSHA256.HashData(offersSecret, "nltg_bolt12_payer"u8.ToArray().Concat(metadata).ToArray());
        using var expectedKey = new Key(payerSecret);

        // Act
        var payerId = CreateSigner(nodeKey).GetBolt12PayerId(metadata);

        // Assert
        Assert.Equal(new CompactPubKey(expectedKey.PubKey.ToBytes()), payerId);
    }

    [Fact]
    public void Given_Metadata_When_SignAsPayer_Then_SignatureVerifiesUnderDerivedPayerIdOnly()
    {
        // Arrange
        var signer = CreateSigner(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var bolt12Signer = new Bolt12Signer(signer);
        var metadata = RandomNumberGenerator.GetBytes(32);
        var tag = Bolt12Constants.InvoiceRequestSignatureTag;

        // Act
        var signature = bolt12Signer.SignAsPayer(metadata, tag, s_someRoot);

        // Assert
        Assert.True(bolt12Signer.Verify(tag, s_someRoot, bolt12Signer.DerivePayerId(metadata), signature));
        Assert.False(bolt12Signer.Verify(tag, s_someRoot, signer.GetNodePublicKey(), signature));
        Assert.False(bolt12Signer.Verify(tag, s_someRoot, bolt12Signer.DerivePayerId(RandomNumberGenerator.GetBytes(32)),
                                         signature));
    }

    [Fact]
    public void Given_EmptyMetadata_When_PayerKeyUsed_Then_Throws()
    {
        // Arrange
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => bolt12Signer.DerivePayerId(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsPayer(ReadOnlyMemory<byte>.Empty,
                                                                        Bolt12Constants.InvoiceRequestSignatureTag,
                                                                        s_someRoot));
    }

    [Theory]
    [InlineData("TapSighash")]
    [InlineData("BIP0340/challenge")]
    [InlineData("lightninginvoice_request")]
    [InlineData("invoice_requestsignature")]
    [InlineData("lightningsignature")]
    [InlineData("lightningfoosignature")]
    [InlineData("lightningofferssignature")]
    [InlineData("lightninginvoice_errorsignature")]
    [InlineData("Lightninginvoicesignature")]
    [InlineData("")]
    public void Given_NonBolt12Tag_When_Signing_Then_Refused(string tag)
    {
        // Arrange: the keys only ever sign BOLT 12 signature digests
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsNode(tag, s_someRoot));
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsPayer(new byte[8], tag, s_someRoot));
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsBlindedRecipient(s_evePathKey, tag, s_someRoot));
    }

    [Fact]
    public void Given_Bolt12TagOfTheOtherMessage_When_Signing_Then_RefusedForThatKeyKind()
    {
        // Arrange: a payer key signs only invoice_requests; the node and blinded keys sign only invoices
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_bobKey));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsNode(Bolt12Constants.InvoiceRequestSignatureTag,
                                                                       s_someRoot));
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsPayer(new byte[8],
                                                                        Bolt12Constants.InvoiceSignatureTag,
                                                                        s_someRoot));
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsBlindedRecipient(
                                             s_evePathKey, Bolt12Constants.InvoiceRequestSignatureTag, s_someRoot));
    }

    [Fact]
    public void Given_RouteBlindingVectorHop_When_SignAsBlindedRecipient_Then_VerifiesUnderBlindedNodeId()
    {
        // Arrange
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_eveKey));
        var tag = Bolt12Constants.InvoiceSignatureTag;

        // Act
        var signature = bolt12Signer.SignAsBlindedRecipient(s_evePathKey, tag, s_someRoot);

        // Assert: the key is the vector's blinded private key, whose public key is Eve's blinded_node_id
        Assert.True(bolt12Signer.Verify(tag, s_someRoot, s_eveBlindedNodeId, signature));
        Assert.False(bolt12Signer.Verify(tag, s_someRoot, CreateSigner(s_eveKey).GetNodePublicKey(), signature));
        Assert.True(NLightningCryptoContext.Instance.TryCreateECPrivKey(s_eveBlindedKey, out var expected));
        using (expected)
        {
            var expectedSignature = new byte[64];
            expected!.SignBIP340(Bolt12TaggedHash.Compute(tag, (byte[])s_someRoot), new byte[32])
                     .WriteToSpan(expectedSignature);
            Assert.Equal(expectedSignature, signature);
        }
    }

    [Fact]
    public void Given_InvalidPathKey_When_SignAsBlindedRecipient_Then_Throws()
    {
        // Arrange
        var bolt12Signer = new Bolt12Signer(CreateSigner(s_eveKey));
        var notAPoint = new CompactPubKey(Convert.FromHexString("02" + new string('f', 64)));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => bolt12Signer.SignAsBlindedRecipient(
                                             notAPoint, Bolt12Constants.InvoiceSignatureTag, s_someRoot));
    }

    [Fact]
    public void Given_EveryBolt12Operation_When_Done_Then_NodeKeyCopiesAreWipedAndNoSecretIsReturned()
    {
        // Arrange
        var nodeKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var bolt12Signer = new Bolt12Signer(CreateSigner(nodeKey));
        var metadata = new byte[] { 1, 2, 3, 4 };
        var offersSecret = HMACSHA256.HashData(nodeKey, "nltg_bolt12"u8);
        var payerSecret = HMACSHA256.HashData(offersSecret, "nltg_bolt12_payer"u8.ToArray().Concat(metadata).ToArray());

        // Act
        var outputs = new List<byte[]>
        {
            bolt12Signer.SignAsNode(Bolt12Constants.InvoiceSignatureTag, s_someRoot),
            bolt12Signer.SignAsPayer(metadata, Bolt12Constants.InvoiceRequestSignatureTag, s_someRoot),
            (byte[])bolt12Signer.DerivePayerId(metadata),
            bolt12Signer.SignAsBlindedRecipient(s_evePathKey, Bolt12Constants.InvoiceSignatureTag, s_someRoot)
        };

        // Assert: every node-key copy the key manager handed out was zeroed, and no output holds a secret
        Assert.NotEmpty(_handedOutNodeKeys);
        Assert.All(_handedOutNodeKeys, copy => Assert.All(copy, b => Assert.Equal(0, b)));
        foreach (var output in outputs)
        {
            Assert.False(Contains(output, nodeKey));
            Assert.False(Contains(output, offersSecret));
            Assert.False(Contains(output, payerSecret));
        }

        // The port that the Application sees exposes no key material at all
        Assert.DoesNotContain(typeof(IBolt12Signer).GetMethods(),
                              m => m.ReturnType == typeof(PrivKey) || m.ReturnType == typeof(Secret));
    }

    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingBolt12Signer_Then_SingletonOverTheLightningSigner()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<IBolt12Signer>();
        var second = provider.GetRequiredService<IBolt12Signer>();

        // Assert
        Assert.IsType<Bolt12Signer>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Given_SignerWithoutBolt12Support_When_Called_Then_NotSupported()
    {
        // Arrange: the default members keep other ILightningSigner implementations (test signers) compiling
        var signer = new Mock<ILightningSigner> { CallBase = true }.Object;

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => signer.GetBolt12PayerId(new byte[8]));
        Assert.Throws<NotSupportedException>(() => signer.SignBolt12(Bolt12SigningKey.Node,
                                                                     Bolt12Constants.InvoiceSignatureTag, s_someRoot));
    }

    private LocalLightningSigner CreateSigner(byte[] nodeKey)
    {
        using var key = new Key(nodeKey);
        var nodeId = new CompactPubKey(key.PubKey.ToBytes());
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(x => x.GetNodeKeyPair()).Returns(() =>
        {
            // The real key manager returns a fresh copy per call, which the signer must wipe
            var copy = nodeKey.ToArray();
            _handedOutNodeKeys.Add(copy);
            return new CryptoKeyPair(copy, nodeId);
        });

        return new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object,
                                        new Mock<IKeyDerivationService>().Object,
                                        new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(),
                                        keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0 || haystack.AsSpan().IndexOf(needle.AsSpan(0, 16)) >= 0;

    private sealed record SignatureVector(string Tag, Hash MerkleRoot, byte[] Digest, byte[] Signature)
    {
        public static SignatureVector Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Offers", "Vectors", "signature-test.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var invoiceRequestCase = document.RootElement.EnumerateArray()
                                             .Single(e => e.TryGetProperty("signature", out _));
            return new SignatureVector(
                invoiceRequestCase.GetProperty("signature_tag").GetString()!,
                Convert.FromHexString(invoiceRequestCase.GetProperty("merkle").GetString()!),
                Convert.FromHexString(invoiceRequestCase.GetProperty("H(signature_tag,merkle)").GetString()!),
                Convert.FromHexString(invoiceRequestCase.GetProperty("signature").GetString()!));
        }
    }
}