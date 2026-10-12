using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Node.PeerStorage;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;

/// <summary>
/// The default <see cref="IPeerBackupBlobProvider"/>: the ids and peers of our channels that are past funding and not
/// closed, with each channel's current funding outpoint and our funding key index, encrypted with
/// <see cref="IPeerStorageCipher"/> and padded so every blob is exactly <see cref="PeerStorageConstants.MaxBlobLength"/>
/// bytes (BOLT 1 SHOULD). It is enough to learn, after a data loss, which channels we had, with whom and at which
/// funding (the static channel backup lane replaces it with a full backup).
/// </summary>
/// <remarks>
/// <para>A peer that refuses that length (a <c>warning</c>, NL-559; e.g. LDK takes at most 1,024 bytes) gets the sized
/// variant instead (<see cref="CreateBlobAsync(int, CancellationToken)"/>): the same backup padded to exactly the
/// length it accepts, naming the first channels that fit it. A BOLT 1 SHOULD is traded there for a peer that keeps
/// something rather than nothing.</para>
/// <para>Plaintext version 2 (splicing plan SP2-0, lane SP2-E; written since): magic <c>"NLPB"</c>, version (1 byte),
/// creation time (u64 UNIX seconds), channel count (u16), then per channel its id (32 bytes), the peer's node id (33
/// bytes), a flags byte (bit 0: the funding outpoint follows; bit 1: our funding key index is known; bit 2: a simple
/// taproot channel, NL-877 T5, ignored by readers that predate it), the current
/// funding txid (32 bytes, zeroes when unknown), its output index (u16) and our funding key index (u32; 0 before any
/// splice), then zeroes up to <see cref="IPeerStorageCipher.MaxPlaintextLength"/>. The funding moves with a locked
/// splice (and a dual-funded RBF), so the fingerprint changes and the blob is sent again.</para>
/// <para>Version 1 (still read): the same header, then per channel only its id and the peer's node id.</para>
/// <para>At most <see cref="MaxChannels"/> channels fit; more are left out.</para>
/// </remarks>
public sealed class ChannelListPeerBackupBlobProvider : IPeerBackupBlobProvider
{
    internal const byte Version = 2;
    internal const byte Version1 = 1;
    private const int HeaderLength = 4 + 1 + 8 + 2;
    private const int Version1EntryLength = 32 + CryptoConstants.CompactPubkeyLen;
    private const int EntryLength = Version1EntryLength + 1 + CryptoConstants.Sha256HashLen + 2 + 4;
    private const byte FlagFunding = 1;
    private const byte FlagKeyIndex = 2;
    private const byte FlagSimpleTaproot = 4;

    private static readonly byte[] s_magic = "NLPB"u8.ToArray();

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerStorageCipher _cipher;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    public ChannelListPeerBackupBlobProvider(IChannelMemoryRepository channelMemoryRepository,
                                            IPeerStorageCipher cipher, TimeProvider? timeProvider = null,
                                            IServiceScopeFactory? serviceScopeFactory = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _cipher = cipher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>
    /// How many channels one blob can name.
    /// </summary>
    public int MaxChannels => (_cipher.MaxPlaintextLength - HeaderLength) / EntryLength;

    /// <inheritdoc />
    public Task<PeerBackupBlob?> CreateBlobAsync(CancellationToken cancellationToken = default) =>
        CreateBlobAsync(PeerStorageConstants.MaxBlobLength, cancellationToken);

    /// <summary>
    /// The blob length <see cref="Encrypt"/> adds over the plaintext: version, nonce and tag
    /// (<see cref="PeerStorageConstants.MaxBlobLength"/> minus <see cref="IPeerStorageCipher.MaxPlaintextLength"/>).
    /// </summary>
    private int CipherOverhead => PeerStorageConstants.MaxBlobLength - _cipher.MaxPlaintextLength;

    /// <inheritdoc />
    public async Task<PeerBackupBlob?> CreateBlobAsync(int maxBlobLength, CancellationToken cancellationToken = default)
    {
        var plaintextLength = Math.Min(maxBlobLength, PeerStorageConstants.MaxBlobLength) - CipherOverhead;
        var maxChannels = Math.Min(MaxChannels, (plaintextLength - HeaderLength) / EntryLength);
        if (maxChannels <= 0)
            return null;

        var models = _channelMemoryRepository
                    .FindChannels(c => c.State is >= ChannelState.V1FundingSigned
                                              and not (ChannelState.Closed or ChannelState.Stale))
                    .OrderBy(c => Convert.ToHexString(c.ChannelId))
                    .Take(maxChannels)
                    .ToList();
        if (models.Count == 0)
            return null;

        var channels = new List<PeerBackupChannel>(models.Count);
        foreach (var model in models)
            channels.Add(await DescribeAsync(model, cancellationToken));

        var plaintext = new byte[plaintextLength];
        s_magic.CopyTo(plaintext, 0);
        plaintext[4] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(plaintext.AsSpan(5),
                                              (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(13), (ushort)channels.Count);
        var offset = HeaderLength;
        foreach (var channel in channels)
        {
            WriteEntry(plaintext.AsSpan(offset, EntryLength), channel);
            offset += EntryLength;
        }

        // The fingerprint leaves out the creation time: an unchanged channel list is not sent again
        var fingerprint = Convert.ToHexString(SHA256.HashData(plaintext.AsSpan(HeaderLength, offset - HeaderLength)));
        var blob = _cipher.Encrypt(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        return new PeerBackupBlob(blob, fingerprint);
    }

    /// <inheritdoc />
    public Task<PeerBackupContents?> TryReadBlobAsync(ReadOnlyMemory<byte> blob,
                                                      CancellationToken cancellationToken = default)
    {
        var plaintext = _cipher.TryDecrypt(blob.Span);
        if (plaintext is null || plaintext.Length < HeaderLength || !plaintext.AsSpan(0, 4).SequenceEqual(s_magic)
         || plaintext[4] is not (Version or Version1))
            return Task.FromResult<PeerBackupContents?>(null);

        var entryLength = plaintext[4] == Version ? EntryLength : Version1EntryLength;
        var createdAt =
            DateTimeOffset.FromUnixTimeSeconds((long)BinaryPrimitives.ReadUInt64BigEndian(plaintext.AsSpan(5)));
        var count = BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(13));
        if (HeaderLength + count * entryLength > plaintext.Length)
            return Task.FromResult<PeerBackupContents?>(null);

        var channels = new List<PeerBackupChannel>(count);
        var offset = HeaderLength;
        for (var i = 0; i < count; i++)
        {
            try
            {
                channels.Add(ReadEntry(plaintext.AsSpan(offset, entryLength)));
            }
            catch (ArgumentException)
            {
                // Authenticated, so only a bug of ours writes a bad key: keep the rest
            }

            offset += entryLength;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(plaintext.AsSpan(HeaderLength, count * entryLength)));
        return Task.FromResult<PeerBackupContents?>(new PeerBackupContents(createdAt, channels, fingerprint));
    }

    /// <summary>
    /// The blob entry of <paramref name="channel"/>: its current funding outpoint (when known) and our funding key
    /// index of it: the stored funding row's, else the engine's current splice funding (locked in this process), else 0
    /// while the funding key is the key set's (never spliced); unknown when none says.
    /// </summary>
    private async Task<PeerBackupChannel> DescribeAsync(ChannelModel channel, CancellationToken cancellationToken)
    {
        var simpleTaproot = channel.ChannelParams.OptionSimpleTaproot;
        if (channel.FundingOutput is not { TransactionId: { } txId, Index: { } index } funding)
            return new PeerBackupChannel(channel.ChannelId, channel.RemoteNodeId, IsSimpleTaproot: simpleTaproot);

        // The engine's current funding counts only as a splice's: rebuilt from the channel after a restart it always
        // says index 0
        uint? keyIndex = null;
        if (await ReadStoredKeyIndexAsync(channel.ChannelId, txId, cancellationToken) is { } stored)
            keyIndex = stored;
        else if (channel.Commitments?.Params.Funding is { Kind: not ChannelFundingKind.Initial } engine
              && engine.FundingTxId == txId)
            keyIndex = engine.LocalFundingKeyIndex;
        else if (funding.LocalFundingPubKey == channel.LocalKeySet.FundingCompactPubKey)
            keyIndex = 0;

        return new PeerBackupChannel(channel.ChannelId, channel.RemoteNodeId, txId, index, keyIndex, simpleTaproot);
    }

    /// <summary>Our funding key index of the stored current funding row, or null (no store, no row, an error).</summary>
    private async Task<uint?> ReadStoredKeyIndexAsync(ChannelId channelId, TxId fundingTxId,
                                                      CancellationToken cancellationToken)
    {
        if (_serviceScopeFactory is null)
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetService<IUnitOfWork>();
            if (unitOfWork?.ChannelFundingDbRepository is not { } fundings)
                return null;

            return await fundings.GetFundingSetAsync(channelId) is { } set && set.Current.FundingTxId == fundingTxId
                       ? set.Current.LocalFundingKeyIndex
                       : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // No funding table (a test double, NotSupportedException) or a read error: the key's comparison decides
            return null;
        }
    }

    private static void WriteEntry(Span<byte> entry, PeerBackupChannel channel)
    {
        ((ReadOnlySpan<byte>)channel.ChannelId).CopyTo(entry);
        ((ReadOnlySpan<byte>)channel.PeerNodeId).CopyTo(entry[32..]);
        var flags = entry[Version1EntryLength..];
        if (channel is { FundingTxId: { } txId, FundingOutputIndex: { } outputIndex })
        {
            flags[0] = FlagFunding;
            ((ReadOnlySpan<byte>)(byte[])txId).CopyTo(flags[1..]);
            BinaryPrimitives.WriteUInt16BigEndian(flags[(1 + CryptoConstants.Sha256HashLen)..], outputIndex);
        }

        if (channel.LocalFundingKeyIndex is { } keyIndex)
        {
            flags[0] |= FlagKeyIndex;
            BinaryPrimitives.WriteUInt32BigEndian(flags[(1 + CryptoConstants.Sha256HashLen + 2)..], keyIndex);
        }

        if (channel.IsSimpleTaproot)
            flags[0] |= FlagSimpleTaproot;
    }

    private static PeerBackupChannel ReadEntry(ReadOnlySpan<byte> entry)
    {
        var channelId = new ChannelId(entry[..32]);
        var peer = new CompactPubKey(entry.Slice(32, CryptoConstants.CompactPubkeyLen).ToArray());
        if (entry.Length == Version1EntryLength)
            return new PeerBackupChannel(channelId, peer);

        // No conditional with a bare null here: it would convert through TxId's implicit byte[] operator (NL-443)
        var fields = entry[Version1EntryLength..];
        var flags = fields[0];
        TxId? txId = null;
        ushort? outputIndex = null;
        uint? keyIndex = null;
        if ((flags & FlagFunding) != 0)
        {
            txId = new TxId(fields.Slice(1, CryptoConstants.Sha256HashLen).ToArray());
            outputIndex = BinaryPrimitives.ReadUInt16BigEndian(fields[(1 + CryptoConstants.Sha256HashLen)..]);
        }

        if ((flags & FlagKeyIndex) != 0)
            keyIndex = BinaryPrimitives.ReadUInt32BigEndian(fields[(1 + CryptoConstants.Sha256HashLen + 2)..]);

        return new PeerBackupChannel(channelId, peer, txId, outputIndex, keyIndex, (flags & FlagSimpleTaproot) != 0);
    }
}