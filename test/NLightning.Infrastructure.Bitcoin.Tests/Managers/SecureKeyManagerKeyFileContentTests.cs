using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Bitcoin.Tests.Managers;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Managers;

/// <summary>
/// A key file opened from memory (locked start, NL-1349): the same node id as from disk, nothing ever written.
/// </summary>
public sealed class SecureKeyManagerKeyFileContentTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    // A version 1 key file for 0x01 x 32 / Password (the fixture of SecureKeyManagerTests)
    private const string LegacyKeyFileFixture =
        "{\"network\":\"RegTest\",\"descriptor\":\"legacy\",\"lastUsedIndex\":7,\"encryptedExtKey\":" +
        "\"fIKuEtKdVqp1Wu8e8d8RdB6vgRcMKDcFL97nIJ1tAsomDv2vV4RsZ2migpRk7tNJPhmcAatqndxHpHqbUIPjEWWgrravS1aCGew+" +
        "vNvnW96kKQDcEEXWjUIDfKkZmDYHUmqgwOcB4HTmFwc8LTm+WIo5G7evjEJLcJ9tnGavEQ==\",\"heightOfBirth\":123}";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nltg-keycontent-{Guid.NewGuid():N}");

    public SecureKeyManagerKeyFileContentTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Given_AKeyFileVersion_When_OpenedFromMemory_Then_ItHasTheNodeIdOfAStartFromDisk(int version)
    {
        // Arrange
        var path = Path.Combine(_directory, "key.json");
        if (version == 1)
        {
            File.WriteAllText(path, LegacyKeyFileFixture);
        }
        else
        {
            using var created = version == 3
                                    ? SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, path, 5)
                                    : new SecureKeyManager(RandomNumberGenerator.GetBytes(32), BitcoinNetwork.Regtest,
                                                           path, 5);
            created.SaveToFile(Password);
        }

        var content = File.ReadAllText(path);
        File.Delete(path);

        // Act
        using var fromMemory = SecureKeyManager.FromKeyFileContent(content, BitcoinNetwork.Regtest, Password, _ => { });
        File.WriteAllText(path, content);
        using var fromDisk = SecureKeyManager.FromFilePath(path, BitcoinNetwork.Regtest, Password);

        // Assert: same identity and derivation; the in-memory open wrote nothing (no v1 upgrade, no backup)
        Assert.Equal((byte[])fromDisk.GetNodePubKey(), (byte[])fromMemory.GetNodePubKey());
        Assert.Equal(fromDisk.DerivationScheme, fromMemory.DerivationScheme);
        Assert.Equal(fromDisk.OutputChannelDescriptor, fromMemory.OutputChannelDescriptor);
        Assert.Equal(fromDisk.HeightOfBirth, fromMemory.HeightOfBirth);
    }

    [Fact]
    public void Given_AV1KeyFile_When_OpenedFromMemory_Then_NoFileIsWritten()
    {
        // Act
        using var keyManager = SecureKeyManager.FromKeyFileContent(LegacyKeyFileFixture, BitcoinNetwork.Regtest,
                                                                   Password, _ => { });

        // Assert
        Assert.Equal(KeyDerivationScheme.LegacyGenesisChainCode, keyManager.DerivationScheme);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
        Assert.Throws<InvalidOperationException>(() => keyManager.SaveToFile(Password));
    }

    [Fact]
    public void Given_AWrongPasswordOrNetwork_When_Opened_Then_ItThrowsWithoutTheSecret()
    {
        // Act / Assert
        var wrongPassword = Assert.Throws<CryptographicException>(() =>
            SecureKeyManager.FromKeyFileContent(LegacyKeyFileFixture, BitcoinNetwork.Regtest, "nope", _ => { }));
        Assert.DoesNotContain("nope", wrongPassword.Message);
        Assert.ThrowsAny<Exception>(() =>
            SecureKeyManager.FromKeyFileContent(LegacyKeyFileFixture, BitcoinNetwork.Mainnet, Password, _ => { }));
    }

    [Fact]
    public void Given_AnIndexKnownOutsideTheFile_When_KeysAreReserved_Then_TheLargerIndexWinsAndIsPersisted()
    {
        // Arrange: the v1 fixture says 7, the outside store says 10
        var persisted = new List<uint>();

        // Act
        using var keyManager = SecureKeyManager.FromKeyFileContent(LegacyKeyFileFixture, BitcoinNetwork.Regtest,
                                                                   Password, persisted.Add, 10);
        keyManager.GetNextChannelKey(out var index);

        // Assert
        Assert.Equal(11U, index);
        Assert.Equal([11U], persisted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void Given_AKeyIndexFile_When_Persisted_Then_ItHoldsOnlyPublicDataAndRefusesAnotherKey()
    {
        // Arrange
        var path = KeyIndexFile.GetPath(_directory);
        var key = new CompactPubKey(Convert.FromHexString(
                                        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
        var other = new CompactPubKey(Convert.FromHexString(
                                          "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5"));
        var file = KeyIndexFile.Open(path, "regtest");
        Assert.True(file.BelongsTo(key));
        Assert.False(File.Exists(path));

        // Act
        file.Persist(key, 3);
        file.Persist(key, 2);
        var reopened = KeyIndexFile.Open(path, "regtest");

        // Assert
        Assert.Equal(3U, reopened.LastUsedIndex);
        Assert.True(reopened.BelongsTo(key));
        Assert.False(reopened.BelongsTo(other));
        Assert.Throws<InvalidOperationException>(() => reopened.Persist(other, 4));
        Assert.Equal("nltg-key-index 1 regtest 0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798 3",
                     File.ReadAllText(path, Encoding.UTF8).Trim());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Throws<InvalidDataException>(() => KeyIndexFile.Open(path, "signet"));
        File.WriteAllText(path, "garbage");
        Assert.Throws<InvalidDataException>(() => KeyIndexFile.Open(path, "regtest"));
    }
}