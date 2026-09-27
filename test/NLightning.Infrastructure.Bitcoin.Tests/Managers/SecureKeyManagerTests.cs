using System.Runtime.Serialization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Crypto.Ciphers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Node.Models;

public sealed class SecureKeyManagerTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    // Fixed salt of version 1 key files (copied from the pre-v2 SecureKeyManager).
    private static readonly byte[] s_legacySalt =
    [
        0xFF, 0x1D, 0x3B, 0xF5, 0x24, 0xA2, 0xB7, 0xA9,
        0xC3, 0x1B, 0x1F, 0x58, 0xE9, 0x48, 0xB5, 0x69
    ];

    private const string LegacyKeyFileFixture =
        "{\"network\":\"RegTest\",\"descriptor\":\"legacy\",\"lastUsedIndex\":7,\"encryptedExtKey\":" +
        "\"fIKuEtKdVqp1Wu8e8d8RdB6vgRcMKDcFL97nIJ1tAsomDv2vV4RsZ2migpRk7tNJPhmcAatqndxHpHqbUIPjEWWgrravS1aCGew+" +
        "vNvnW96kKQDcEEXWjUIDfKkZmDYHUmqgwOcB4HTmFwc8LTm+WIo5G7evjEJLcJ9tnGavEQ==\",\"heightOfBirth\":123}";

    private static readonly byte[] s_privateKey =
        Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101");

    private readonly string _directory;
    private readonly string _filePath;

    public SecureKeyManagerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nltg-skm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "nltg.key.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Given_PublicKey_When_ComputingNodeSharedSecret_Then_MatchesEcdhWithNodeKey()
    {
        // Arrange
        var ecdh = new Ecdh();
        var remoteKey = ecdh.GenerateKeyPair();
        var nodePrivateKey = ecdh.GenerateKeyPair().PrivKey.Value;
        using var keyManager = new SecureKeyManager((byte[])nodePrivateKey.Clone(), BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var expected = new byte[32];
        ecdh.SecP256K1Dh(nodePrivateKey, remoteKey.CompactPubKey, expected);
        var sharedSecret = new byte[32];

        // Act
        keyManager.ComputeNodeSharedSecret(remoteKey.CompactPubKey, sharedSecret);

        // Assert
        Assert.Equal(expected, sharedSecret);
        Assert.Equal(nodePrivateKey, keyManager.GetNodeKeyPair().PrivKey.Value);
    }

    [Fact]
    public void Given_DisposedKeyManager_When_ComputingNodeSharedSecret_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var ecdh = new Ecdh();
        var remoteKey = ecdh.GenerateKeyPair();
        var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(), BitcoinNetwork.Regtest,
                                              Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        keyManager.Dispose();
        var sharedSecret = new byte[32];

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => keyManager.ComputeNodeSharedSecret(remoteKey.CompactPubKey,
                                                                                            sharedSecret));
    }

    [Fact]
    public void Given_InvalidPublicKey_When_ComputingNodeSharedSecret_Then_ThrowsArgumentException()
    {
        // Arrange
        var ecdh = new Ecdh();
        using var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(),
                                                    BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var publicKey = new byte[33];
        publicKey[0] = 0x02;
        var sharedSecret = new byte[32];

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => keyManager.ComputeNodeSharedSecret(publicKey, sharedSecret));
    }

    [Fact]
    public void Given_WarmKeyManager_When_ComputingNodeSharedSecret_Then_AllocatesOnlyTheWipedKeyCopy()
    {
        // Arrange: the peel hot path runs this once per HTLC (twice with a path_key)
        const int iterations = 100;
        var ecdh = new Ecdh();
        byte[] remoteKey = ecdh.GenerateKeyPair().CompactPubKey;
        using var keyManager = new SecureKeyManager(ecdh.GenerateKeyPair().PrivKey.Value.ToArray(),
                                                    BitcoinNetwork.Regtest,
                                                    Path.Combine(Path.GetTempPath(), "unused.key.json"), 0);
        var sharedSecret = new byte[32];
        keyManager.ComputeNodeSharedSecret(remoteKey, sharedSecret);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            keyManager.ComputeNodeSharedSecret(remoteKey, sharedSecret);
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        // Assert: the private key copy and the parsed ECPrivKey, but no hash object or NBitcoin Key/PubKey wrappers
        Assert.True(perCall <= 512, $"Allocated {perCall} bytes per call.");
    }

    [Fact]
    public void Given_NewKey_When_SaveToFile_Then_WritesVersion2WithRandomSaltNonceAndStrongArgon2()
    {
        // Arrange
        using var keyManager = NewKeyManager();

        // Act
        keyManager.SaveToFile(Password);
        var first = ReadKeyFile();
        keyManager.SaveToFile(Password);
        var second = ReadKeyFile();

        // Assert
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, first.Version);
        Assert.Equal(Argon2Id.SaltLen, Convert.FromBase64String(first.Salt!).Length);
        Assert.Equal(24, Convert.FromBase64String(first.Nonce!).Length);
        Assert.Equal(64UL * 1024 * 1024, first.Argon2MemLimit);
        Assert.True(first.Argon2OpsLimit >= 3);
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.EncryptedExtKey, second.EncryptedExtKey);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Given_Version2KeyFile_When_FromFilePath_Then_RoundTripsKeyAndMetadata()
    {
        // Arrange
        using (var keyManager = NewKeyManager())
        {
            keyManager.GetNextChannelKey(out _);
            keyManager.SaveToFile(Password);
        }

        // Act
        using var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);

        // Assert
        Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());
        Assert.Equal(123u, loaded.HeightOfBirth);
        loaded.GetNextChannelKey(out var index);
        Assert.Equal(2u, index);
    }

    [Theory]
    [InlineData("mainnet")] // NBitcoin names it "Main" in the file (NL-403)
    [InlineData("testnet")]
    [InlineData("regtest")]
    [InlineData("signet")]
    public void Given_AKeyFileSavedOnANetwork_When_FromFilePathOnTheSameNetwork_Then_ItLoads(string networkName)
    {
        // Arrange
        var network = BitcoinNetwork.Resolve(networkName);
        using (var keyManager = new SecureKeyManager(s_privateKey.ToArray(), network, _filePath, 7))
            keyManager.SaveToFile(Password);

        // Act
        using var loaded = SecureKeyManager.FromFilePath(_filePath, network, Password);

        // Assert
        Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());
        Assert.Equal(7u, loaded.HeightOfBirth);
    }

    [Fact]
    public void Given_AMainnetKeyFile_When_FromFilePathOnRegtest_Then_Throws()
    {
        // Arrange
        using (var keyManager = new SecureKeyManager(s_privateKey.ToArray(), BitcoinNetwork.Mainnet, _filePath, 7))
            keyManager.SaveToFile(Password);

        // Act / Assert
        var exception = Assert.ThrowsAny<Exception>(() => SecureKeyManager.FromFilePath(_filePath,
                                                             BitcoinNetwork.Regtest, Password));
        Assert.Contains("Invalid network", exception.Message);
    }

    [Fact]
    public void Given_Version2KeyFile_When_FromFilePathWithWrongPassword_Then_Throws()
    {
        // Arrange
        using (var keyManager = NewKeyManager())
            keyManager.SaveToFile(Password);

        // Act / Assert
        Assert.Throws<CryptographicException>(() => SecureKeyManager.FromFilePath(
                                                  _filePath, BitcoinNetwork.Regtest, "wrong password"));
    }

    [Fact]
    public void Given_Version1KeyFile_When_FromFilePath_Then_ReadsKeyAndUpgradesFileToVersion2()
    {
        // Arrange
        File.WriteAllText(_filePath, CreateLegacyKeyFileJson(Password));

        // Act
        using (var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password))
        {
            // Assert
            Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());
            Assert.Equal(123u, loaded.HeightOfBirth);
        }

        var upgraded = ReadKeyFile();
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, upgraded.Version);
        Assert.NotNull(upgraded.Salt);
        Assert.NotEqual(Convert.ToBase64String(s_legacySalt), upgraded.Salt);
        Assert.Equal(7u, upgraded.LastUsedIndex);
        Assert.Equal(123u, upgraded.HeightOfBirth);

        using var reloaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);
        Assert.Equal(ExpectedNodePubKey(), (byte[])reloaded.GetNodePubKey());
    }

    [Fact]
    public void Given_Version1KeyFile_When_FromFilePathWithWrongPassword_Then_ThrowsAndLeavesFileUntouched()
    {
        // Arrange
        var json = CreateLegacyKeyFileJson(Password);
        File.WriteAllText(_filePath, json);

        // Act / Assert
        Assert.Throws<CryptographicException>(() => SecureKeyManager.FromFilePath(
                                                  _filePath, BitcoinNetwork.Regtest, "wrong password"));
        Assert.Equal(json, File.ReadAllText(_filePath));
    }

    [Fact]
    public void Given_UnknownKeyFileVersion_When_FromFilePath_Then_Throws()
    {
        // Arrange
        using (var keyManager = NewKeyManager())
            keyManager.SaveToFile(Password);
        var data = ReadKeyFile();
        data.Version = 99;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data));

        // Act / Assert
        Assert.Throws<System.Runtime.Serialization.SerializationException>(
            () => SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password));
    }

    [Fact]
    public void Given_Version1KeyFileFixture_When_FromFilePath_Then_DecryptsExpectedKey()
    {
        // Arrange: a v1 key file for s_privateKey / Password (fixed salt, zero nonce, 64 KiB Argon2id)
        File.WriteAllText(_filePath, LegacyKeyFileFixture);

        // Act
        using var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);

        // Assert
        Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());
        Assert.Equal(LegacyKeyFileFixture, CreateLegacyKeyFileJson(Password));
    }

    [Fact]
    public void Given_NonAsciiPasswordsDifferingAtTheEnd_When_FromFilePath_Then_OnlyTheRightOneOpensTheFile()
    {
        // Arrange: before the fix, libsodium hashed only the first password.Length UTF-8 bytes, so both matched
        using (var keyManager = NewKeyManager())
            keyManager.SaveToFile("\u00fc\u00fc1");

        // Act / Assert
        Assert.Throws<CryptographicException>(() => SecureKeyManager.FromFilePath(
                                                  _filePath, BitcoinNetwork.Regtest, "\u00fc\u00fc2"));
        using var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, "\u00fc\u00fc1");
        Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());
    }

    [Fact]
    public void Given_Version1FileWithTruncatedNonAsciiPasswordEncoding_When_FromFilePath_Then_OpensAndUpgrades()
    {
        // Arrange: the old libsodium backend hashed the UTF-8 password cut to password.Length bytes
        const string nonAsciiPassword = "p\u00e4ssw\u00f6rd";
        var truncated = Encoding.UTF8.GetBytes(nonAsciiPassword)[..nonAsciiPassword.Length];
        var json = CreateLegacyKeyFileJson(truncated);
        File.WriteAllText(_filePath, json);

        // Act
        using (var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, nonAsciiPassword))
            Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());

        // Assert: upgraded to v2 with the full UTF-8 encoding, which the truncated encoding cannot open
        var upgraded = ReadKeyFile();
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, upgraded.Version);
        Assert.Equal(json, File.ReadAllText(_filePath + ".v1.bak"));
        Assert.False(TryDecryptVersion2(upgraded, truncated));
        Assert.True(TryDecryptVersion2(upgraded, Encoding.UTF8.GetBytes(nonAsciiPassword)));
    }

    [Fact]
    public void Given_Version1KeyFile_When_FromFilePath_Then_KeepsTheOriginalAsBackup()
    {
        // Arrange
        var json = CreateLegacyKeyFileJson(Password);
        File.WriteAllText(_filePath, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Act
        using (SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password))
        {
        }

        // Assert
        var backupPath = _filePath + ".v1.bak";
        Assert.Equal(json, File.ReadAllText(backupPath));
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, ReadKeyFile().Version);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backupPath));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Given_RestrictedKeyFile_When_SaveToFile_Then_KeepsItsPermissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);
        const UnixFileMode restricted = UnixFileMode.UserRead;
        File.SetUnixFileMode(_filePath, restricted | UnixFileMode.UserWrite);

        // Act
        keyManager.SaveToFile(Password);

        // Assert
        Assert.Equal(restricted | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Given_GroupReadableKeyFile_When_UpdateLastUsedChannelIndexOnFile_Then_DropsGroupAndOtherBits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);
        File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        // Act
        await keyManager.UpdateLastUsedChannelIndexOnFile();

        // Assert: the owner bits are kept, the group bit is not (SR-02)
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Given_WorldReadableVersion1KeyFile_When_FromFilePathUpgradesIt_Then_FileAndBackupAreOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange: older builds wrote the key file with the umask's mode, typically 0644
        File.WriteAllText(_filePath, CreateLegacyKeyFileJson(Password));
        File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                                      | UnixFileMode.OtherRead);

        // Act
        using (SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password))
        {
        }

        // Assert
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, ReadKeyFile().Version);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(_filePath));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(_filePath + ".v1.bak"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Given_NoKeyFile_When_SaveToFile_Then_CreatesItOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        using var keyManager = NewKeyManager();

        // Act
        keyManager.SaveToFile(Password);

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
    }

    [Fact]
    public void Given_SymlinkedKeyFile_When_SaveToFile_Then_KeepsTheLinkAndUpdatesTheTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        // Arrange
        var targetPath = Path.Combine(_directory, "real.key.json");
        File.WriteAllText(targetPath, "{}");
        File.CreateSymbolicLink(_filePath, targetPath);
        using var keyManager = NewKeyManager();

        // Act
        keyManager.SaveToFile(Password);

        // Assert
        Assert.NotNull(new FileInfo(_filePath).LinkTarget);
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, ReadKeyFile().Version);
        Assert.Equal(File.ReadAllText(targetPath), File.ReadAllText(_filePath));
    }

    [Fact]
    public void Given_ReplaceFails_When_SaveToFile_Then_LeavesNoTempFile()
    {
        // Arrange: a directory at the key path makes the final move fail
        Directory.CreateDirectory(_filePath);
        using var keyManager = NewKeyManager();

        // Act
        Assert.ThrowsAny<IOException>(() => keyManager.SaveToFile(Password));

        // Assert
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Given_APrivateKeyArray_When_ConstructingTheKeyManager_Then_TheArrayIsZeroed()
    {
        // Arrange
        var privateKey = s_privateKey.ToArray();

        // Act
        using var keyManager = new SecureKeyManager(privateKey, BitcoinNetwork.Regtest, _filePath, 0);

        // Assert
        Assert.All(privateKey, b => Assert.Equal(0, b));
        Assert.Equal(ExpectedNodePubKey(), (byte[])keyManager.GetNodePubKey());
    }

    [Fact]
    public void Given_ALegacyKeyManager_When_GettingTheNodeKey_Then_ItIsTheMasterKeyAndTheSchemeIsLegacy()
    {
        // Arrange
        using var keyManager = NewKeyManager();

        // Act
        var keyPair = keyManager.GetNodeKeyPair();

        // Assert: the node id of every existing node (NL-159 keeps it)
        Assert.Equal(KeyDerivationScheme.LegacyGenesisChainCode, keyManager.DerivationScheme);
        Assert.Equal(s_privateKey, keyPair.PrivKey.Value.ToArray());
        Assert.Equal(ExpectedNodePubKey(), (byte[])keyPair.CompactPubKey);
    }

    [Fact]
    public void Given_TheBip84TestMnemonic_When_ReadingTheDescriptors_Then_EachNamesTheAccountXpubAtItsOrigin()
    {
        // Arrange: the BIP84 test vector; its account xpub (m/84'/0'/0') is the vector's zpub in xpub encoding
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon " +
                                "abandon about";
        const string bip84AccountXpub = "xpub6CatWdiZiodmUeTDp8LT5or8nmbKNcuyvz7WyksVFkKB4RHwCD3XyuvPEbvqAQY3rAPshW" +
                                        "cMLoP2fMFMKHPJ4ZeZXYVUhLv1VMrjPC7PW6V";

        // Act
        using var keyManager = SecureKeyManager.FromMnemonic(mnemonic, string.Empty, BitcoinNetwork.Mainnet,
                                                             _filePath);

        // Assert (SR-13): the descriptor's xpub derives the wallet's keys, and its origin is the account path
        Assert.Equal($"wpkh([73c5da0a/84'/0'/0']{bip84AccountXpub}/0/*)", keyManager.OutputDepositP2WshDescriptor);
        Assert.Equal($"wpkh([73c5da0a/84'/0'/0']{bip84AccountXpub}/1/*)", keyManager.OutputChangeP2WshDescriptor);
        AssertDescriptorDerives(keyManager.OutputDepositP2TrDescriptor, "86'/0'/0'", "/0/*)",
                                keyManager.GetDepositP2TrKeyAtIndex(4, false));
        AssertDescriptorDerives(keyManager.OutputChangeP2TrDescriptor, "86'/0'/0'", "/1/*)",
                                keyManager.GetDepositP2TrKeyAtIndex(4, true));
        AssertDescriptorDerives(keyManager.OutputChannelDescriptor, "6425'/0'/0'/0", "/*)",
                                keyManager.GetChannelKeyAtIndex(4));
        AssertDescriptorDerives(keyManager.OutputDepositP2WshDescriptor, "84'/0'/0'", "/0/*)",
                                keyManager.GetDepositP2WpkhKeyAtIndex(4, false));

        return;

        static void AssertDescriptorDerives(string descriptor, string origin, string suffix, byte[] expectedKey)
        {
            var prefixEnd = descriptor.IndexOf(']');
            Assert.EndsWith($"/{origin}", descriptor[..prefixEnd]);
            Assert.EndsWith(suffix, descriptor);
            var xpub = descriptor[(prefixEnd + 1)..descriptor.LastIndexOf(suffix, StringComparison.Ordinal)];
            var branch = suffix == "/*)" ? string.Empty : suffix[1..2] + "/";
            var derived = ExtPubKey.Parse(xpub, Network.Main).Derive(new KeyPath(branch + "4"));
            Assert.Equal(ExtKey.CreateFromBytes(expectedKey).Neuter().PubKey, derived.PubKey);
        }
    }

    [Fact]
    public void Given_AnUpgradedVersion1KeyFile_When_Loaded_Then_WarnsAboutTheWeakBackup()
    {
        // Arrange
        File.WriteAllText(_filePath, CreateLegacyKeyFileJson(Password));
        Assert.Null(SecureKeyManager.GetWeakBackupWarning(_filePath));

        // Act
        using (SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password))
        {
        }

        var warning = SecureKeyManager.GetWeakBackupWarning(_filePath);
        File.Delete(_filePath + ".v1.bak");

        // Assert (SR-18): the notice names the backup until it is deleted
        Assert.NotNull(warning);
        Assert.Contains(_filePath + ".v1.bak", warning);
        Assert.Null(SecureKeyManager.GetWeakBackupWarning(_filePath));
    }

    [Fact]
    public void Given_Version1KeyFile_When_Upgraded_Then_KeepsTheNodeIdAndTheLegacyDerivation()
    {
        // Arrange
        File.WriteAllText(_filePath, CreateLegacyKeyFileJson(Password));
        byte[] channelKeyBefore;
        using (var legacy = NewKeyManager())
            channelKeyBefore = legacy.GetChannelKeyAtIndex(3);

        // Act
        using (SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password))
        {
        }

        using var reloaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);

        // Assert: upgraded to version 2, never to the BIP32 version 3
        var upgraded = ReadKeyFile();
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, upgraded.Version);
        Assert.Null(upgraded.NodeKeyPath);
        Assert.Equal(KeyDerivationScheme.LegacyGenesisChainCode, reloaded.DerivationScheme);
        Assert.Equal(ExpectedNodePubKey(), (byte[])reloaded.GetNodePubKey());
        Assert.Equal(channelKeyBefore, (byte[])reloaded.GetChannelKeyAtIndex(3));
    }

    [Fact]
    public void Given_ANewNode_When_CreateNewAndSaveToFile_Then_WritesVersion3WithTheNodeKeyOnItsOwnPath()
    {
        // Arrange
        using var keyManager = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, _filePath, 42);

        // Act
        keyManager.SaveToFile(Password);

        // Assert
        var data = ReadKeyFile();
        Assert.Equal(KeyFileData.Bip32Version, data.Version);
        Assert.Equal(SecureKeyManager.NodeKeyPathString, data.NodeKeyPath);
        Assert.Equal(KeyDerivationScheme.Bip32, keyManager.DerivationScheme);
        var master = DecryptMasterKey(data, Network.RegTest);
        Assert.Equal(0, master.Depth);
        Assert.NotEqual(Network.RegTest.GenesisHash.ToBytes(), master.ChainCode.ToArray());
        var expectedNodeKey = master.Derive(new KeyPath(SecureKeyManager.NodeKeyPathString)).PrivateKey;
        Assert.Equal(expectedNodeKey.PubKey.ToBytes(), (byte[])keyManager.GetNodePubKey());
        Assert.Equal(expectedNodeKey.ToBytes(), keyManager.GetNodeKeyPair().PrivKey.Value.ToArray());
        Assert.NotEqual(master.PrivateKey.PubKey.ToBytes(), (byte[])keyManager.GetNodePubKey());
    }

    [Fact]
    public void Given_Version3KeyFile_When_FromFilePath_Then_KeepsTheNodeIdAndTheWalletKeys()
    {
        // Arrange
        byte[] nodeId;
        byte[] depositKey;
        string descriptor;
        using (var created = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, _filePath, 42))
        {
            created.GetNextChannelKey(out _);
            created.SaveToFile(Password);
            nodeId = created.GetNodePubKey();
            depositKey = created.GetDepositP2WpkhKeyAtIndex(5, false);
            descriptor = created.OutputDepositP2WshDescriptor;
        }

        // Act
        using var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);

        // Assert
        Assert.Equal(KeyDerivationScheme.Bip32, loaded.DerivationScheme);
        Assert.Equal(nodeId, (byte[])loaded.GetNodePubKey());
        Assert.Equal(depositKey, (byte[])loaded.GetDepositP2WpkhKeyAtIndex(5, false));
        Assert.Equal(descriptor, loaded.OutputDepositP2WshDescriptor);
        Assert.Equal(42u, loaded.HeightOfBirth);
        loaded.GetNextChannelKey(out var index);
        Assert.Equal(2u, index);
        Assert.Equal(KeyFileData.Bip32Version, ReadKeyFile().Version);
        Assert.False(File.Exists(_filePath + ".v1.bak"));
    }

    [Fact]
    public void Given_Version3KeyFileWithAnotherNodeKeyPath_When_FromFilePath_Then_Throws()
    {
        // Arrange
        using (var created = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, _filePath, 42))
            created.SaveToFile(Password);
        var data = ReadKeyFile();
        data.NodeKeyPath = "m/1017'/0'/6'/0/1";
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data));

        // Act & Assert
        Assert.Throws<SerializationException>(
            () => SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password));
    }

    [Fact]
    public void Given_TheBip84TestMnemonic_When_FromMnemonic_Then_DerivesTheStandardBip32Keys()
    {
        // Arrange: the BIP84 test vector (https://github.com/bitcoin/bips/blob/master/bip-0084.mediawiki)
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon " +
                                "abandon about";

        // Act
        using var keyManager = SecureKeyManager.FromMnemonic(mnemonic, string.Empty, BitcoinNetwork.Mainnet,
                                                             _filePath);

        // Assert: m/84'/0'/0'/0/0 of the vector, so the seed restores in any BIP32 wallet
        var firstReceiveKey = ExtKey.CreateFromBytes(keyManager.GetDepositP2WpkhKeyAtIndex(0, false)).PrivateKey;
        Assert.Equal("KyZpNDKnfs94vbrwhJneDi77V6jF64PWPF8x5cdJb8ifgg2DUc9d",
                     firstReceiveKey.GetWif(Network.Main).ToString());
        Assert.Equal("bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu",
                     firstReceiveKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());
        var root = ExtKey.Parse("xprv9s21ZrQH143K3GJpoapnV8SFfukcVBSfeCficPSGfubmSFDxo1kuHnLisriDvSnRRuL2Qrg5ggqHK" +
                                "NVpxR86QEC8w35uxmGoggxtQTPvfUu", Network.Main);
        Assert.Equal(root.Derive(new KeyPath(SecureKeyManager.NodeKeyPathString)).PrivateKey.PubKey.ToBytes(),
                     (byte[])keyManager.GetNodePubKey());
    }

    [Fact]
    public void Given_Version1FileWithAnAnsiCodePagePassword_When_FromFilePathWithTheAnsiEncoder_Then_OpensAndUpgrades()
    {
        // Arrange: on Windows the old libsodium P/Invoke marshalled the password as LPStr (ANSI code page, NL-212);
        // Latin-1 stands in for code page 1252 here, where both give the same bytes for these characters
        const string nonAsciiPassword = "pässwörd";
        var ansiBytes = Encoding.Latin1.GetBytes(nonAsciiPassword);
        var json = CreateLegacyKeyFileJson(ansiBytes);
        File.WriteAllText(_filePath, json);

        // Act
        using (var loaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, nonAsciiPassword,
                                                          p => Encoding.Latin1.GetBytes(p)))
            Assert.Equal(ExpectedNodePubKey(), (byte[])loaded.GetNodePubKey());

        // Assert: rewritten as version 2 with the full UTF-8 password, the original kept
        var upgraded = ReadKeyFile();
        Assert.Equal(KeyFileData.GenesisChainCodeVersion, upgraded.Version);
        Assert.Equal(json, File.ReadAllText(_filePath + ".v1.bak"));
        Assert.True(TryDecryptVersion2(upgraded, Encoding.UTF8.GetBytes(nonAsciiPassword)));
    }

    [Fact]
    public void Given_Version1FileWithAnAnsiCodePagePassword_When_FromFilePathWithoutTheAnsiEncoder_Then_Throws()
    {
        // Arrange
        const string nonAsciiPassword = "pässwörd";
        File.WriteAllText(_filePath, CreateLegacyKeyFileJson(Encoding.Latin1.GetBytes(nonAsciiPassword)));

        // Act & Assert
        Assert.Throws<CryptographicException>(() => SecureKeyManager.FromFilePath(
                                                  _filePath, BitcoinNetwork.Regtest, nonAsciiPassword, null));
    }

    [Fact]
    public void Given_APassword_When_GetSystemAnsiPasswordBytes_Then_ReturnsItsAnsiMarshalling()
    {
        // Arrange: Marshal.StringToHGlobalAnsi is the LPStr marshalling (UTF-8 on Unix, the ANSI code page on Windows)
        const string password = "correct horse";

        // Act
        var bytes = SecureKeyManager.GetSystemAnsiPasswordBytes(password);

        // Assert
        Assert.Equal(Encoding.ASCII.GetBytes(password), bytes);
    }

    [Fact]
    public void Given_ASavedKeyFile_When_GetNextChannelKey_Then_TheIndexIsOnFileBeforeItReturns()
    {
        // Arrange: the index used to be written fire-and-forget, so a crash could lose it and a restart reuse it
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);

        // Act
        keyManager.GetNextChannelKey(out var index);

        // Assert: read right away, no waiting for a background write
        Assert.Equal(1u, index);
        Assert.Equal(1u, ReadKeyFile().LastUsedIndex);
    }

    [Fact]
    public async Task Given_ConcurrentChannelOpens_When_GetNextChannelKey_Then_IndexesAreUniqueAndTheFileHasTheHighest()
    {
        // Arrange
        const int opens = 16;
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);

        // Act
        var indexes = await Task.WhenAll(Enumerable.Range(0, opens).Select(_ => Task.Run(() =>
        {
            keyManager.GetNextChannelKey(out var index);
            return index;
        }, TestContext.Current.CancellationToken)));

        // Assert: a late write never overwrites a higher index
        Assert.Equal(opens, indexes.Distinct().Count());
        Assert.Equal((uint)opens, ReadKeyFile().LastUsedIndex);
        using var reloaded = SecureKeyManager.FromFilePath(_filePath, BitcoinNetwork.Regtest, Password);
        reloaded.GetNextChannelKey(out var next);
        Assert.Equal((uint)opens + 1, next);
    }

    [Fact]
    public async Task Given_AKeyFileWithAHigherIndex_When_UpdateLastUsedChannelIndexOnFile_Then_ItIsNotLowered()
    {
        // Arrange
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);
        var data = ReadKeyFile();
        data.LastUsedIndex = 50;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data));

        // Act
        await keyManager.UpdateLastUsedChannelIndexOnFile();

        // Assert
        Assert.Equal(50u, ReadKeyFile().LastUsedIndex);
    }

    private static ExtKey DecryptMasterKey(KeyFileData data, Network network)
    {
        var key = new byte[32];
        using (var argon2Id = new Argon2Id())
            argon2Id.DeriveKeyFromPasswordBytesAndSalt(Encoding.UTF8.GetBytes(Password),
                                                       Convert.FromBase64String(data.Salt!), key,
                                                       data.Argon2OpsLimit, data.Argon2MemLimit);

        var cipherText = Convert.FromBase64String(data.EncryptedExtKey);
        var plainText = new byte[cipherText.Length - 16];
        using (var xChaCha20Poly1305 = new XChaCha20Poly1305())
            xChaCha20Poly1305.Decrypt(key, Convert.FromBase64String(data.Nonce!), ReadOnlySpan<byte>.Empty,
                                      cipherText, plainText);

        return ExtKey.Parse(Encoding.UTF8.GetString(plainText), network);
    }

    private SecureKeyManager NewKeyManager()
    {
        return new SecureKeyManager(s_privateKey.ToArray(), BitcoinNetwork.Regtest, _filePath, 123);
    }

    private KeyFileData ReadKeyFile()
    {
        return JsonSerializer.Deserialize<KeyFileData>(File.ReadAllText(_filePath))!;
    }

    private static bool TryDecryptVersion2(KeyFileData data, byte[] passwordBytes)
    {
        var key = new byte[32];
        using (var argon2Id = new Argon2Id())
            argon2Id.DeriveKeyFromPasswordBytesAndSalt(passwordBytes, Convert.FromBase64String(data.Salt!), key,
                                                       data.Argon2OpsLimit, data.Argon2MemLimit);

        var cipherText = Convert.FromBase64String(data.EncryptedExtKey);
        var plainText = new byte[cipherText.Length - 16];
        try
        {
            using var xChaCha20Poly1305 = new XChaCha20Poly1305();
            xChaCha20Poly1305.Decrypt(key, Convert.FromBase64String(data.Nonce!), ReadOnlySpan<byte>.Empty,
                                      cipherText, plainText);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] ExpectedNodePubKey()
    {
        return new Key(s_privateKey.ToArray()).PubKey.ToBytes();
    }

    /// <summary>
    /// Builds a key file exactly like the pre-v2 SecureKeyManager did: fixed salt, all-zero nonce,
    /// Argon2id with 3 passes and a 64 KiB memory limit, and no version field.
    /// </summary>
    private static string CreateLegacyKeyFileJson(string password)
    {
        return CreateLegacyKeyFileJson(Encoding.UTF8.GetBytes(password));
    }

    private static string CreateLegacyKeyFileJson(byte[] passwordBytes)
    {
        var extKey = new ExtKey(new Key(s_privateKey.ToArray()), Network.RegTest.GenesisHash.ToBytes());
        var extKeyBytes = Encoding.UTF8.GetBytes(extKey.ToString(Network.RegTest));

        var key = new byte[32];
        using (var argon2Id = new Argon2Id())
            argon2Id.DeriveKeyFromPasswordBytesAndSalt(passwordBytes, s_legacySalt, key, 3, 1 << 16);

        var cipherText = new byte[extKeyBytes.Length + 16];
        using (var xChaCha20Poly1305 = new XChaCha20Poly1305())
            xChaCha20Poly1305.Encrypt(key, new byte[24], ReadOnlySpan<byte>.Empty, extKeyBytes, cipherText);

        return $$"""
                 {"network":"{{Network.RegTest}}","descriptor":"legacy","lastUsedIndex":7,"encryptedExtKey":"{{Convert.ToBase64String(cipherText)}}","heightOfBirth":123}
                 """;
    }
}