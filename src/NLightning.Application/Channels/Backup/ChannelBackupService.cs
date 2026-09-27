using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Interfaces;
using Models;

/// <summary>
/// Builds, encrypts, verifies and writes static channel backups (see <see cref="IChannelBackupService"/>).
/// </summary>
/// <remarks>
/// The file is only replaced when its channels changed, and never by an empty backup while the database holds no
/// channel row at all (a wiped or new database: the existing file is what <c>restorechanbackup</c> needs). A file
/// that does not decrypt with our key is moved aside, not overwritten.
/// </remarks>
public sealed class ChannelBackupService : IChannelBackupService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ILightningSigner _signer;
    private readonly NodeOptions _nodeOptions;
    private readonly ChannelBackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelBackupService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private byte[]? _lastWrittenContent;
    private bool _keptExistingLogged;

    public ChannelBackupService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                                ILightningSigner signer, IOptions<NodeOptions> nodeOptions,
                                IOptions<ChannelBackupOptions>? options = null, TimeProvider? timeProvider = null,
                                ILogger<ChannelBackupService>? logger = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _signer = signer;
        _nodeOptions = nodeOptions.Value;
        _options = options?.Value ?? new ChannelBackupOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ChannelBackupService>.Instance;
    }

    /// <summary>Whether <paramref name="channel"/> belongs in a backup: funding outpoint and the peer's keys known,
    /// not Closed or Stale.</summary>
    public static bool IsBackedUp(ChannelModel channel) =>
        channel.State is not (ChannelState.None or ChannelState.Closed or ChannelState.Stale)
     && channel.FundingOutput is { TransactionId: not null, Index: not null }
     && channel.RemoteKeySet is not null;

    /// <inheritdoc />
    public async Task<ChannelBackupSnapshot> CreateSnapshotAsync(ChannelId? channelId,
                                                                 CancellationToken cancellationToken)
    {
        var (snapshot, _) = await LoadAsync(channelId, cancellationToken);
        if (channelId is { } only && snapshot.Channels.Count == 0)
            throw new KeyNotFoundException($"Channel {only} is not backed up: unknown, closed, or before its "
                                         + "funding outpoint.");

        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ChannelBackupExport> ExportAsync(ChannelId? channelId, CancellationToken cancellationToken)
    {
        var snapshot = await CreateSnapshotAsync(channelId, cancellationToken);
        return new ChannelBackupExport(snapshot, Encrypt(snapshot));
    }

    /// <inheritdoc />
    public ChannelBackupSnapshot Decrypt(ReadOnlySpan<byte> backup)
    {
        var key = DeriveKey();
        try
        {
            return ChannelBackupCodec.Decode(ChannelBackupCipher.Decrypt(key, backup));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public async Task<ChannelBackupVerification> VerifyAsync(ReadOnlyMemory<byte> backup,
                                                             CancellationToken cancellationToken)
    {
        ChannelBackupSnapshot snapshot;
        try
        {
            snapshot = Decrypt(backup.Span);
        }
        catch (ChannelBackupException e)
        {
            return new ChannelBackupVerification(false, e.Message, null, []);
        }

        if (snapshot.ChainHash != _nodeOptions.BitcoinNetwork.ChainHash)
            return new ChannelBackupVerification(false, "The backup is for another chain.", snapshot, []);

        if (snapshot.NodeId != _secureKeyManager.GetNodePubKey())
            return new ChannelBackupVerification(false, "The backup was made by another node.", snapshot, []);

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var checks = new List<ChannelBackupEntryCheck>(snapshot.Channels.Count);
        foreach (var entry in snapshot.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var basepoints = _signer.GetChannelBasepoints(entry.KeyIndex);
            var keysMatch = basepoints.FundingPubKey == entry.LocalFundingPubKey
                         && basepoints.PaymentBasepoint == entry.LocalPaymentBasepoint;
            checks.Add(new ChannelBackupEntryCheck(entry, keysMatch,
                                                   await GetLocalStateAsync(unitOfWork, entry.ChannelId)));
        }

        var mismatches = checks.Where(c => !c.KeysMatch).ToList();
        var error = mismatches.Count == 0
                        ? null
                        : $"The keys of {mismatches.Count} channel(s) do not derive from this node's key file "
                        + $"({string.Join(", ", mismatches.Select(c => c.Entry.ChannelId))}).";
        return new ChannelBackupVerification(error is null, error, snapshot, checks);
    }

    /// <inheritdoc />
    public async Task<ChannelBackupWriteResult> WriteFileAsync(CancellationToken cancellationToken)
    {
        var path = _options.FilePath;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(path))
            return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Disabled, 0, null);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var (snapshot, channelRows) = await LoadAsync(null, cancellationToken);
            var content = CanonicalContent(snapshot);
            if (_lastWrittenContent is not null && content.AsSpan().SequenceEqual(_lastWrittenContent))
                return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Unchanged, snapshot.Channels.Count,
                                                    path);

            string? movedAside = null;
            if (File.Exists(path))
            {
                ChannelBackupSnapshot? existing = null;
                try
                {
                    existing = Decrypt(await File.ReadAllBytesAsync(path, cancellationToken));
                }
                catch (ChannelBackupException e)
                {
                    movedAside = ChannelBackupFile.MoveAside(path, _timeProvider.GetUtcNow());
                    _logger.LogWarning("The channel backup {Path} does not decrypt with this node's key ({Reason}); "
                                     + "moved to {MovedTo}", path, e.Message, movedAside);
                }

                if (existing is not null)
                {
                    if (channelRows == 0 && existing.Channels.Count > 0)
                    {
                        if (!_keptExistingLogged)
                        {
                            _keptExistingLogged = true;
                            _logger.LogWarning("The database holds no channel but the channel backup {Path} holds {Count}"
                                             + ": the file is kept (restore it with restorechanbackup)", path,
                                               existing.Channels.Count);
                        }

                        return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.KeptExisting,
                                                            existing.Channels.Count, path);
                    }

                    if (CanonicalContent(existing).AsSpan().SequenceEqual(content))
                    {
                        _lastWrittenContent = content;
                        return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Unchanged,
                                                            snapshot.Channels.Count, path);
                    }
                }
            }

            await ChannelBackupFile.WriteAtomicallyAsync(path, Encrypt(snapshot), cancellationToken);
            _lastWrittenContent = content;
            _keptExistingLogged = false;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Channel backup {Path} written with {Count} channel(s)", path,
                                       snapshot.Channels.Count);

            return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Written, snapshot.Channels.Count, path,
                                                movedAside);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(ChannelBackupSnapshot Snapshot, int ChannelRows)> LoadAsync(
        ChannelId? channelId, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var channels = (await unitOfWork.ChannelDbRepository.GetAllAsync()).ToList();
        var entries = new List<ChannelBackupEntry>();
        var peers = new Dictionary<Domain.Crypto.ValueObjects.CompactPubKey, PeerModel?>();
        foreach (var channel in channels.Where(IsBackedUp).OrderBy(c => c.ChannelId.ToString()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channelId is { } only && channel.ChannelId != only)
                continue;

            if (!peers.TryGetValue(channel.RemoteNodeId, out var peer))
            {
                peer = await unitOfWork.PeerDbRepository.GetByNodeIdAsync(channel.RemoteNodeId);
                peers[channel.RemoteNodeId] = peer;
            }

            entries.Add(CreateEntry(channel, peer));
        }

        var createdAt = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var snapshot = new ChannelBackupSnapshot(_nodeOptions.BitcoinNetwork.ChainHash,
                                                 _secureKeyManager.GetNodePubKey(), createdAt, entries);
        return (snapshot, channels.Count);
    }

    internal static ChannelBackupEntry CreateEntry(ChannelModel channel, PeerModel? peer)
    {
        var funding = channel.FundingOutput!;
        var remote = channel.RemoteKeySet!;
        var addresses = new List<ChannelBackupAddress>();
        if (peer is not null && !string.IsNullOrWhiteSpace(peer.Host) && peer.Port is > 0 and <= ushort.MaxValue)
            addresses.Add(new ChannelBackupAddress(peer.Type, peer.Host, (ushort)peer.Port));

        return new ChannelBackupEntry
        {
            ChannelId = channel.ChannelId,
            RemoteNodeId = channel.RemoteNodeId,
            Addresses = addresses,
            FundingTxId = funding.TransactionId!.Value,
            FundingOutputIndex = funding.Index!.Value,
            CapacitySat = (ulong)funding.Amount.Satoshi,
            FundingHeight = channel.FundingCreatedAtBlockHeight,
            ShortChannelId = HasShortChannelId(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null,
            IsInitiator = channel.IsInitiator,
            OptionAnchorOutputs = channel.ChannelParams.OptionAnchorOutputs,
            AnnounceChannel = channel.ChannelParams.AnnounceChannel,
            HasInferredParams = channel.ChannelParams.HasInferredParams,
            Version = channel.Version,
            UseScidAlias = channel.ChannelParams.UseScidAlias,
            MinimumDepth = channel.ChannelParams.MinimumDepth,
            ChannelType = channel.ChannelParams.ToChannelType().GetWireBytes() ?? [],
            KeyIndex = channel.LocalKeySet.KeyIndex,
            LocalFundingPubKey = channel.LocalKeySet.FundingCompactPubKey,
            LocalPaymentBasepoint = channel.LocalKeySet.PaymentCompactBasepoint,
            RemoteFundingPubKey = remote.FundingCompactPubKey,
            RemoteRevocationBasepoint = remote.RevocationCompactBasepoint,
            RemotePaymentBasepoint = remote.PaymentCompactBasepoint,
            RemoteDelayedPaymentBasepoint = remote.DelayedPaymentCompactBasepoint,
            RemoteHtlcBasepoint = remote.HtlcCompactBasepoint,
            Local = ChannelBackupParty.From(channel.ChannelParams.Local),
            Remote = ChannelBackupParty.From(channel.ChannelParams.Remote)
        };
    }

    private static bool HasShortChannelId(ShortChannelId shortChannelId)
    {
        var bytes = (byte[])shortChannelId;
        return bytes is not null && bytes.Any(b => b != 0);
    }

    private static async Task<ChannelState?> GetLocalStateAsync(IUnitOfWork unitOfWork, ChannelId channelId)
    {
        try
        {
            return (await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId))?.State;
        }
        catch (Exception)
        {
            // A row the repository refuses (legacy HTLC state) still exists
            return null;
        }
    }

    /// <summary>The plaintext with the creation time left out, to compare two backups' channels.</summary>
    private static byte[] CanonicalContent(ChannelBackupSnapshot snapshot) =>
        ChannelBackupCodec.Encode(snapshot with { CreatedAt = DateTimeOffset.UnixEpoch });

    private byte[] Encrypt(ChannelBackupSnapshot snapshot)
    {
        var key = DeriveKey();
        try
        {
            return ChannelBackupCipher.Encrypt(key, ChannelBackupCodec.Encode(snapshot));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DeriveKey()
    {
        var keyPair = _secureKeyManager.GetNodeKeyPair();
        var privateKey = (byte[])keyPair.PrivKey;
        try
        {
            return ChannelBackupCipher.DeriveKey(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}