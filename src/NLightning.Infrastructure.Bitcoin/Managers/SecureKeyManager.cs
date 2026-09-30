using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Managers;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Crypto.Ciphers;
using Infrastructure.Crypto.Factories;
using Infrastructure.Crypto.Hashes;
using Node.Models;
using Onion;

/// <summary>
/// Manages a securely stored private key using protected memory allocation.
/// This class ensures that the private key remains inaccessible from regular memory
/// and is securely wiped when no longer needed.
/// </summary>
public class SecureKeyManager : ISecureKeyManager, IDisposable
{
    /// <summary>
    /// BIP32 path of the node key for <see cref="KeyDerivationScheme.Bip32"/> (version 3 key files, NL-159). It follows
    /// the layout of LND's node key (BIP43 purpose 1017, key family 6); the seed is not an LND seed, so the two are not
    /// interchangeable.
    /// </summary>
    public const string NodeKeyPathString = "m/1017'/0'/6'/0/0";

    /// <summary>
    /// Fixed salt used by version 1 key files. Only used to read them; new files get a random per-file salt.
    /// </summary>
    private static readonly byte[] s_legacySalt =
    [
        0xFF, 0x1D, 0x3B, 0xF5, 0x24, 0xA2, 0xB7, 0xA9,
        0xC3, 0x1B, 0x1F, 0x58, 0xE9, 0x48, 0xB5, 0x69
    ];

    /// <summary>
    /// Argon2id passes used by version 1 key files.
    /// </summary>
    private const ulong LegacyArgon2OpsLimit = 3;

    private const int ChainCodeLength = 32;

    /// <summary>
    /// Group and other permission bits: a key file or its backup never keeps them (SECURITY_REVIEW SR-02).
    /// </summary>
    private const UnixFileMode GroupOrOtherMode = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                                         | UnixFileMode.GroupExecute
                                                                         | UnixFileMode.OtherRead
                                                                         | UnixFileMode.OtherWrite
                                                                         | UnixFileMode.OtherExecute;

    private readonly string _filePath;
    private readonly object _lastUsedIndexLock = new();
    private readonly Network _network;
    private readonly KeyPath _channelKeyPath = new(KeyConstants.ChannelKeyPathString);
    private readonly KeyPath _depositP2TrKeyPath = new(KeyConstants.P2TrKeyPathString);
    private readonly KeyPath _depositP2WpkhKeyPath = new(KeyConstants.P2WpkhKeyPathString);
    private readonly byte[] _nodePubKey = [];

    private uint _lastUsedIndex;

    // The master private key, followed by its chain code for the BIP32 scheme
    private ulong _masterKeyLength;
    private IntPtr _secureMasterKeyPtr;

    // The node private key (for the legacy scheme a copy of the master private key)
    private ulong _nodeKeyLength;
    private IntPtr _secureNodeKeyPtr;

    public BitcoinKeyPath ChannelKeyPath => _channelKeyPath.ToBytes();
    public BitcoinKeyPath DepositP2TrKeyPath => _depositP2TrKeyPath.ToBytes();
    public BitcoinKeyPath DepositP2WpkhKeyPath => _depositP2WpkhKeyPath.ToBytes();

    /// <summary>
    /// How the master and node keys are derived. Chosen when the key is created and recorded by the key file version;
    /// it never changes for an existing key file, because it decides the node id.
    /// </summary>
    public KeyDerivationScheme DerivationScheme { get; }

    public string OutputChannelDescriptor { get; init; }
    public string OutputDepositP2TrDescriptor { get; init; }

    public string OutputDepositP2WshDescriptor { get; init; }
    public string OutputChangeP2TrDescriptor { get; init; }

    public string OutputChangeP2WshDescriptor { get; init; }

    public uint HeightOfBirth { get; init; }

    /// <summary>
    /// Manages a key with the legacy derivation (<see cref="KeyDerivationScheme.LegacyGenesisChainCode"/>): the node
    /// key is <paramref name="privateKey"/> itself, which is also the BIP32 master key with the network's genesis hash
    /// as its chain code. New nodes use <see cref="CreateNew"/>.
    /// </summary>
    /// <param name="privateKey">The private key to be managed. The array is zeroed.</param>
    /// <param name="network">The network associated with the private key.</param>
    /// <param name="filePath">The file path for storing the key data.</param>
    /// <param name="heightOfBirth">Block Height when the wallet was created</param>
    public SecureKeyManager(byte[] privateKey, BitcoinNetwork network, string filePath, uint heightOfBirth)
        : this(privateKey, null, network, filePath, heightOfBirth)
    {
    }

    /// <param name="privateKey">The master private key. The array is zeroed.</param>
    /// <param name="chainCode">The master chain code (<see cref="KeyDerivationScheme.Bip32"/>), or null for the legacy
    /// scheme. The array is zeroed.</param>
    /// <param name="network">The network associated with the key.</param>
    /// <param name="filePath">The file path for storing the key data.</param>
    /// <param name="heightOfBirth">Block Height when the wallet was created</param>
    private SecureKeyManager(byte[] privateKey, byte[]? chainCode, BitcoinNetwork network, string filePath,
                             uint heightOfBirth)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        byte[]? masterMaterial = null;
        byte[]? nodeKey = null;
        try
        {
            if (privateKey.Length != CryptoConstants.PrivkeyLen)
                throw new ArgumentException($"The private key must be {CryptoConstants.PrivkeyLen} bytes.",
                                            nameof(privateKey));
            if (chainCode is not null && chainCode.Length != ChainCodeLength)
                throw new ArgumentException($"The chain code must be {ChainCodeLength} bytes.", nameof(chainCode));

            _network = Network.GetNetwork(network)
                    ?? throw new ArgumentException("Invalid network specified.", nameof(network));
            DerivationScheme = chainCode is null
                                   ? KeyDerivationScheme.LegacyGenesisChainCode
                                   : KeyDerivationScheme.Bip32;

            masterMaterial = new byte[privateKey.Length + (chainCode?.Length ?? 0)];
            privateKey.CopyTo(masterMaterial, 0);
            chainCode?.CopyTo(masterMaterial, privateKey.Length);
            _secureMasterKeyPtr = AllocateSecure(masterMaterial, out _masterKeyLength);

            // Output descriptors: each names the xpub at its own key origin (SR-13; they used to put the master xpub
            // behind the account's origin, which describes other keys than ours)
            var extKey = GetMasterKey();
            var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
            var channelXpub = extKey.Derive(_channelKeyPath).Neuter().ToString(_network);
            var p2TrXpub = extKey.Derive(_depositP2TrKeyPath).Neuter().ToString(_network);
            var p2WpkhXpub = extKey.Derive(_depositP2WpkhKeyPath).Neuter().ToString(_network);

            OutputChannelDescriptor = $"wpkh([{fingerprint}/{_channelKeyPath}]{channelXpub}/*)";
            OutputDepositP2TrDescriptor = $"tr([{fingerprint}/{_depositP2TrKeyPath}]{p2TrXpub}/0/*)";
            OutputChangeP2TrDescriptor = $"tr([{fingerprint}/{_depositP2TrKeyPath}]{p2TrXpub}/1/*)";
            OutputDepositP2WshDescriptor = $"wpkh([{fingerprint}/{_depositP2WpkhKeyPath}]{p2WpkhXpub}/0/*)";
            OutputChangeP2WshDescriptor = $"wpkh([{fingerprint}/{_depositP2WpkhKeyPath}]{p2WpkhXpub}/1/*)";

            var nodePrivateKey = DerivationScheme == KeyDerivationScheme.Bip32
                                     ? extKey.Derive(new KeyPath(NodeKeyPathString)).PrivateKey
                                     : extKey.PrivateKey;
            nodeKey = nodePrivateKey.ToBytes();
            _nodePubKey = nodePrivateKey.PubKey.ToBytes();
            _secureNodeKeyPtr = AllocateSecure(nodeKey, out _nodeKeyLength);
        }
        catch
        {
            ReleaseUnmanagedResources();
            throw;
        }
        finally
        {
            // Wipe the plain copies. These arrays are not pinned, so they are zeroed as arrays: zeroing through an
            // address taken earlier (the old UnsafeAddrOfPinnedArrayElement) can hit wherever the GC moved them to.
            CryptographicOperations.ZeroMemory(privateKey);
            if (chainCode is not null)
                CryptographicOperations.ZeroMemory(chainCode);
            if (masterMaterial is not null)
                CryptographicOperations.ZeroMemory(masterMaterial);
            if (nodeKey is not null)
                CryptographicOperations.ZeroMemory(nodeKey);
        }

        _filePath = filePath;
        HeightOfBirth = heightOfBirth;
    }

    /// <summary>
    /// Creates the key of a new node: a standard BIP32 master key from a random 32-byte seed
    /// (<see cref="KeyDerivationScheme.Bip32"/>), with the node key at <see cref="NodeKeyPathString"/>. Its key file is
    /// version <see cref="KeyFileData.Bip32Version"/>.
    /// </summary>
    public static SecureKeyManager CreateNew(BitcoinNetwork network, string filePath, uint heightOfBirth)
    {
        var seed = RandomNumberGenerator.GetBytes(CryptoConstants.PrivkeyLen);
        try
        {
            return FromBip32MasterKey(ExtKey.CreateFromSeed(seed), network, filePath, heightOfBirth);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public ExtPrivKey GetNextChannelKey(out uint index)
    {
        lock (_lastUsedIndexLock)
        {
            _lastUsedIndex++;
            index = _lastUsedIndex;

            // Persist the index before the key is handed out, under the lock: the old fire-and-forget write could be
            // lost in a crash or land after a later one, and after a restart a new channel would get the index (and
            // so the keys) of an existing channel. A failed write throws, so the key is never used.
            PersistLastUsedIndex();
        }

        // Derive the key at m/6425'/0'/0'/0/index
        var masterKey = GetMasterKey();
        var derivedKey = masterKey.Derive(_channelKeyPath.Derive(index));

        return derivedKey.ToBytes();
    }

    public ExtPrivKey GetChannelKeyAtIndex(uint index)
    {
        var masterKey = GetMasterKey();
        return masterKey.Derive(_channelKeyPath.Derive(index)).ToBytes();
    }

    public ExtPrivKey GetDepositP2TrKeyAtIndex(uint index, bool isChange)
    {
        var masterKey = GetMasterKey();
        return masterKey.Derive(_depositP2TrKeyPath.Derive(isChange ? "1" : "0")).Derive(index).ToBytes();
    }

    public ExtPrivKey GetDepositP2WpkhKeyAtIndex(uint index, bool isChange)
    {
        var masterKey = GetMasterKey();
        return masterKey.Derive(_depositP2WpkhKeyPath.Derive(isChange ? "1" : "0")).Derive(index).ToBytes();
    }

    public CryptoKeyPair GetNodeKeyPair()
    {
        return new CryptoKeyPair(CopyFromSecure(_secureNodeKeyPtr, _nodeKeyLength), _nodePubKey.ToArray());
    }

    public CompactPubKey GetNodePubKey()
    {
        return _nodePubKey.ToArray();
    }

    /// <inheritdoc/>
    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        // Copy the node key out of locked memory only for the ECDH, then wipe it.
        // Hot path (every peeled HTLC): parse straight into an ECPrivKey and hash with the one-shot BCL SHA-256
        // instead of allocating a native hash state and NBitcoin Key/PubKey wrappers per call.
        var privateKey = CopyFromSecure(_secureNodeKeyPtr, _nodeKeyLength);
        try
        {
            using var ecPrivKey = SphinxKeyGenerator.CreatePrivateKey(privateKey, nameof(privateKey));
            SphinxKeyGenerator.ComputeEcdhSharedSecret(ecPrivKey, publicKey, sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>
    /// Raises the last used channel key index to at least <paramref name="highestUsedIndex"/> (the highest channel key
    /// index stored anywhere else, e.g. in the channel tables) and writes it to the key file when it changed. Defence in
    /// depth against a key-file write lost after its channel row was committed: the next
    /// <see cref="GetNextChannelKey"/> can then never hand out an index a channel already uses (SECURITY_REVIEW SR-19).
    /// Never lowers the index. Returns true when the index was raised.
    /// </summary>
    public bool EnsureLastUsedChannelIndexAtLeast(uint highestUsedIndex)
    {
        lock (_lastUsedIndexLock)
        {
            if (highestUsedIndex <= _lastUsedIndex)
                return false;

            _lastUsedIndex = highestUsedIndex;
            PersistLastUsedIndex();
            return true;
        }
    }

    /// <summary>
    /// Writes the last used channel key index to the key file (<see cref="GetNextChannelKey"/> already does it before
    /// it returns).
    /// </summary>
    public Task UpdateLastUsedChannelIndexOnFile()
    {
        lock (_lastUsedIndexLock)
        {
            PersistLastUsedIndex();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Encrypts the master key with <paramref name="password"/> and writes the key file with a fresh random salt and
    /// nonce: version <see cref="KeyFileData.GenesisChainCodeVersion"/> for the legacy scheme,
    /// <see cref="KeyFileData.Bip32Version"/> for the BIP32 scheme.
    /// </summary>
    public void SaveToFile(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        lock (_lastUsedIndexLock)
        {
            var extKeyBytes = Encoding.UTF8.GetBytes(GetMasterKey().ToString(_network));
            var salt = new byte[Argon2Id.SaltLen];
            var nonce = new byte[CryptoConstants.Xchacha20Poly1305NonceLen];
            var cipherText = new byte[extKeyBytes.Length + CryptoConstants.Xchacha20Poly1305TagLen];
            Span<byte> key = stackalloc byte[CryptoConstants.PrivkeyLen];

            try
            {
                using (var cryptoProvider = CryptoFactory.GetCryptoProvider())
                {
                    cryptoProvider.RandomBytes(salt);
                    cryptoProvider.RandomBytes(nonce);
                }

                using (var argon2Id = new Argon2Id())
                {
                    argon2Id.DeriveKeyFromPasswordAndSalt(password, salt, key, Argon2Id.DefaultOpsLimit,
                                                          Argon2Id.DefaultMemLimit);
                }

                using (var xChaCha20Poly1305 = new XChaCha20Poly1305())
                {
                    xChaCha20Poly1305.Encrypt(key, nonce, ReadOnlySpan<byte>.Empty, extKeyBytes, cipherText);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(extKeyBytes);
            }

            var isBip32 = DerivationScheme == KeyDerivationScheme.Bip32;
            var data = new KeyFileData
            {
                Version = isBip32 ? KeyFileData.Bip32Version : KeyFileData.GenesisChainCodeVersion,
                Network = _network.ToString(),
                LastUsedIndex = _lastUsedIndex,
                Descriptor = OutputChannelDescriptor,
                EncryptedExtKey = Convert.ToBase64String(cipherText),
                HeightOfBirth = HeightOfBirth,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Argon2MemLimit = Argon2Id.DefaultMemLimit,
                Argon2OpsLimit = Argon2Id.DefaultOpsLimit,
                NodeKeyPath = isBip32 ? NodeKeyPathString : null
            };
            var json = JsonSerializer.Serialize(data);
            WriteFileAtomically(_filePath, json);
        }
    }

    /// <summary>
    /// Restores a key from a BIP39 mnemonic as the standard BIP32 master key (<see cref="KeyDerivationScheme.Bip32"/>,
    /// node key at <see cref="NodeKeyPathString"/>). Before NL-159 this dropped the mnemonic's chain code; the daemon
    /// never called it.
    /// </summary>
    public static SecureKeyManager FromMnemonic(string mnemonic, string passphrase, BitcoinNetwork network,
                                                string? filePath = null, uint currentHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            filePath = GetKeyFilePath(network);

        var mnemonicObj = new Mnemonic(mnemonic, Wordlist.English);
        return FromBip32MasterKey(mnemonicObj.DeriveExtKey(passphrase), network, filePath, currentHeight);
    }

    public static SecureKeyManager FromFilePath(string filePath, BitcoinNetwork expectedNetwork, string password)
    {
        return FromFilePath(filePath, expectedNetwork, password,
                            OperatingSystem.IsWindows() ? GetSystemAnsiPasswordBytes : null);
    }

    /// <summary>
    /// Loads a key file. The file's version decides the derivation, and so the node id: a version 1 or 2 file keeps
    /// the legacy scheme when it is upgraded, a version 3 file uses BIP32.
    /// </summary>
    /// <param name="filePath">The key file.</param>
    /// <param name="expectedNetwork">The network the file must be for.</param>
    /// <param name="password">The key file password.</param>
    /// <param name="legacyAnsiPasswordEncoder">For version 1 files: how the old libsodium P/Invoke marshalled the
    /// password as an ANSI string (the system code page on Windows, NL-212); null skips that retry.</param>
    internal static SecureKeyManager FromFilePath(string filePath, BitcoinNetwork expectedNetwork, string password,
                                                  Func<string, byte[]?>? legacyAnsiPasswordEncoder)
    {
        var jsonString = File.ReadAllText(filePath);
        var data = JsonSerializer.Deserialize<KeyFileData>(jsonString)
                ?? throw new SerializationException("Invalid key file");

        var network = Network.GetNetwork(expectedNetwork)
                   ?? throw new ArgumentException("Invalid network specified.", nameof(expectedNetwork));

        // The file stores NBitcoin's name of the network (SaveToFile writes Network.ToString()): "RegTest",
        // "TestNet", "signet", but "Main" for mainnet, which is not "mainnet" in lower case (NL-403)
        if (expectedNetwork != data.Network.ToLowerInvariant() && Network.GetNetwork(data.Network) != network)
            throw new Exception($"Invalid network. Expected {expectedNetwork}, but got {data.Network}");

        if (data.Version == KeyFileData.Bip32Version && data.NodeKeyPath != NodeKeyPathString)
            throw new SerializationException($"Invalid key file: unsupported node key path '{data.NodeKeyPath}'");

        var extKeyBytes = DecryptExtKey(data, password, legacyAnsiPasswordEncoder, out var usedLegacyPasswordEncoding);
        ExtKey extKey;
        try
        {
            extKey = ExtKey.Parse(Encoding.UTF8.GetString(extKeyBytes), network);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(extKeyBytes);
        }

        // Versions 1 and 2 keep the legacy derivation (the stored chain code is the genesis hash and is ignored), so
        // the node id of an existing key file never changes
        var chainCode = data.Version == KeyFileData.Bip32Version ? extKey.ChainCode.ToArray() : null;
        var keyManager =
            new SecureKeyManager(extKey.PrivateKey.ToBytes(), chainCode, expectedNetwork, filePath, data.HeightOfBirth)
            {
                // The descriptor is recomputed from the key, not read back: files written before SR-13 hold a wrong one
                _lastUsedIndex = data.LastUsedIndex
            };

        if (data.Version < KeyFileData.GenesisChainCodeVersion || usedLegacyPasswordEncoding)
        {
            // Migrate legacy key files (fixed salt, zero nonce, weak Argon2id parameters, or a password hashed with
            // the truncated libsodium encoding) to version 2, keeping their derivation. Older binaries cannot read the
            // new format, so keep a copy of the original file first.
            try
            {
                var backupPath = BackupKeyFile(filePath, data.Version);
                Console.Error.WriteLine($"Upgrading key file {filePath} to version " +
                                        $"{KeyFileData.GenesisChainCodeVersion}. The original file was saved to " +
                                        $"{backupPath}; builds older than this one cannot read the upgraded file.");
                keyManager.SaveToFile(password);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to upgrade key file {filePath} to version " +
                                        $"{KeyFileData.GenesisChainCodeVersion}: {e.Message}");
            }
        }

        if (GetWeakBackupWarning(filePath) is { } warning)
            Console.Error.WriteLine(warning);

        return keyManager;
    }

    /// <summary>
    /// Gets the warning about the <c>.v1.bak</c> copy an upgrade left next to a key file, or null when there is
    /// none. The copy holds the same key under the version 1 encryption (fixed salt, 64 KiB Argon2id), so whoever
    /// gets the directory attacks it rather than the upgraded file (SR-18).
    /// </summary>
    internal static string? GetWeakBackupWarning(string filePath)
    {
        var backupPath = $"{filePath}.v{KeyFileData.LegacyVersion}.bak";
        if (!File.Exists(backupPath))
            return null;

        return $"{backupPath} holds this node's key under the weak version 1 encryption. Keep it only while you may " +
               "need to go back to a build older than the key file upgrade, then delete it (and any copy of it, such " +
               "as in backups).";
    }

    /// <summary>
    /// Gets the path for the Key file
    /// </summary>
    public static string GetKeyFilePath(string configPath)
    {
        return Path.Combine(configPath, "nltg.key.json");
    }

    /// <summary>
    /// The password as the old libsodium P/Invoke passed it on Windows: marshalled as <c>LPStr</c> (the system ANSI code
    /// page, best-fit mapping) and read for <c>password.Length</c> bytes (NL-212). Null when that encoding is shorter.
    /// </summary>
    internal static byte[]? GetSystemAnsiPasswordBytes(string password)
    {
        var ptr = Marshal.StringToHGlobalAnsi(password);
        var ansiLength = 0;
        try
        {
            while (Marshal.ReadByte(ptr, ansiLength) != 0)
                ansiLength++;

            if (ansiLength < password.Length)
                return null;

            var bytes = new byte[password.Length];
            Marshal.Copy(ptr, bytes, 0, password.Length);
            return bytes;
        }
        finally
        {
            // The buffer holds the password: wipe it before freeing it
            for (var i = 0; i < ansiLength; i++)
                Marshal.WriteByte(ptr, i, 0);
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static SecureKeyManager FromBip32MasterKey(ExtKey masterKey, BitcoinNetwork network, string filePath,
                                                       uint heightOfBirth)
    {
        if (masterKey.Depth != 0)
            throw new ArgumentException("Not a BIP32 master key.", nameof(masterKey));

        return new SecureKeyManager(masterKey.PrivateKey.ToBytes(), masterKey.ChainCode.ToArray(), network, filePath,
                                    heightOfBirth);
    }

    private static byte[] DecryptExtKey(KeyFileData data, string password,
                                        Func<string, byte[]?>? legacyAnsiPasswordEncoder,
                                        out bool usedLegacyPasswordEncoding)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt;
        byte[] nonce;
        ulong opsLimit;
        ulong memLimit;
        var isLegacyFile = false;
        switch (data.Version)
        {
            case 0 or KeyFileData.LegacyVersion:
                salt = s_legacySalt;
                nonce = new byte[CryptoConstants.Xchacha20Poly1305NonceLen];
                opsLimit = LegacyArgon2OpsLimit;
                memLimit = Argon2Id.LegacyMemLimit;
                isLegacyFile = true;
                break;
            case KeyFileData.GenesisChainCodeVersion or KeyFileData.Bip32Version:
                salt = DecodeBase64Field(data.Salt, "salt", Argon2Id.SaltLen);
                nonce = DecodeBase64Field(data.Nonce, "nonce", CryptoConstants.Xchacha20Poly1305NonceLen);
                opsLimit = data.Argon2OpsLimit;
                memLimit = data.Argon2MemLimit;
                if (memLimit < Argon2Id.DefaultMemLimit || memLimit > Argon2Id.MaxMemLimit
                 || opsLimit < 1 || opsLimit > Argon2Id.MaxOpsLimit)
                    throw new SerializationException("Invalid key file: unsupported Argon2id parameters");
                break;
            default:
                throw new SerializationException($"Unsupported key file version {data.Version}");
        }

        byte[] encryptedExtKey;
        try
        {
            encryptedExtKey = Convert.FromBase64String(data.EncryptedExtKey);
        }
        catch (FormatException e)
        {
            throw new SerializationException("Invalid key file: encryptedExtKey is not valid base64", e);
        }

        if (encryptedExtKey.Length <= CryptoConstants.Xchacha20Poly1305TagLen)
            throw new SerializationException("Invalid key file: encryptedExtKey is too short");

        var extKeyBytes = new byte[encryptedExtKey.Length - CryptoConstants.Xchacha20Poly1305TagLen];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[]? ansiPasswordBytes = null;
        try
        {
            usedLegacyPasswordEncoding = false;
            if (TryDecrypt(passwordBytes, salt, nonce, opsLimit, memLimit, encryptedExtKey, extKeyBytes))
                return extKeyBytes;

            // Only version 1 files were written with the old libsodium encodings: later versions always hash the full
            // UTF-8 password, so a wrong password costs them one Argon2id run, not three
            if (isLegacyFile)
            {
                // Before the fix for the libsodium password length, the libsodium backend hashed only the first
                // password.Length (UTF-16 char count) bytes of the UTF-8 password. For non-ASCII passwords, retry with
                // that truncated encoding so files written by those builds still open.
                if (passwordBytes.Length != password.Length
                 && TryDecrypt(passwordBytes.AsSpan(0, password.Length), salt, nonce, opsLimit, memLimit,
                               encryptedExtKey, extKeyBytes))
                {
                    usedLegacyPasswordEncoding = true;
                    return extKeyBytes;
                }

                // On Windows the P/Invoke marshalled the password as LPStr, in the ANSI code page (NL-212)
                // (skipped when it gives bytes already tried: an ASCII password, or a UTF-8 code page)
                ansiPasswordBytes = legacyAnsiPasswordEncoder?.Invoke(password);
                if (ansiPasswordBytes is not null
                 && !ansiPasswordBytes.AsSpan().SequenceEqual(passwordBytes)
                 && !ansiPasswordBytes.AsSpan().SequenceEqual(passwordBytes.AsSpan(0, password.Length))
                 && TryDecrypt(ansiPasswordBytes, salt, nonce, opsLimit, memLimit, encryptedExtKey, extKeyBytes))
                {
                    usedLegacyPasswordEncoding = true;
                    return extKeyBytes;
                }
            }

            throw new CryptographicException("Decryption failed.");
        }
        catch
        {
            CryptographicOperations.ZeroMemory(extKeyBytes);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (ansiPasswordBytes is not null)
                CryptographicOperations.ZeroMemory(ansiPasswordBytes);
        }
    }

    private static bool TryDecrypt(ReadOnlySpan<byte> passwordBytes, ReadOnlySpan<byte> salt,
                                   ReadOnlySpan<byte> nonce, ulong opsLimit, ulong memLimit,
                                   ReadOnlySpan<byte> encryptedExtKey, Span<byte> extKeyBytes)
    {
        Span<byte> key = stackalloc byte[CryptoConstants.PrivkeyLen];
        try
        {
            using (var argon2Id = new Argon2Id())
            {
                argon2Id.DeriveKeyFromPasswordBytesAndSalt(passwordBytes, salt, key, opsLimit, memLimit);
            }

            using var xChaCha20Poly1305 = new XChaCha20Poly1305();
            xChaCha20Poly1305.Decrypt(key, nonce, ReadOnlySpan<byte>.Empty, encryptedExtKey, extKeyBytes);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecodeBase64Field(string? value, string name, int expectedLength)
    {
        if (string.IsNullOrEmpty(value))
            throw new SerializationException($"Invalid key file: missing {name}");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException e)
        {
            throw new SerializationException($"Invalid key file: {name} is not valid base64", e);
        }

        if (bytes.Length != expectedLength)
            throw new SerializationException($"Invalid key file: {name} must be {expectedLength} bytes");

        return bytes;
    }

    /// <summary>
    /// Copies the key file to <c>{filePath}.v{version}.bak</c> (owner-only, like the key file) unless that backup
    /// already exists, and returns the backup path.
    /// </summary>
    private static string BackupKeyFile(string filePath, int version)
    {
        var backupPath = $"{filePath}.v{Math.Max(version, KeyFileData.LegacyVersion)}.bak";
        if (File.Exists(backupPath))
            return backupPath;

        var sourcePath = ResolveFinalPath(filePath);
        WriteFileAtomically(backupPath, File.ReadAllText(sourcePath), sourcePath);
        return backupPath;
    }

    private static void WriteFileAtomically(string path, string contents, string? modeSourcePath = null)
    {
        var targetPath = ResolveFinalPath(path);
        var tempPath = CreateTempPath(targetPath);
        try
        {
            using (var stream = new FileStream(tempPath, CreateTempFileOptions()))
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                stream.Flush(true);
            }

            CopyOwnerFileMode(modeSourcePath ?? targetPath, tempPath);
            CopyOwnershipAndAcl(targetPath, tempPath);
            File.Move(tempPath, targetPath, true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        // The rename is only durable once the directory entry is on disk: without this a power loss could bring back
        // the old key file (for example a lower LastUsedIndex, so a channel key index used twice; SECURITY_REVIEW SR-19)
        SyncParentDirectory(targetPath);
    }

    /// <summary>
    /// fsyncs the directory that holds <paramref name="filePath"/> on Unix, so a rename into it survives a power loss.
    /// Windows commits a replace with <c>MoveFileEx(MOVEFILE_WRITE_THROUGH)</c> semantics already and needs nothing
    /// here. A failure throws: a key-index write that is not durable must not hand out the key.
    /// </summary>
    internal static void SyncParentDirectory(string filePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(directory))
            return;

        var fd = UnixOpen(directory, UnixOpenReadOnly);
        if (fd < 0)
            throw new IOException($"Could not open directory {directory} to sync it (errno {Marshal.GetLastPInvokeError()})");

        try
        {
            if (UnixFsync(fd) != 0)
                throw new IOException($"Could not sync directory {directory} (errno {Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private const int UnixOpenReadOnly = 0;

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fd);

    // stat(2) and chown(2) of RestoreUnixOwnership: the owner/group of the key file survive a rewrite (SR-14)
    [DllImport("libc", EntryPoint = "stat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int UnixStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int UnixChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    /// <summary>
    /// Follows symlinks so an atomic replace swaps the real file, not the link.
    /// </summary>
    private static string ResolveFinalPath(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null)
            return path;

        return info.ResolveLinkTarget(true)?.FullName ?? path;
    }

    private static string CreateTempPath(string targetPath) => $"{targetPath}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// The temp file is created owner-only (0600 on Unix), so key material is never readable by others, even briefly.
    /// </summary>
    private static FileStreamOptions CreateTempFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return options;
    }

    /// <summary>
    /// Keeps the owner bits the operator set on the existing file (for example 0400) across the replace, but never its
    /// group or other bits: a file written by an old build with the umask's 0644 becomes 0600 (SECURITY_REVIEW SR-02).
    /// </summary>
    private static void CopyOwnerFileMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(sourcePath))
            return;

        var sourceMode = File.GetUnixFileMode(sourcePath);
        if ((sourceMode & GroupOrOtherMode) != 0)
            Console.Error.WriteLine($"Key file {sourcePath} was readable by other users; the rewritten file is " +
                                    "owner-only. Consider the key exposed if other users had access to this host.");

        File.SetUnixFileMode(destinationPath, sourceMode & ~GroupOrOtherMode);
    }

    /// <summary>
    /// Keeps the existing file's owner, group (Unix) and Windows ACL across the atomic replace (SECURITY_REVIEW
    /// SR-14): the temp file is created by the running user, so a rewrite by another user, e.g. root, would otherwise
    /// hand the key file (and its 0600 mode) to that user instead of its owner.
    /// </summary>
    private static void CopyOwnershipAndAcl(string targetPath, string tempPath)
    {
        if (OperatingSystem.IsWindows())
            CopyWindowsAcl(targetPath, tempPath);
        else
            RestoreUnixOwnership(targetPath, tempPath);
    }

    /// <summary>
    /// Copies the existing key file's Windows ACL onto the temp file, so explicit ACEs an operator set on the key file
    /// survive the replace (without this the new file inherits only the directory's ACL). Best effort: when the ACL
    /// cannot be read or set, the new file keeps the directory's inheritance.
    /// </summary>
    private static void CopyWindowsAcl(string targetPath, string tempPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(targetPath))
            return;

        try
        {
            var security = new FileInfo(targetPath).GetAccessControl();
            new FileInfo(tempPath).SetAccessControl(security);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException)
        {
            // The directory's inherited ACL still applies; a hard failure here would lose the key-file write.
        }
    }

    /// <summary>
    /// Restores the existing key file's Unix owner and group onto the temp file. Best effort: only root (or another
    /// chown-capable process) can give the file back to its owner after someone else rewrote it, and a group change
    /// needs membership; when the chown is not permitted, the file stays with the running user, as before this
    /// preservation existed. The owner/group come from <c>stat(2)</c>'s <c>st_uid</c>/<c>st_gid</c> fields (offsets of
    /// the 64-bit <c>struct stat</c> of macOS and Linux).
    /// </summary>
    private static void RestoreUnixOwnership(string targetPath, string tempPath)
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
         || !File.Exists(targetPath) || !Environment.Is64BitProcess)
            return;

        var owner = GetUnixFileOwner(targetPath);
        if (owner is null)
            return;

        _ = UnixChown(tempPath, owner.Value.Uid, owner.Value.Gid);
    }

    /// <summary>
    /// Reads a Unix file's <c>st_uid</c>/<c>st_gid</c> through <c>stat(2)</c>, or null when the platform has no
    /// direct <c>stat</c> symbol (glibc older than 2.33) or the call failed. Internal for the tests.
    /// </summary>
    internal static (uint Uid, uint Gid)? GetUnixFileOwner(string path)
    {
        // A buffer comfortably larger than every platform's struct stat (144 bytes): the native call writes its own
        // size, the untouched tail is ignored
        try
        {
            var buffer = new byte[256];
            if (UnixStat(path, buffer) != 0)
                return null;

            // macOS: st_uid at 16, st_gid at 20. Linux (glibc and musl, 64-bit): st_uid at 24, st_gid at 28.
            var littleEndian = BitConverter.IsLittleEndian;
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                       ? (littleEndian ? BitConverter.ToUInt32(buffer, 16) : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16)),
                          littleEndian ? BitConverter.ToUInt32(buffer, 20) : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(20)))
                       : (littleEndian ? BitConverter.ToUInt32(buffer, 24) : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(24)),
                          littleEndian ? BitConverter.ToUInt32(buffer, 28) : BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(28)));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // libc without a direct stat symbol (glibc older than 2.33): leave the ownership alone
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: the original exception is more useful than a cleanup failure.
        }
    }

    private static IntPtr AllocateSecure(byte[] material, out ulong length)
    {
        length = (ulong)material.Length;

        using var cryptoProvider = CryptoFactory.GetCryptoProvider();

        // Allocate secure memory
        var ptr = cryptoProvider.MemoryAlloc(length);

        // Lock the memory to prevent swapping
        if (cryptoProvider.MemoryLock(ptr, length) == -1)
        {
            cryptoProvider.MemoryFree(ptr);
            throw new InvalidOperationException("Failed to lock memory.");
        }

        Marshal.Copy(material, 0, ptr, material.Length);
        return ptr;
    }

    private static void FreeSecure(ref IntPtr ptr, ref ulong length)
    {
        if (ptr == IntPtr.Zero)
            return;

        using var cryptoProvider = CryptoFactory.GetCryptoProvider();

        // Securely wipe the memory before freeing it
        cryptoProvider.MemoryZero(ptr, length);

        // Unlock the memory
        cryptoProvider.MemoryUnlock(ptr, length);

        // MemoryFree the memory
        cryptoProvider.MemoryFree(ptr);

        length = 0;
        ptr = IntPtr.Zero;
    }

    /// <summary>
    /// Copies bytes out of secure memory into a new array the caller must zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the key is not initialized.</exception>
    private static byte[] CopyFromSecure(IntPtr ptr, ulong length)
    {
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException("Secure key is not initialized.");

        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, (int)length);
        return bytes;
    }

    /// <summary>
    /// Writes <see cref="_lastUsedIndex"/> into the key file (atomically, flushed). The caller holds
    /// <see cref="_lastUsedIndexLock"/>. Never lowers the stored index, and does nothing while there is no key file (a
    /// key manager that was never saved).
    /// </summary>
    private void PersistLastUsedIndex()
    {
        if (!File.Exists(_filePath))
            return;

        var data = JsonSerializer.Deserialize<KeyFileData>(File.ReadAllText(_filePath))
                ?? throw new SerializationException("Invalid key file");
        if (data.LastUsedIndex > _lastUsedIndex)
            return;

        data.LastUsedIndex = _lastUsedIndex;
        WriteFileAtomically(_filePath, JsonSerializer.Serialize(data));
    }

    private ExtKey GetMasterKey()
    {
        var material = CopyFromSecure(_secureMasterKeyPtr, _masterKeyLength);
        var privateKey = material.AsSpan(0, CryptoConstants.PrivkeyLen).ToArray();
        try
        {
            // NBitcoin's Key and ExtKey keep their own copies; the plain arrays are wiped here
            var chainCode = DerivationScheme == KeyDerivationScheme.Bip32
                                ? material.AsSpan(CryptoConstants.PrivkeyLen, ChainCodeLength).ToArray()
                                : _network.GenesisHash.ToBytes();
            return new ExtKey(new Key(privateKey), chainCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private void ReleaseUnmanagedResources()
    {
        FreeSecure(ref _secureMasterKeyPtr, ref _masterKeyLength);
        FreeSecure(ref _secureNodeKeyPtr, ref _nodeKeyLength);
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~SecureKeyManager()
    {
        ReleaseUnmanagedResources();
    }
}