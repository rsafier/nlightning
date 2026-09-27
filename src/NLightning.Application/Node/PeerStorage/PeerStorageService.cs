using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Node.PeerStorage;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// BOLT 1 peer storage on both sides (see <see cref="IPeerStorageService"/>).
/// </summary>
/// <remarks>
/// <para>
/// Provider: a <c>peer_storage</c> is kept only while we offer <c>option_provide_storage</c>, and only from a peer we
/// have a channel with (in <see cref="IChannelMemoryRepository"/>, i.e. not Closed or Stale) or whose blob we already
/// keep, unless <see cref="PeerStorageOptions.StoreWithoutChannel"/>. The latest blob of every peer is held in memory
/// (loaded from <c>PeerStorageBlobs</c> once, at the first connection) and written to the database at most once per
/// <see cref="PeerStorageOptions.MinStoreInterval"/> (BOLT 1 MAY delay to one update per minute); the delayed write is
/// done by the periodic round or at disposal. Blobs are never deleted, so a closed channel's peer still gets its blob
/// back (BOLT 1: SHOULD wait at least 2016 blocks after the close). <see cref="OnPeerInitialized"/> enqueues
/// <c>peer_storage_retrieval</c> on the connection before it returns, and the peer service calls it before it reports
/// the init exchange done, so it precedes our <c>channel_reestablish</c> (BOLT 1 rationale).
/// </para>
/// <para>
/// Client: a peer that negotiated <c>option_provide_storage</c> gets our blob (<see cref="IPeerBackupBlobProvider"/>)
/// at its first connection of the process, whenever the round (every <see cref="PeerStorageOptions.BackupInterval"/>)
/// finds that what the blob holds changed, and again when the <c>peer_storage_retrieval</c> it sends after init is not
/// the last blob we sent it. A connection with nothing new gets nothing, so the peer's retrieval can be checked against
/// the last blob sent (<see cref="PeerBackupRetrieval.MatchesLastSent"/>). A blob handed back that is not ours is only
/// logged; one of ours naming channels we have no record of is a sign of data loss, logged and kept for the restore
/// flow (<see cref="GetRetrievals"/>).
/// </para>
/// </remarks>
public sealed class PeerStorageService : IPeerStorageService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPeerBackupBlobProvider _blobProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<PeerStorageService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PeerStorageOptions _options;
    private readonly FeatureSupport _offerStorage;

    private readonly ConcurrentDictionary<CompactPubKey, StoredEntry> _stored = new();
    private readonly ConcurrentDictionary<CompactPubKey, IPeerService> _storagePeers = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerBackupBlob> _lastSent = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerBackupRetrieval> _retrievals = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _loadLock = new();
    private readonly Lock _timerLock = new();
    private readonly CancellationTokenSource _stopping = new();

    private volatile bool _loaded;
    private ITimer? _timer;
    private int _roundRunning;
    private volatile bool _disposed;

    public PeerStorageService(IServiceScopeFactory scopeFactory, IPeerBackupBlobProvider blobProvider,
                              IChannelMemoryRepository channelMemoryRepository, IOptions<NodeOptions> nodeOptions,
                              ILogger<PeerStorageService> logger, IOptions<PeerStorageOptions>? options = null,
                              TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _blobProvider = blobProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _options = options?.Value ?? new PeerStorageOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _offerStorage = nodeOptions.Value.Features.OptionProvideStorage;
    }

    /// <summary>
    /// Completes when the round running now (if any) is done; for tests.
    /// </summary>
    internal Task LastRound { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The work started by the last message or connection (a write, a send, a retrieval check); for tests.
    /// </summary>
    internal Task LastWork { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnPeerInitialized(IPeerService peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            EnsureLoaded();
            StartTimer();

            var peerId = peer.PeerPubKey;

            // Provider: hand back what we keep, first thing after init (before channel_reestablish)
            if (_stored.TryGetValue(peerId, out var entry))
            {
                byte[] blob;
                lock (entry)
                    blob = entry.Blob;

                _logger.LogDebug("Sending peer_storage_retrieval ({Length} bytes) to peer {Peer}", blob.Length,
                                 peerId);
                LastWork = SendAsync(peer, new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(blob)));
            }

            // Client: our own blob to a peer that stores it
            if (!_options.SendBackups || peer.Features.OptionProvideStorage == FeatureSupport.No)
                return;

            _storagePeers[peerId] = peer;
            peer.OnDisconnect += (_, _) => _storagePeers.TryRemove(new KeyValuePair<CompactPubKey, IPeerService>(
                                                                       peerId, peer));

            // Only when it holds something else than what we sent it last (or nothing from this process): a peer
            // hands the stored blob back after init, and a new encryption of the same backup would not match it
            LastWork = SendBackupAsync(peer, null, force: false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Peer storage failed at the connection of peer {Peer}", peer.PeerPubKey);
        }
    }

    /// <inheritdoc />
    public void HandleMessage(IPeerService peer, IMessage message)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            switch (message)
            {
                case PeerStorageMessage peerStorage:
                    HandlePeerStorage(peer.PeerPubKey, peerStorage.Payload.Blob.ToArray());
                    break;
                case PeerStorageRetrievalMessage retrieval:
                    LastWork = HandleRetrievalAsync(peer.PeerPubKey, retrieval.Payload.Blob.ToArray());
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Peer storage failed to handle {MessageType} from peer {Peer}",
                             Enum.GetName(message.Type), peer.PeerPubKey);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerBackupRetrieval> GetRetrievals() =>
        _retrievals.Values.OrderBy(r => r.ReceivedAt).ToList();

    /// <inheritdoc />
    public Task<StoredPeerBlob?> GetStoredBlobAsync(CompactPubKey peerNodeId)
    {
        EnsureLoaded();
        if (!_stored.TryGetValue(peerNodeId, out var entry))
            return Task.FromResult<StoredPeerBlob?>(null);

        lock (entry)
            return Task.FromResult<StoredPeerBlob?>(new StoredPeerBlob(peerNodeId, entry.Blob, entry.ReceivedAt));
    }

    /// <summary>
    /// One round: the delayed writes that are due, then our blob to every peer that stores it when what it holds
    /// changed. Rounds never overlap.
    /// </summary>
    internal Task RunRoundAsync()
    {
        if (Interlocked.CompareExchange(ref _roundRunning, 1, 0) != 0)
            return LastRound;

        return LastRound = RunRoundCoreAsync();
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer?.Dispose();
        }

        _stopping.Cancel();

        // Best effort: the blobs whose write was delayed. When the container disposes this singleton it can no longer
        // create scopes; the latest blob of a peer is then lost if it came less than MinStoreInterval before the stop
        // (BOLT 1 allows the delay), and the peer's next peer_storage replaces it anyway
        try
        {
            FlushAsync(force: true).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write the delayed peer storage blobs at shutdown");
        }
    }

    private void HandlePeerStorage(CompactPubKey peerId, byte[] blob)
    {
        if (_offerStorage == FeatureSupport.No)
        {
            _logger.LogDebug("Ignoring peer_storage from peer {Peer}: we do not offer option_provide_storage", peerId);
            return;
        }

        EnsureLoaded();
        if (!_stored.ContainsKey(peerId) && !_options.StoreWithoutChannel && !HasChannelWith(peerId))
        {
            _logger.LogDebug("Ignoring peer_storage from peer {Peer}: no channel with it", peerId);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var entry = _stored.GetOrAdd(peerId, _ => new StoredEntry());
        bool writeNow;
        lock (entry)
        {
            if (entry.Blob.AsSpan().SequenceEqual(blob) && (entry.Dirty || entry.WrittenAt is not null))
            {
                // The same blob again (peers send it on every connection): nothing to write
                _logger.LogDebug("Peer {Peer} sent the peer_storage blob we already keep", peerId);
                return;
            }

            entry.Blob = blob;
            entry.ReceivedAt = now;
            entry.Dirty = true;
            writeNow = entry.WrittenAt is null || now - entry.WrittenAt.Value >= _options.MinStoreInterval;
        }

        _logger.LogDebug("Keeping the peer_storage blob ({Length} bytes) of peer {Peer}{Delay}", blob.Length, peerId,
                         writeNow ? string.Empty : " (write delayed)");
        if (writeNow)
            LastWork = WriteAsync(peerId, entry, now);
    }

    private bool HasChannelWith(CompactPubKey peerId) =>
        _channelMemoryRepository.FindChannels(c => c.RemoteNodeId.Equals(peerId)
                                                && c.State is not (ChannelState.Closed or ChannelState.Stale))
                                .Count > 0;

    private async Task HandleRetrievalAsync(CompactPubKey peerId, byte[] blob)
    {
        try
        {
            var contents = await _blobProvider.TryReadBlobAsync(blob, _stopping.Token);
            bool? matchesLastSent = _lastSent.TryGetValue(peerId, out var lastSent)
                                        ? lastSent.Blob.AsSpan().SequenceEqual(blob)
                                        : null;

            var unknownChannels = new List<PeerBackupChannel>();
            if (contents is not null && contents.Channels.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                foreach (var channel in contents.Channels)
                    if (!await unitOfWork.ChannelDbRepository.ExistsAsync(channel.ChannelId))
                        unknownChannels.Add(channel);
            }

            _retrievals[peerId] = new PeerBackupRetrieval(peerId, _timeProvider.GetUtcNow(), blob.Length, contents,
                                                          matchesLastSent, unknownChannels);

            // The peer lost (or never stored) what we sent it last: send the current backup again (a blob that holds
            // the same backup, e.g. the one sent before our restart, is kept)
            if (matchesLastSent == false && contents?.Fingerprint != lastSent?.Fingerprint
             && _storagePeers.TryGetValue(peerId, out var storagePeer))
                await SendBackupAsync(storagePeer, null, force: true);

            if (contents is null)
            {
                _logger.LogInformation(
                    "Peer {Peer} handed back a peer storage blob ({Length} bytes) that is not one of ours", peerId,
                    blob.Length);
            }
            else if (unknownChannels.Count > 0)
            {
                _logger.LogWarning(
                    "Peer {Peer} holds a backup of ours from {CreatedAt} naming {Count} channel(s) we have no record "
                  + "of ({ChannelIds}): possible data loss, restore from it", peerId, contents.CreatedAt,
                    unknownChannels.Count, string.Join(", ", unknownChannels.Select(c => c.ChannelId)));
            }
            else
            {
                _logger.LogInformation(
                    "Peer {Peer} handed back our backup from {CreatedAt} ({Count} channel(s), matches the last one "
                  + "sent: {Matches})", peerId, contents.CreatedAt, contents.Channels.Count,
                    matchesLastSent?.ToString() ?? "none sent yet");
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to read the peer_storage_retrieval of peer {Peer}", peerId);
        }
    }

    private async Task SendBackupAsync(IPeerService peer, PeerBackupBlob? blob, bool force)
    {
        try
        {
            blob ??= await _blobProvider.CreateBlobAsync(_stopping.Token);
            if (blob is null)
                return;

            var peerId = peer.PeerPubKey;
            if (!force && _lastSent.TryGetValue(peerId, out var last) && last.Fingerprint == blob.Fingerprint)
                return;

            await peer.SendPeerStorageMessageAsync(new PeerStorageMessage(new PeerStoragePayload(blob.Blob)));
            _lastSent[peerId] = blob;
            _logger.LogDebug("Sent our peer_storage backup ({Length} bytes) to peer {Peer}", blob.Blob.Length,
                             peerId);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to send our peer_storage backup to peer {Peer}", peer.PeerPubKey);
        }
    }

    private async Task SendAsync(IPeerService peer, IMessage message)
    {
        try
        {
            await peer.SendPeerStorageMessageAsync(message);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to send {MessageType} to peer {Peer}", Enum.GetName(message.Type),
                               peer.PeerPubKey);
        }
    }

    private async Task RunRoundCoreAsync()
    {
        try
        {
            await FlushAsync(force: false);

            if (_storagePeers.IsEmpty || !_options.SendBackups)
                return;

            var blob = await _blobProvider.CreateBlobAsync(_stopping.Token);
            if (blob is null)
                return;

            foreach (var peer in _storagePeers.Values)
                await SendBackupAsync(peer, blob, force: false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Peer storage round failed");
        }
        finally
        {
            Interlocked.Exchange(ref _roundRunning, 0);
        }
    }

    private async Task FlushAsync(bool force)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (peerId, entry) in _stored)
        {
            bool due;
            lock (entry)
                due = entry.Dirty
                   && (force || entry.WrittenAt is null || now - entry.WrittenAt.Value >= _options.MinStoreInterval);

            if (due)
                await WriteAsync(peerId, entry, now);
        }
    }

    private async Task WriteAsync(CompactPubKey peerId, StoredEntry entry, DateTimeOffset now)
    {
        await _writeLock.WaitAsync();
        try
        {
            byte[] blob;
            DateTimeOffset receivedAt;
            lock (entry)
            {
                if (!entry.Dirty)
                    return;

                blob = entry.Blob;
                receivedAt = entry.ReceivedAt;
                entry.Dirty = false;
                entry.WrittenAt = now;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.PeerStorageDbRepository.UpsertAsync(new StoredPeerBlob(peerId, blob, receivedAt));
                await unitOfWork.SaveChangesAsync();
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                _logger.LogWarning("Could not store the latest peer_storage blob of peer {Peer} at shutdown", peerId);
            }
            catch (Exception e)
            {
                lock (entry)
                    if (ReferenceEquals(entry.Blob, blob))
                        entry.Dirty = true;

                _logger.LogError(e, "Failed to store the peer_storage blob of peer {Peer}; retrying in the next round",
                                 peerId);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (_loadLock)
        {
            if (_loaded)
                return;

            try
            {
                // Once, at the first connection (a small table): the retrieval must go out before this returns
                using var scope = _scopeFactory.CreateScope();
                using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var rows = unitOfWork.PeerStorageDbRepository.GetAllAsync().GetAwaiter().GetResult();
                foreach (var row in rows)
                    _stored.TryAdd(row.PeerNodeId, new StoredEntry
                    {
                        Blob = row.Blob,
                        ReceivedAt = row.UpdatedAt,
                        WrittenAt = row.UpdatedAt
                    });

                _logger.LogDebug("Loaded {Count} peer storage blob(s)", rows.Count);
                _loaded = true;
            }
            catch (Exception e)
            {
                // Retried at the next connection or message: marking it loaded would lose the stored blobs until the
                // restart (no retrieval for their peers, a channel-less peer's next blob refused). A blob received in
                // between is newer than the stored one and is kept (TryAdd)
                _logger.LogError(e, "Failed to load the stored peer storage blobs; retrying at the next connection");
            }
        }
    }

    private void StartTimer()
    {
        lock (_timerLock)
        {
            if (_timer is not null || _disposed)
                return;

            var interval = _options.BackupInterval > TimeSpan.Zero ? _options.BackupInterval : TimeSpan.FromMinutes(1);
            _timer = _timeProvider.CreateTimer(_ => _ = RunRoundAsync(), null, interval, interval);
        }
    }

    private sealed class StoredEntry
    {
        public byte[] Blob { get; set; } = [];
        public DateTimeOffset ReceivedAt { get; set; }
        public DateTimeOffset? WrittenAt { get; set; }
        public bool Dirty { get; set; }
    }
}