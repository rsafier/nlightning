using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Application.Node.PeerStorage;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;

/// <summary>
/// The default <see cref="IPeerBackupBlobProvider"/>: the ids and peers of our channels that are past funding and not
/// closed, encrypted with <see cref="IPeerStorageCipher"/> and padded so every blob is exactly
/// <see cref="PeerStorageConstants.MaxBlobLength"/> bytes (BOLT 1 SHOULD). It is enough to learn, after a data loss,
/// which channels we had and with whom (the static channel backup lane replaces it with a full backup).
/// </summary>
/// <remarks>
/// Plaintext (version 1): magic <c>"NLPB"</c>, version (1 byte), creation time (u64 UNIX seconds), channel count
/// (u16), then per channel its id (32 bytes) and the peer's node id (33 bytes), then zeroes up to
/// <see cref="IPeerStorageCipher.MaxPlaintextLength"/>. At most <see cref="MaxChannels"/> channels fit; more are left
/// out (logged by the caller's view of the count).
/// </remarks>
public sealed class ChannelListPeerBackupBlobProvider : IPeerBackupBlobProvider
{
    internal const byte Version = 1;
    private const int HeaderLength = 4 + 1 + 8 + 2;
    private const int EntryLength = 32 + CryptoConstants.CompactPubkeyLen;

    private static readonly byte[] s_magic = "NLPB"u8.ToArray();

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerStorageCipher _cipher;
    private readonly TimeProvider _timeProvider;

    public ChannelListPeerBackupBlobProvider(IChannelMemoryRepository channelMemoryRepository,
                                            IPeerStorageCipher cipher, TimeProvider? timeProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _cipher = cipher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How many channels one blob can name.
    /// </summary>
    public int MaxChannels => (_cipher.MaxPlaintextLength - HeaderLength) / EntryLength;

    /// <inheritdoc />
    public Task<PeerBackupBlob?> CreateBlobAsync(CancellationToken cancellationToken = default)
    {
        var channels = _channelMemoryRepository
                      .FindChannels(c => c.State is >= ChannelState.V1FundingSigned
                                                and not (ChannelState.Closed or ChannelState.Stale))
                      .Select(c => new PeerBackupChannel(c.ChannelId, c.RemoteNodeId))
                      .OrderBy(c => Convert.ToHexString(c.ChannelId))
                      .Take(MaxChannels)
                      .ToList();
        if (channels.Count == 0)
            return Task.FromResult<PeerBackupBlob?>(null);

        var plaintext = new byte[_cipher.MaxPlaintextLength];
        s_magic.CopyTo(plaintext, 0);
        plaintext[4] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(plaintext.AsSpan(5),
                                              (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(13), (ushort)channels.Count);
        var offset = HeaderLength;
        foreach (var channel in channels)
        {
            ((ReadOnlySpan<byte>)channel.ChannelId).CopyTo(plaintext.AsSpan(offset));
            ((ReadOnlySpan<byte>)channel.PeerNodeId).CopyTo(plaintext.AsSpan(offset + 32));
            offset += EntryLength;
        }

        // The fingerprint leaves out the creation time: an unchanged channel list is not sent again
        var fingerprint = Convert.ToHexString(SHA256.HashData(plaintext.AsSpan(HeaderLength, offset - HeaderLength)));
        var blob = _cipher.Encrypt(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        return Task.FromResult<PeerBackupBlob?>(new PeerBackupBlob(blob, fingerprint));
    }

    /// <inheritdoc />
    public Task<PeerBackupContents?> TryReadBlobAsync(ReadOnlyMemory<byte> blob,
                                                      CancellationToken cancellationToken = default)
    {
        var plaintext = _cipher.TryDecrypt(blob.Span);
        if (plaintext is null || plaintext.Length < HeaderLength || !plaintext.AsSpan(0, 4).SequenceEqual(s_magic)
         || plaintext[4] != Version)
            return Task.FromResult<PeerBackupContents?>(null);

        var createdAt =
            DateTimeOffset.FromUnixTimeSeconds((long)BinaryPrimitives.ReadUInt64BigEndian(plaintext.AsSpan(5)));
        var count = BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(13));
        if (HeaderLength + count * EntryLength > plaintext.Length)
            return Task.FromResult<PeerBackupContents?>(null);

        var channels = new List<PeerBackupChannel>(count);
        var offset = HeaderLength;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var channelId = new ChannelId(plaintext.AsSpan(offset, 32));
                var peer = new CompactPubKey(plaintext.AsSpan(offset + 32, CryptoConstants.CompactPubkeyLen)
                                                      .ToArray());
                channels.Add(new PeerBackupChannel(channelId, peer));
            }
            catch (ArgumentException)
            {
                // Authenticated, so only a bug of ours writes a bad key: keep the rest
            }

            offset += EntryLength;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(plaintext.AsSpan(HeaderLength, count * EntryLength)));
        return Task.FromResult<PeerBackupContents?>(new PeerBackupContents(createdAt, channels, fingerprint));
    }
}