using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>Encrypted signer-owned nonce journal. A nonce is durably consumed before it can produce a signature.</summary>
public sealed class NativeNonceStateStore : IDisposable
{
    private const long MaxJournalBytes = 64 * 1024 * 1024;
    private const int MaxRecordBytes = 8 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly byte[] _key;
    private readonly object _gate = new();
    private readonly Dictionary<string, NonceRecord> _records = new(StringComparer.Ordinal);
    private bool _failed;

    public static NativeNonceStateStore ForSigner(string path, ISecureKeyManager keys)
    {
        var secret = keys.GetNodeKeyPair().PrivKey.Value.ToArray();
        var key = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes("NLightning/native-nonce-journal/v1"));
        try { return new NativeNonceStateStore(path, key); }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(key); }
    }

    public NativeNonceStateStore(string path, ReadOnlySpan<byte> encryptionKey)
    {
        _key = encryptionKey.ToArray();
        if (_key.Length != 32) throw new ArgumentException("Native nonce journal encryption key must be 32 bytes.");
        if (new FileInfo(path).LinkTarget is not null)
            throw new UnauthorizedAccessException("Native nonce journal must not be a symbolic link.");
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
                throw new UnauthorizedAccessException("Native nonce journal must be accessible only to the signer owner.");
            if (_stream.Length > MaxJournalBytes) throw new InvalidDataException("Native nonce journal exceeds its size limit.");
            Load();
            // Persist the directory entry before any nonce or session can be returned.
            _stream.Flush(flushToDisk: true);
            Managers.SecureKeyManager.SyncParentDirectory(path);
        }
        catch { Dispose(); throw; }
    }

    internal MusigNoncePair Store(string purpose, ChannelId channel, MusigNoncePair pair, byte[]? context = null)
    {
        lock (_gate)
        {
            RequireHealthy();
            var bytes = new byte[97];
            try
            {
                pair.SecretNonce.Consume(bytes);
                var record = new NonceRecord(purpose, channel.ToString(), pair.PublicNonce, bytes.ToArray(), context?.ToArray(), false);
                var id = Id(record.Purpose, record.Channel, record.PublicNonce);
                if (_records.ContainsKey(id) || _records.Values.Any(r => r.PublicNonce.AsSpan().SequenceEqual(record.PublicNonce)))
                    throw new InvalidOperationException("Native nonce identity already exists.");
                if (_records.Count >= 65_536) throw new InvalidOperationException("Native nonce identity capacity reached.");
                var live = _records.Where(p => p.Value.Purpose == purpose && p.Value.Channel == record.Channel && !p.Value.Consumed).ToArray();
                var limit = purpose is "gossip-node" or "gossip-bitcoin" ? 1 : 8;
                foreach (var (expiredId, expired) in live.Take(Math.Max(0, live.Length - limit + 1)))
                {
                    CryptographicOperations.ZeroMemory(expired.Secret);
                    _records[expiredId] = expired with { Secret = [], Consumed = true };
                }
                _records.Add(id, record);
                Save();
                return new MusigNoncePair(new MusigSecretNonce(bytes), pair.PublicNonce);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal MusigSecretNonce? Take(string purpose, ChannelId channel, MusigPublicNonce publicNonce)
    {
        lock (_gate)
        {
            RequireHealthy();
            var id = Id(purpose, channel.ToString(), publicNonce);
            if (!_records.TryGetValue(id, out var record) || record.Consumed) return null;
            var secret = record.Secret.ToArray();
            try
            {
                CryptographicOperations.ZeroMemory(record.Secret);
                _records[id] = record with { Secret = [], Consumed = true };
                Save();
                return new MusigSecretNonce(secret);
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
    }

    internal (MusigNoncePair Pair, byte[]? Context)? TakeLatest(string purpose, ChannelId channel)
    {
        lock (_gate)
        {
            RequireHealthy();
            var record = _records.Values.LastOrDefault(r => r.Purpose == purpose && r.Channel == channel.ToString() && !r.Consumed);
            if (record is null) return null;
            var secret = Take(purpose, channel, new MusigPublicNonce(record.PublicNonce));
            return secret is null ? null : (new MusigNoncePair(secret, record.PublicNonce), record.Context);
        }
    }

    internal void Forget(string purpose, ChannelId channel, MusigPublicNonce? nonce = null)
    {
        lock (_gate)
        {
            RequireHealthy();
            var changed = false;
            foreach (var (id, record) in _records.Where(p => p.Value.Purpose == purpose && p.Value.Channel == channel.ToString()
                && !p.Value.Consumed && (nonce is null || p.Value.PublicNonce.AsSpan().SequenceEqual((byte[])nonce.Value))).ToArray())
            {
                CryptographicOperations.ZeroMemory(record.Secret);
                _records[id] = record with { Secret = [], Consumed = true };
                changed = true;
            }
            if (changed) Save();
        }
    }

    private static string Id(string purpose, string channel, byte[] publicNonce) => $"{purpose}:{channel}:{Convert.ToHexString(publicNonce)}";
    private void RequireHealthy() { if (_failed) throw new InvalidOperationException("Native nonce journal failed; signing is blocked."); }

    private void Load()
    {
        var header = new byte[4];
        while (_stream.Position < _stream.Length)
        {
            _stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 28 or > MaxRecordBytes || length > _stream.Length - _stream.Position)
                throw new InvalidDataException("Incomplete native nonce journal record; signing is blocked.");
            var record = new byte[length];
            _stream.ReadExactly(record);
            var plaintext = new byte[length - 28];
            try
            {
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(record.AsSpan(0, 12), record.AsSpan(28), record.AsSpan(12, 16), plaintext, header);
                var entries = JsonSerializer.Deserialize<NonceRecord[]>(plaintext) ?? throw new InvalidDataException("Missing nonce snapshot.");
                foreach (var existing in _records.Values) CryptographicOperations.ZeroMemory(existing.Secret);
                _records.Clear();
                foreach (var entry in entries)
                {
                    if (entry.PublicNonce.Length != 66 || entry.Secret.Length != (entry.Consumed ? 0 : 97))
                        throw new InvalidDataException("Invalid native nonce snapshot.");
                    _records.Add(Id(entry.Purpose, entry.Channel, entry.PublicNonce), entry);
                }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    private void Save()
    {
        RequireHealthy();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_records.Values.ToArray());
        try
        {
            var length = checked(plaintext.Length + 28);
            if (length > MaxRecordBytes || _stream.Length + length + 4 > MaxJournalBytes)
                throw new InvalidOperationException("Native nonce journal size limit reached.");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            var record = new byte[length];
            RandomNumberGenerator.Fill(record.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(record.AsSpan(0, 12), plaintext, record.AsSpan(28), record.AsSpan(12, 16), header);
            _stream.Write(header);
            _stream.Write(record);
            _stream.Flush(flushToDisk: true);
        }
        catch { _failed = true; throw; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public byte[] GetCheckpointDigest()
    {
        lock (_gate)
        {
            RequireHealthy();
            var position = _stream.Position;
            try
            {
                _stream.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(HMACSHA256.HashData(_key, "NLightning/native-nonce-checkpoint/v1"u8));
                var buffer = new byte[8192];
                int count;
                while ((count = _stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
                return hash.GetHashAndReset();
            }
            finally { _stream.Position = position; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _failed = true;
            _stream.Dispose();
            foreach (var record in _records.Values) CryptographicOperations.ZeroMemory(record.Secret);
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    private sealed record NonceRecord(string Purpose, string Channel, byte[] PublicNonce, byte[] Secret, byte[]? Context, bool Consumed);
}