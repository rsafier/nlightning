using System.Globalization;

namespace NLightning.Infrastructure.Bitcoin.Managers;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The last used channel key index of a key that has no key file on disk (locked start, NL-1349): one line of public
/// data, <c>nltg-key-index 1 &lt;network&gt; &lt;node public key&gt; &lt;index&gt;</c>, bound to the node public key.
/// It never holds key material. It is written only after the key was accepted (the first channel key reservation), with
/// the same atomic, owner-only replace as the key file, so a power loss can never bring back a lower index
/// (SECURITY_REVIEW SR-19).
/// </summary>
public sealed class KeyIndexFile
{
    private const string Magic = "nltg-key-index";
    private const int FormatVersion = 1;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _network;

    /// <summary>The node public key the file is bound to (hex), or null while it does not exist.</summary>
    public string? NodePublicKey { get; private set; }

    /// <summary>The stored last used channel key index (0 while the file does not exist).</summary>
    public uint LastUsedIndex { get; private set; }

    private KeyIndexFile(string path, string network)
    {
        _path = path;
        _network = network;
    }

    /// <summary>Gets the index file's path in a configuration directory.</summary>
    public static string GetPath(string configPath) => Path.Combine(configPath, "nltg.key-index");

    /// <summary>Reads the file when it exists. Nothing is written.</summary>
    /// <exception cref="InvalidDataException">The file is malformed or for another network.</exception>
    public static KeyIndexFile Open(string path, string network)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);

        var file = new KeyIndexFile(path, network);
        if (!File.Exists(path))
            return file;

        var parts = File.ReadAllText(path).Trim().Split(' ');
        if (parts.Length != 5 || parts[0] != Magic || parts[1] != FormatVersion.ToString(CultureInfo.InvariantCulture)
         || parts[3].Length != 66 || !parts[3].All(Uri.IsHexDigit)
         || !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            throw new InvalidDataException($"The key index file {path} is malformed.");
        if (!string.Equals(parts[2], network, StringComparison.Ordinal))
            throw new InvalidDataException($"The key index file {path} is for another network.");

        file.NodePublicKey = parts[3].ToLowerInvariant();
        file.LastUsedIndex = index;
        return file;
    }

    /// <summary>
    /// Checks that the file, when it exists, belongs to <paramref name="nodePublicKey"/>. Nothing is written.
    /// </summary>
    public bool BelongsTo(CompactPubKey nodePublicKey) =>
        NodePublicKey is null || string.Equals(NodePublicKey, ToHex(nodePublicKey), StringComparison.Ordinal);

    /// <summary>
    /// Durably stores <paramref name="index"/> for <paramref name="nodePublicKey"/> when it raises the stored index.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file belongs to another node public key.</exception>
    public void Persist(CompactPubKey nodePublicKey, uint index)
    {
        lock (_lock)
        {
            if (!BelongsTo(nodePublicKey))
                throw new InvalidOperationException("The key index file belongs to another node key.");
            if (NodePublicKey is not null && index <= LastUsedIndex)
                return;

            var hex = ToHex(nodePublicKey);
            SecureKeyManager.WriteFileAtomically(
                _path, string.Create(CultureInfo.InvariantCulture,
                                     $"{Magic} {FormatVersion} {_network} {hex} {index}\n"));
            NodePublicKey = hex;
            LastUsedIndex = index;
        }
    }

    private static string ToHex(CompactPubKey key) => Convert.ToHexString((byte[])key).ToLowerInvariant();
}