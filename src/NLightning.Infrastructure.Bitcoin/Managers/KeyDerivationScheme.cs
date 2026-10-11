namespace NLightning.Infrastructure.Bitcoin.Managers;

/// <summary>
/// How <see cref="SecureKeyManager"/> derives the master key and the node key (NL-159).
/// </summary>
public enum KeyDerivationScheme
{
    /// <summary>
    /// Key files of version 1 and 2: the node key is the master private key, whose BIP32 chain code is the network's
    /// genesis hash. Kept for every existing key file, because changing it would change the node id.
    /// </summary>
    LegacyGenesisChainCode,

    /// <summary>
    /// Key files of version 3 (new nodes): a standard BIP32 master key (from a random seed or a BIP39 mnemonic) and the
    /// node key at <see cref="SecureKeyManager.NodeKeyPathString"/>, so the node key is not the wallet's root key.
    /// </summary>
    Bip32
}