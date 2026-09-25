using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Managers;

using Crypto.Functions;
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

/// <summary>
/// Manages a securely stored private key using protected memory allocation.
/// This class ensures that the private key remains inaccessible from regular memory
/// and is securely wiped when no longer needed.
/// </summary>
public class SecureKeyManager : ISecureKeyManager, IDisposable
{
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

    private static readonly Ecdh s_ecdh = new();

    private readonly string _filePath;
    private readonly object _lastUsedIndexLock = new();
    private readonly Network _network;
    private readonly KeyPath _channelKeyPath = new(KeyConstants.ChannelKeyPathString);
    private readonly KeyPath _depositP2TrKeyPath = new(KeyConstants.P2TrKeyPathString);
    private readonly KeyPath _depositP2WpkhKeyPath = new(KeyConstants.P2WpkhKeyPathString);

    private uint _lastUsedIndex;
    private ulong _privateKeyLength;
    private IntPtr _securePrivateKeyPtr;

    public BitcoinKeyPath ChannelKeyPath => _channelKeyPath.ToBytes();
    public BitcoinKeyPath DepositP2TrKeyPath => _depositP2TrKeyPath.ToBytes();
    public BitcoinKeyPath DepositP2WpkhKeyPath => _depositP2WpkhKeyPath.ToBytes();

    public string OutputChannelDescriptor { get; init; }
    public string OutputDepositP2TrDescriptor { get; init; }

    public string OutputDepositP2WshDescriptor { get; init; }
    public string OutputChangeP2TrDescriptor { get; init; }

    public string OutputChangeP2WshDescriptor { get; init; }

    public uint HeightOfBirth { get; init; }

    /// <summary>
    /// Manages secure key operations for generating and managing cryptographic keys.
    /// Provides functionality to safely store, load, and derive secure keys protected in memory.
    /// </summary>
    /// <param name="privateKey">The private key to be managed.</param>
    /// <param name="network">The network associated with the private key.</param>
    /// <param name="filePath">The file path for storing the key data.</param>
    /// <param name="heightOfBirth">Block Height when the wallet was created</param>
    public SecureKeyManager(byte[] privateKey, BitcoinNetwork network, string filePath, uint heightOfBirth)
    {
        _privateKeyLength = (ulong)privateKey.Length;

        using var cryptoProvider = CryptoFactory.GetCryptoProvider();

        // Allocate secure memory
        _securePrivateKeyPtr = cryptoProvider.MemoryAlloc(_privateKeyLength);

        // Lock the memory to prevent swapping
        if (cryptoProvider.MemoryLock(_securePrivateKeyPtr, _privateKeyLength) == -1)
            throw new InvalidOperationException("Failed to lock memory.");

        // Copy the private key to secure memory
        Marshal.Copy(privateKey, 0, _securePrivateKeyPtr, (int)_privateKeyLength);

        // Get Output Descriptor
        _network = Network.GetNetwork(network)
                ?? throw new ArgumentException("Invalid network specified.", nameof(network));
        var extKey = new ExtKey(new Key(privateKey), network.ChainHash);
        var xpub = extKey.Neuter().ToString(_network);
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();

        OutputChannelDescriptor = $"wpkh([{fingerprint}/{ChannelKeyPath}/*]{xpub}/0/*)";
        OutputDepositP2TrDescriptor = $"tr([{fingerprint}/{DepositP2TrKeyPath}]{xpub}/0/*)";
        OutputChangeP2TrDescriptor = $"tr([{fingerprint}/{DepositP2TrKeyPath}]{xpub}/1/*)";
        OutputDepositP2WshDescriptor = $"wpkh([{fingerprint}/{DepositP2WpkhKeyPath}]{xpub}/0/*)";
        OutputChangeP2WshDescriptor = $"wpkh([{fingerprint}/{DepositP2WpkhKeyPath}]{xpub}/1/*)";

        // Securely wipe the original key from regular memory
        cryptoProvider.MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(privateKey, 0), _privateKeyLength);

        _filePath = filePath;
        HeightOfBirth = heightOfBirth;
    }

    public ExtPrivKey GetNextChannelKey(out uint index)
    {
        lock (_lastUsedIndexLock)
        {
            _lastUsedIndex++;
            index = _lastUsedIndex;
        }

        // Derive the key at m/6425'/0'/0'/0/index
        var masterKey = GetMasterKey();
        var derivedKey = masterKey.Derive(_channelKeyPath.Derive(index));

        _ = UpdateLastUsedChannelIndexOnFile().ContinueWith(task =>
        {
            if (task.IsFaulted)
                Console.Error.WriteLine($"Failed to update last used index on file: {task.Exception.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);

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
        var masterKey = GetMasterKey();
        return new CryptoKeyPair(masterKey.PrivateKey.ToBytes(), masterKey.PrivateKey.PubKey.ToBytes());
    }

    public CompactPubKey GetNodePubKey()
    {
        var masterKey = GetMasterKey();
        return masterKey.PrivateKey.PubKey.ToBytes();
    }

    /// <inheritdoc/>
    public void ComputeNodeSharedSecret(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret)
    {
        // The node key is the master private key; copy it out of locked memory only for the ECDH, then wipe it
        var privateKey = GetPrivateKeyBytes();
        try
        {
            s_ecdh.SecP256K1Dh(privateKey, publicKey, sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public async Task UpdateLastUsedChannelIndexOnFile()
    {
        var jsonString = await File.ReadAllTextAsync(_filePath);
        var data = JsonSerializer.Deserialize<KeyFileData>(jsonString)
                ?? throw new SerializationException("Invalid key file");

        lock (_lastUsedIndexLock)
        {
            data.LastUsedIndex = _lastUsedIndex;
        }

        jsonString = JsonSerializer.Serialize(data);

        await WriteFileAtomicallyAsync(_filePath, jsonString);
    }

    /// <summary>
    /// Encrypts the master key with <paramref name="password"/> and writes the key file in the current
    /// (<see cref="KeyFileData.CurrentVersion"/>) format, with a fresh random salt and nonce.
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

            var data = new KeyFileData
            {
                Version = KeyFileData.CurrentVersion,
                Network = _network.ToString(),
                LastUsedIndex = _lastUsedIndex,
                Descriptor = OutputChannelDescriptor,
                EncryptedExtKey = Convert.ToBase64String(cipherText),
                HeightOfBirth = HeightOfBirth,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Argon2MemLimit = Argon2Id.DefaultMemLimit,
                Argon2OpsLimit = Argon2Id.DefaultOpsLimit
            };
            var json = JsonSerializer.Serialize(data);
            WriteFileAtomically(_filePath, json);
        }
    }

    public static SecureKeyManager FromMnemonic(string mnemonic, string passphrase, BitcoinNetwork network,
                                                string? filePath = null, uint currentHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            filePath = GetKeyFilePath(network);

        var mnemonicObj = new Mnemonic(mnemonic, Wordlist.English);
        var extKey = mnemonicObj.DeriveExtKey(passphrase);
        return new SecureKeyManager(extKey.PrivateKey.ToBytes(), network, filePath, currentHeight);
    }

    public static SecureKeyManager FromFilePath(string filePath, BitcoinNetwork expectedNetwork, string password)
    {
        var jsonString = File.ReadAllText(filePath);
        var data = JsonSerializer.Deserialize<KeyFileData>(jsonString)
                ?? throw new SerializationException("Invalid key file");

        if (expectedNetwork != data.Network.ToLowerInvariant())
            throw new Exception($"Invalid network. Expected {expectedNetwork}, but got {data.Network}");

        var network = Network.GetNetwork(expectedNetwork)
                   ?? throw new ArgumentException("Invalid network specified.", nameof(expectedNetwork));

        var extKeyBytes = DecryptExtKey(data, password, out var usedLegacyPasswordEncoding);
        ExtKey extKey;
        try
        {
            extKey = ExtKey.Parse(Encoding.UTF8.GetString(extKeyBytes), network);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(extKeyBytes);
        }

        var keyManager =
            new SecureKeyManager(extKey.PrivateKey.ToBytes(), expectedNetwork, filePath, data.HeightOfBirth)
            {
                _lastUsedIndex = data.LastUsedIndex,
                OutputChannelDescriptor = data.Descriptor
            };

        if (data.Version < KeyFileData.CurrentVersion || usedLegacyPasswordEncoding)
        {
            // Migrate legacy key files (fixed salt, zero nonce, weak Argon2id parameters, or a password hashed with
            // the truncated libsodium encoding) to the current format. Older binaries cannot read the new format,
            // so keep a copy of the original file first.
            try
            {
                var backupPath = BackupKeyFile(filePath, data.Version);
                Console.Error.WriteLine($"Upgrading key file {filePath} to version {KeyFileData.CurrentVersion}. " +
                                        $"The original file was saved to {backupPath}; builds older than this " +
                                        "one cannot read the upgraded file.");
                keyManager.SaveToFile(password);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to upgrade key file {filePath} to version " +
                                        $"{KeyFileData.CurrentVersion}: {e.Message}");
            }
        }

        return keyManager;
    }

    /// <summary>
    /// Gets the path for the Key file
    /// </summary>
    public static string GetKeyFilePath(string configPath)
    {
        return Path.Combine(configPath, "nltg.key.json");
    }

    private static byte[] DecryptExtKey(KeyFileData data, string password, out bool usedLegacyPasswordEncoding)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt;
        byte[] nonce;
        ulong opsLimit;
        ulong memLimit;
        switch (data.Version)
        {
            case 0 or KeyFileData.LegacyVersion:
                salt = s_legacySalt;
                nonce = new byte[CryptoConstants.Xchacha20Poly1305NonceLen];
                opsLimit = LegacyArgon2OpsLimit;
                memLimit = Argon2Id.LegacyMemLimit;
                break;
            case KeyFileData.CurrentVersion:
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
        try
        {
            usedLegacyPasswordEncoding = false;
            if (TryDecrypt(passwordBytes, salt, nonce, opsLimit, memLimit, encryptedExtKey, extKeyBytes))
                return extKeyBytes;

            // Before the fix for the libsodium password length, the libsodium backend hashed only the first
            // password.Length (UTF-16 char count) bytes of the UTF-8 password. For non-ASCII passwords, retry with
            // that truncated encoding so files written by those builds still open.
            if (passwordBytes.Length != password.Length
             && TryDecrypt(passwordBytes.AsSpan(0, password.Length), salt, nonce, opsLimit, memLimit, encryptedExtKey,
                           extKeyBytes))
            {
                usedLegacyPasswordEncoding = true;
                return extKeyBytes;
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
    /// Copies the key file to <c>{filePath}.v{version}.bak</c> (keeping its permissions) unless that backup already
    /// exists, and returns the backup path.
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

            CopyUnixFileMode(modeSourcePath ?? targetPath, tempPath);
            File.Move(tempPath, targetPath, true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static async Task WriteFileAtomicallyAsync(string path, string contents)
    {
        var targetPath = ResolveFinalPath(path);
        var tempPath = CreateTempPath(targetPath);
        try
        {
            await using (var stream = new FileStream(tempPath, CreateTempFileOptions()))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents));
                await stream.FlushAsync();
            }

            CopyUnixFileMode(targetPath, tempPath);
            File.Move(tempPath, targetPath, true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

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
    /// Keeps the permissions the operator set on the existing file (for example chmod 600) across the replace.
    /// </summary>
    private static void CopyUnixFileMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(sourcePath))
            return;

        File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
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

    private ExtKey GetMasterKey()
    {
        return new ExtKey(new Key(GetPrivateKeyBytes()), _network.GenesisHash.ToBytes());
    }

    private void ReleaseUnmanagedResources()
    {
        if (_securePrivateKeyPtr == IntPtr.Zero)
            return;

        using var cryptoProvider = CryptoFactory.GetCryptoProvider();

        // Securely wipe the memory before freeing it
        cryptoProvider.MemoryZero(_securePrivateKeyPtr, _privateKeyLength);

        // Unlock the memory
        cryptoProvider.MemoryUnlock(_securePrivateKeyPtr, _privateKeyLength);

        // MemoryFree the memory
        cryptoProvider.MemoryFree(_securePrivateKeyPtr);

        _privateKeyLength = 0;
        _securePrivateKeyPtr = IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves the private key stored in secure memory.
    /// </summary>
    /// <returns>The private key as a byte array.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the key is not initialized.</exception>
    private byte[] GetPrivateKeyBytes()
    {
        if (_securePrivateKeyPtr == IntPtr.Zero)
            throw new InvalidOperationException("Secure key is not initialized.");

        var privateKey = new byte[_privateKeyLength];
        Marshal.Copy(_securePrivateKeyPtr, privateKey, 0, (int)_privateKeyLength);

        return privateKey;
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