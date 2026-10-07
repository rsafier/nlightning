using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Bitcoin.Enums;
using Domain.Protocol.Enums;
using Domain.Protocol.ValueObjects;
using Domain.Utils;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Crypto.Ciphers;

public sealed class SecureKeyManagerRemoteBoundaryTests
{
    private static SecureKeyManager Create() =>
        new(Enumerable.Repeat((byte)1, 32).ToArray(), BitcoinNetwork.Regtest,
            Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.json"), 0);

    [Theory]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Wpkh, true)]
    public void PublicWalletDerivationPreservesExistingAddresses(AddressType type, bool change)
    {
        using var manager = Create();
        var extKey = ExtKey.CreateFromBytes(type == AddressType.P2Tr
                                                ? manager.GetDepositP2TrKeyAtIndex(42, change)
                                                : manager.GetDepositP2WpkhKeyAtIndex(42, change));
        Assert.Equal(extKey.Neuter().PubKey.ToBytes(), (byte[])manager.GetWalletPublicKey(42, change, type));
    }

    [Theory]
    [InlineData(NodeDataPurpose.PeerStorage)]
    [InlineData(NodeDataPurpose.ChannelBackup)]
    public void NodeDataCipherPreservesLegacyKeyDerivationAndRejectsTampering(NodeDataPurpose purpose)
    {
        using var manager = Create();
        var privateKey = Enumerable.Repeat((byte)1, 32).ToArray();
        var key = purpose == NodeDataPurpose.PeerStorage
                      ? HMACSHA256.HashData(privateKey, "nltg peer storage v1"u8)
                      : HKDF.DeriveKey(HashAlgorithmName.SHA256, privateKey, 32,
                                      "NLightning static channel backup"u8.ToArray(), "nltg-scb-v1"u8.ToArray());
        var nonce = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
        var ad = "header"u8.ToArray();
        var plaintext = "backup data"u8.ToArray();
        var legacyCiphertext = new byte[plaintext.Length + 16];
        using var cipher = new XChaCha20Poly1305();
        cipher.Encrypt(key, nonce, ad, plaintext, legacyCiphertext);
        var ciphertext = manager.EncryptNodeData(purpose, nonce, ad, plaintext);
        Assert.Equal(legacyCiphertext, ciphertext);
        Assert.Equal(plaintext, manager.DecryptNodeData(purpose, nonce, ad, legacyCiphertext));
        ciphertext[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => manager.DecryptNodeData(purpose, nonce, ad, ciphertext));
        CryptographicOperations.ZeroMemory(privateKey);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void InvoiceSignatureMatchesLegacyPaddedMessage()
    {
        using var manager = Create();
        byte[] words = [1, 2, 3, 4, 5, 6, 7, 31, 9];
        using var writer = new BitWriter(words.Length * 5);
        foreach (var word in words)
            writer.WriteByteAsBits(word, 5);
        var hrp = Encoding.ASCII.GetBytes("lnbcrt10n");
        var message = hrp.Concat(writer.ToArray()).ToArray();
        using var nodeKey = new Key(Enumerable.Repeat((byte)1, 32).ToArray());
        var expected = nodeKey.SignCompact(new uint256(SHA256.HashData(message)), false);
        var actual = manager.SignBolt11Invoice("lnbcrt10n", words);
        Assert.Equal(expected.Signature, actual[..64]);
        Assert.Equal(expected.RecoveryId, actual[64]);
        words[0] = 32;
        Assert.Throws<ArgumentException>(() => manager.SignBolt11Invoice("lnbcrt10n", words));
    }

    [Fact]
    public void OfferPathIdMatchesExistingDoubleHmac()
    {
        using var manager = Create();
        var secret = HMACSHA256.HashData(Enumerable.Repeat((byte)1, 32).ToArray(), "nltg_bolt12_offer_paths"u8);
        var metadata = "offer metadata"u8.ToArray();
        var expected = HMACSHA256.HashData(secret, "nltg_bolt12_offer_path"u8.ToArray().Concat(metadata).ToArray());
        Assert.Equal(expected, manager.ComputeOfferPathId(metadata));
        CryptographicOperations.ZeroMemory(secret);
    }
    [Fact]
    public void InjectedSeedUsesBip32IdentityWipesInputAndOnlyPersistsPublicIndexes()
    {
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var master = ExtKey.CreateFromSeed(seed.ToArray());
        var expectedNode = master.Derive(new KeyPath(SecureKeyManager.NodeKeyPathString)).Neuter().PubKey.ToBytes();
        var persisted = new List<uint>();
        using var manager = SecureKeyManager.FromSeed(seed, BitcoinNetwork.Regtest, persisted.Add, 7);
        Assert.All(seed, value => Assert.Equal(0, value));
        Assert.Equal(expectedNode, (byte[])manager.GetNodePubKey());
        Assert.Equal(8u, manager.ReserveChannelKeyIndex());
        Assert.True(manager.EnsureLastUsedChannelIndexAtLeast(20));
        Assert.False(manager.EnsureLastUsedChannelIndexAtLeast(10));
        manager.GetNextChannelKey(out var next);
        Assert.Equal(21u, next);
        Assert.Equal(new uint[] { 8, 20, 21 }, persisted);
        Assert.Throws<InvalidOperationException>(() => manager.SaveToFile("password"));
    }

    [Fact]
    public void InjectedSeedReservationFailsWhenPublicIndexPersistenceFails()
    {
        using var manager = SecureKeyManager.FromSeed(new byte[32], BitcoinNetwork.Regtest,
                                                       _ => throw new IOException("index journal unavailable"));
        Assert.Throws<IOException>(() => manager.ReserveChannelKeyIndex());
        Assert.Throws<IOException>(() => manager.GetNextChannelKey(out _));
        Assert.Throws<IOException>(() => manager.EnsureLastUsedChannelIndexAtLeast(50));
    }

    [Fact]
    public void InvalidInjectedSeedIsWipedBeforeItIsRejected()
    {
        var seed = Enumerable.Repeat((byte)7, 31).ToArray();
        Assert.Throws<ArgumentException>(() => SecureKeyManager.FromSeed(seed, BitcoinNetwork.Regtest, _ => { }));
        Assert.All(seed, value => Assert.Equal(0, value));
    }
}