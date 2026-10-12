using System.Text.Json.Serialization;

namespace NLightning.Infrastructure.Node.Models;

/// <summary>
/// The node key file (JSON).
/// </summary>
/// <remarks>
/// Version 1 files have no <see cref="Version"/> (read as 0 or 1), and are encrypted with a fixed salt, an all-zero
/// nonce and a 64 KiB Argon2id memory limit. Version 2 files store a random salt, a random nonce and the Argon2id
/// parameters used. Versions 1 and 2 hold a master key whose chain code is the network's genesis hash, and the node
/// key is the master key itself. Version 3 files (new nodes, NL-159) are encrypted like version 2 but hold a standard
/// BIP32 master key, and the node key is derived at <see cref="NodeKeyPath"/>. The version decides the node id, so a
/// file never changes between the two derivations; version 1 files are upgraded to version 2.
/// </remarks>
public class KeyFileData
{
    public const int LegacyVersion = 1;
    public const int GenesisChainCodeVersion = 2;
    public const int Bip32Version = 3;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Version { get; set; }

    [JsonPropertyName("network")] public string Network { get; set; } = string.Empty;

    [JsonPropertyName("descriptor")] public string Descriptor { get; set; } = string.Empty;

    [JsonPropertyName("lastUsedIndex")] public uint LastUsedIndex { get; set; }

    [JsonPropertyName("encryptedExtKey")] public string EncryptedExtKey { get; set; } = string.Empty;

    [JsonPropertyName("heightOfBirth")] public uint HeightOfBirth { get; set; }

    /// <summary>
    /// Base64 Argon2id salt (version 2 and later).
    /// </summary>
    [JsonPropertyName("salt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Salt { get; set; }

    /// <summary>
    /// Base64 XChaCha20-Poly1305 nonce (version 2 and later).
    /// </summary>
    [JsonPropertyName("nonce")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nonce { get; set; }

    /// <summary>
    /// Argon2id memory limit in bytes (version 2 and later).
    /// </summary>
    [JsonPropertyName("argon2MemLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong Argon2MemLimit { get; set; }

    /// <summary>
    /// Argon2id number of passes (version 2 and later).
    /// </summary>
    [JsonPropertyName("argon2OpsLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong Argon2OpsLimit { get; set; }

    /// <summary>
    /// BIP32 path of the node key (version 3 only, informational and checked on load).
    /// </summary>
    [JsonPropertyName("nodeKeyPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeKeyPath { get; set; }
}