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
        Assert.Equal(KeyFileData.CurrentVersion, first.Version);
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
        Assert.Equal(KeyFileData.CurrentVersion, upgraded.Version);
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
        Assert.Equal(KeyFileData.CurrentVersion, upgraded.Version);
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
        Assert.Equal(KeyFileData.CurrentVersion, ReadKeyFile().Version);
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
    public async Task Given_KeyFileWithCustomMode_When_UpdateLastUsedChannelIndexOnFile_Then_KeepsItsPermissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        using var keyManager = NewKeyManager();
        keyManager.SaveToFile(Password);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(_filePath, mode);

        // Act
        await keyManager.UpdateLastUsedChannelIndexOnFile();

        // Assert
        Assert.Equal(mode, File.GetUnixFileMode(_filePath));
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
        Assert.Equal(KeyFileData.CurrentVersion, ReadKeyFile().Version);
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