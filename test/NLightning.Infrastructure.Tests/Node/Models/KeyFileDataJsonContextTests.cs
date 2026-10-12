using System.Text.Json;

namespace NLightning.Infrastructure.Tests.Node.Models;

using Infrastructure.Node.Models;

/// <summary>
/// The source-generated key file contract (NL-338) must read and write exactly what the reflection serializer did, or
/// existing key files would change format.
/// </summary>
public class KeyFileDataJsonContextTests
{
    public static TheoryData<KeyFileData> KeyFiles => new()
    {
        // Version 1: no version, salt, nonce, Argon2 parameters or node key path
        new KeyFileData
        {
            Network = "RegTest", Descriptor = "legacy", LastUsedIndex = 7, EncryptedExtKey = "AAEC", HeightOfBirth = 123
        },
        // Version 3: every member set
        new KeyFileData
        {
            Version = KeyFileData.Bip32Version, Network = "Main", Descriptor = "wpkh", LastUsedIndex = 42,
            EncryptedExtKey = "c2VjcmV0", HeightOfBirth = 900_000, Salt = "c2FsdA==", Nonce = "bm9uY2U=",
            Argon2MemLimit = 268_435_456, Argon2OpsLimit = 3, NodeKeyPath = "m/1017'/0'/6'/0/0"
        }
    };

    [Theory]
    [MemberData(nameof(KeyFiles))]
    public void Given_AKeyFile_When_SerializedThroughTheContext_Then_TheJsonEqualsTheReflectionSerializers(
        KeyFileData data)
    {
        // Arrange
#pragma warning disable IL2026, IL3050 // the reflection serializer is the reference here
        var expected = JsonSerializer.Serialize(data);
#pragma warning restore IL2026, IL3050

        // Act
        var actual = JsonSerializer.Serialize(data, KeyFileDataJsonContext.Default.KeyFileData);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Given_AVersion1File_When_ReadThroughTheContext_Then_EveryMemberIsRead()
    {
        // Arrange
        const string json = "{\"network\":\"RegTest\",\"descriptor\":\"legacy\",\"lastUsedIndex\":7,"
                          + "\"encryptedExtKey\":\"AAEC\",\"heightOfBirth\":123}";

        // Act
        var data = JsonSerializer.Deserialize(json, KeyFileDataJsonContext.Default.KeyFileData);

        // Assert
        Assert.NotNull(data);
        Assert.Equal(0, data.Version);
        Assert.Equal("RegTest", data.Network);
        Assert.Equal("legacy", data.Descriptor);
        Assert.Equal(7u, data.LastUsedIndex);
        Assert.Equal("AAEC", data.EncryptedExtKey);
        Assert.Equal(123u, data.HeightOfBirth);
        Assert.Null(data.Salt);
        Assert.Null(data.Nonce);
        Assert.Null(data.NodeKeyPath);
    }
}