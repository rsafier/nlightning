using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.KeyRing;

/// <summary>Signer-local encrypted session journal. Every transition is flushed before its result is exposed.</summary>
public sealed class SwapSessionStateStore : IDisposable
{
    private const int MaxRecordBytes = 8 * 1024 * 1024;
    private const long MaxJournalBytes = 64 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly byte[] _key;
    private bool _failed;
    internal IReadOnlyList<SwapSessionSnapshot> Sessions { get; private set; } = [];

    public SwapSessionStateStore(string path, ReadOnlySpan<byte> encryptionKey)
    {
        _key = encryptionKey.ToArray();
        if (_key.Length != 32) throw new ArgumentException("Swap journal encryption key must be 32 bytes.");
        if (new FileInfo(path).LinkTarget is not null)
            throw new UnauthorizedAccessException("Swap journal must not be a symbolic link.");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(path, options);
        try
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                throw new UnauthorizedAccessException("Swap journal must be accessible only to the signer owner.");
            if (_stream.Length > MaxJournalBytes) throw new InvalidDataException("Swap journal exceeds its size limit.");
            Load();
            // Persist the directory entry before any nonce or session can be returned.
            _stream.Flush(flushToDisk: true);
            Managers.SecureKeyManager.SyncParentDirectory(path);
        }
        catch { Dispose(); throw; }
    }

    private void Load()
    {
        var header = new byte[4];
        while (_stream.Position < _stream.Length)
        {
            _stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 28 or > MaxRecordBytes || length > _stream.Length - _stream.Position)
                throw new InvalidDataException("Incomplete or invalid swap journal record; signing is blocked.");
            var record = new byte[length];
            _stream.ReadExactly(record);
            var plaintext = new byte[length - 28];
            try
            {
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(record.AsSpan(0, 12), record.AsSpan(28), record.AsSpan(12, 16), plaintext, header);
                Sessions = JsonSerializer.Deserialize<SwapSessionSnapshot[]>(plaintext)
                    ?? throw new InvalidDataException("Missing swap sessions.");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    internal void Save(IReadOnlyList<SwapSessionSnapshot> sessions)
    {
        if (_failed) throw new InvalidOperationException("Swap journal failed; signing is blocked until recovery.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(sessions);
        try
        {
            var length = checked(plaintext.Length + 28);
            if (length > MaxRecordBytes || _stream.Length + length + 4 > MaxJournalBytes)
                throw new InvalidOperationException("Swap journal size limit reached.");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            var record = new byte[length];
            RandomNumberGenerator.Fill(record.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(record.AsSpan(0, 12), plaintext, record.AsSpan(28), record.AsSpan(12, 16), header);
            _stream.Write(header);
            _stream.Write(record);
            _stream.Flush(flushToDisk: true);
            Sessions = sessions;
        }
        catch { _failed = true; throw; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public byte[] GetCheckpointDigest()
    {
        if (_failed) throw new InvalidOperationException("Swap journal failed; signing is blocked.");
        var position = _stream.Position;
        try
        {
            _stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(HMACSHA256.HashData(_key, "NLightning/native-swap-checkpoint/v1"u8));
            var buffer = new byte[8192];
            int count;
            while ((count = _stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
            return hash.GetHashAndReset();
        }
        finally { _stream.Position = position; }
    }

    public void Dispose() { _stream.Dispose(); CryptographicOperations.ZeroMemory(_key); }
}

internal sealed record SwapSessionSnapshot(int Family, int Index, byte[][] Keys, byte[][] Tweaks,
    bool[] XOnlyTweaks, byte[] InternalKey, byte[] OutputKey, byte[] Randomness,
    byte[][] Nonces, DateTimeOffset Created, byte[]? Digest, byte[][] Partials, bool Consumed);