using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Protocol.ValueObjects;
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

    private SecureKeyManager NewKeyManager()
    {
        return new SecureKeyManager(s_privateKey.ToArray(), BitcoinNetwork.Regtest, _filePath, 123);
    }

    private KeyFileData ReadKeyFile()
    {
        return JsonSerializer.Deserialize<KeyFileData>(File.ReadAllText(_filePath))!;
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
        var extKey = new ExtKey(new Key(s_privateKey.ToArray()), Network.RegTest.GenesisHash.ToBytes());
        var extKeyBytes = Encoding.UTF8.GetBytes(extKey.ToString(Network.RegTest));

        var key = new byte[32];
        using (var argon2Id = new Argon2Id())
            argon2Id.DeriveKeyFromPasswordAndSalt(password, s_legacySalt, key, 3, 1 << 16);

        var cipherText = new byte[extKeyBytes.Length + 16];
        using (var xChaCha20Poly1305 = new XChaCha20Poly1305())
            xChaCha20Poly1305.Encrypt(key, new byte[24], ReadOnlySpan<byte>.Empty, extKeyBytes, cipherText);

        return $$"""
                 {"network":"{{Network.RegTest}}","descriptor":"legacy","lastUsedIndex":7,"encryptedExtKey":"{{Convert.ToBase64String(cipherText)}}","heightOfBirth":123}
                 """;
    }
}