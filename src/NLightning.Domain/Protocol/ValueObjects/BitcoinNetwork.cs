using System.Collections.Concurrent;

namespace NLightning.Domain.Protocol.ValueObjects;

using Constants;

/// <summary>
/// A Bitcoin network by name (<c>mainnet</c>, <c>testnet</c>, <c>regtest</c>, <c>signet</c> or a registered custom
/// network). Names are case-insensitive and stored in lower case.
/// </summary>
/// <remarks>
/// Custom signets (Mutinynet and any registered with <see cref="RegisterCustomSignet"/>) share the signet genesis block,
/// so for Lightning they are <see cref="Signet"/>: same <c>chain_hash</c>, <c>tb</c> addresses and <c>tbs</c> invoices.
/// <see cref="Resolve"/> maps their names to <see cref="Signet"/>; use it for any network name read from configuration.
/// </remarks>
public readonly struct BitcoinNetwork : IEquatable<BitcoinNetwork>
{
    private static readonly ConcurrentDictionary<string, ChainHash> s_customChainHashes = new();
    private static readonly ConcurrentDictionary<string, byte> s_customSignets = new();

    public static readonly BitcoinNetwork Mainnet = new(NetworkConstants.Mainnet);
    public static readonly BitcoinNetwork Testnet = new(NetworkConstants.Testnet);
    public static readonly BitcoinNetwork Regtest = new(NetworkConstants.Regtest);
    public static readonly BitcoinNetwork Signet = new(NetworkConstants.Signet);

    static BitcoinNetwork()
    {
        RegisterCustomSignet(NetworkConstants.Mutinynet);
    }

    private readonly string? _name;

    public string Name => _name ?? string.Empty;

    /// <summary>
    /// A network by name, lower-cased. A registered custom signet name gives <see cref="Signet"/> (see the remarks of
    /// the type); an unknown name is kept, and <see cref="ChainHash"/> then throws. Prefer <see cref="Resolve"/> for
    /// names from configuration, which fails at once.
    /// </summary>
    public BitcoinNetwork(string name)
    {
        var normalized = Normalize(name);
        _name = normalized.Length > 0 && s_customSignets.ContainsKey(normalized)
                    ? NetworkConstants.Signet
                    : normalized;
    }

    /// <summary>
    /// The BOLT <c>chain_hash</c> of this network (genesis block hash, wire order).
    /// </summary>
    /// <exception cref="InvalidOperationException">The network is neither built in nor registered.</exception>
    public ChainHash ChainHash
    {
        get
        {
            return Name switch
            {
                NetworkConstants.Mainnet => ChainConstants.Main,
                NetworkConstants.Testnet => ChainConstants.Testnet,
                NetworkConstants.Regtest => ChainConstants.Regtest,
                NetworkConstants.Signet => ChainConstants.Signet,
                _ => s_customChainHashes.TryGetValue(Name, out var hash)
                         ? hash
                         : throw new InvalidOperationException($"Chain not supported: {Name}")
            };
        }
    }

    /// <summary>
    /// True for <see cref="Signet"/> and for any registered custom signet name.
    /// </summary>
    public bool IsSignet => Name == NetworkConstants.Signet || IsCustomSignet(Name);

    public override string ToString() => Name;

    /// <summary>
    /// True when <paramref name="name"/> is one of the four built-in networks.
    /// </summary>
    public static bool IsBuiltIn(string? name)
    {
        return Normalize(name) is NetworkConstants.Mainnet or NetworkConstants.Testnet or NetworkConstants.Regtest
                                  or NetworkConstants.Signet;
    }

    /// <summary>
    /// True when <paramref name="name"/> was registered with <see cref="RegisterCustomSignet"/> (Mutinynet is
    /// registered by default).
    /// </summary>
    public static bool IsCustomSignet(string? name)
    {
        var normalized = Normalize(name);
        return normalized.Length > 0 && s_customSignets.ContainsKey(normalized);
    }

    /// <summary>
    /// Resolves a configured network name. Built-in names give that network, custom signet names give
    /// <see cref="Signet"/> (their Lightning parameters are signet's), other registered custom networks give themselves.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or unknown: never fall back to another network.</exception>
    public static BitcoinNetwork Resolve(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
            throw new ArgumentException("The Bitcoin network name is empty.", nameof(name));

        if (IsBuiltIn(normalized))
            return new BitcoinNetwork(normalized);

        if (s_customSignets.ContainsKey(normalized))
            return Signet;

        if (s_customChainHashes.ContainsKey(normalized))
            return new BitcoinNetwork(normalized);

        throw new ArgumentException(
            $"Unknown Bitcoin network '{name}'. Use {NetworkConstants.Mainnet}, {NetworkConstants.Testnet}, "
          + $"{NetworkConstants.Regtest}, {NetworkConstants.Signet} or a registered custom signet "
          + $"({string.Join(", ", s_customSignets.Keys.Order())}).", nameof(name));
    }

    /// <summary>
    /// Register a custom network mapping.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null or white space.</exception>
    /// <exception cref="InvalidOperationException">The name is built in or already registered.</exception>
    public static void Register(string name, ChainHash chainHash)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

        var normalized = Normalize(name);
        if (IsBuiltIn(normalized))
            throw new InvalidOperationException($"Chain hash already registered: {name} is a built-in network");

        if (!s_customChainHashes.TryAdd(normalized, chainHash))
            throw new InvalidOperationException($"Chain hash already registered: {name}");
    }

    /// <summary>
    /// Register a custom signet (for example a node's <c>Node:CustomSignet:Name</c>), so that <see cref="Resolve"/>
    /// maps it to <see cref="Signet"/>. Registering the same custom signet again does nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null or white space.</exception>
    /// <exception cref="InvalidOperationException">The name is built in or registered with another chain hash.</exception>
    public static void RegisterCustomSignet(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

        var normalized = Normalize(name);
        if (IsBuiltIn(normalized))
            throw new InvalidOperationException($"{name} is a built-in network, not a custom signet");

        var hash = s_customChainHashes.GetOrAdd(normalized, ChainConstants.Signet);
        if (hash != ChainConstants.Signet)
            throw new InvalidOperationException($"Chain hash already registered: {name} is not a signet");

        s_customSignets.TryAdd(normalized, 0);
    }

    /// <summary>
    /// Unregister a custom network mapping.
    /// </summary>
    public static void Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

        var normalized = Normalize(name);
        s_customChainHashes.TryRemove(normalized, out _);
        s_customSignets.TryRemove(normalized, out _);
    }

    private static string Normalize(string? name) => name?.Trim().ToLowerInvariant() ?? string.Empty;

    #region Implicit Conversions

    public static implicit operator string(BitcoinNetwork bitcoinNetwork) => bitcoinNetwork.Name;
    public static implicit operator BitcoinNetwork(string value) => new(value);

    #endregion

    #region Equality

    public override bool Equals(object? obj)
    {
        return obj is BitcoinNetwork network && Equals(network);
    }

    public bool Equals(BitcoinNetwork other)
    {
        return Name == other.Name;
    }

    public override int GetHashCode()
    {
        return Name.GetHashCode();
    }

    public static bool operator ==(BitcoinNetwork left, BitcoinNetwork right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(BitcoinNetwork left, BitcoinNetwork right)
    {
        return !(left == right);
    }

    #endregion
}