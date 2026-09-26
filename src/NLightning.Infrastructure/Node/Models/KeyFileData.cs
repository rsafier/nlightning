using System.Text.Json.Serialization;

namespace NLightning.Infrastructure.Node.Models;

/// <summary>
/// The node key file (JSON).
/// </summary>
/// <remarks>
/// Version 1 files have no <see cref="Version"/> (read as 0 or 1), and are encrypted with a fixed salt, an all-zero
/// nonce and a 64 KiB Argon2id memory limit. Version 2 files store a random salt, a random nonce and the Argon2id
/// parameters used.
/// </remarks>
public class KeyFileData
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;

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
}