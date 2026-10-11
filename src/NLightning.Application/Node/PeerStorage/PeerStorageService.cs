using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Node.PeerStorage;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Fencing;
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
/// Client: a peer that offers <c>option_provide_storage</c> gets our blob (<see cref="IPeerBackupBlobProvider"/>) —
/// BOLT 1 lets a node send <c>peer_storage</c> to any peer that offers the feature, so the peer's own advertisement
/// decides and our advertisement only gates the provider side (NL-433) — at its first connection of the process,
/// whenever the round (every <see cref="PeerStorageOptions.BackupInterval"/>) finds that what the blob holds changed,
/// and again when the <c>peer_storage_retrieval</c> it sends after init is not the last blob we sent it. A connection
/// with nothing new gets nothing, so the peer's retrieval can be checked against the last blob sent
/// (<see cref="PeerBackupRetrieval.MatchesLastSent"/>). A blob handed back that is not ours is only logged; one of
/// ours naming channels we have no record of is a sign of data loss, logged and kept for the restore flow
/// (<see cref="GetRetrievals"/>). A peer that answers our blob with a <c>warning</c> refusing its size (e.g. LDK,
/// which takes at most 1,024 bytes) is answered at once with one that fits the byte limit the warning names, and the
/// refusal is counted (<see cref="GetRefusals"/>, NL-559). Only an unambiguous size refusal moves the learned limit or
/// the count (NL-563): the warning must name a byte limit ("<c>up to 1024 bytes</c>"). A peer-storage warning without
/// one — LDK's "peer storage is currently supported only for peers with an active funded channel", sent before any
/// channel with it is funded — is informational and changes nothing; such a peer keeps nothing either way, and once a
/// channel is funded the changed backup goes out with the next round. The limit lives in memory only, so a restart
/// first offers the full-size blob again and relearns it from the next refusal.
/// </para>
/// <para>
/// The peer's copy is the evidence of a data loss, so it is never overwritten before we read it: at the first
/// connection of the process our backup waits for the peer's <c>peer_storage_retrieval</c> (at most
/// <see cref="PeerStorageOptions.RetrievalWait"/>), and once a retrieval names channels we do not know, no backup goes
/// to any peer until the restart (<see cref="BackupsHeldForDataLoss"/>). The next process reads the same copy again, so
/// the evidence survives restarts on the peer. Only retrievals of peers we have a channel with or sent a backup to, or
/// that hold a blob of ours, are recorded. The latest retrieval of each peer is also written to
/// <c>PeerStorageRetrievals</c> (NL-432) before any backup goes back to the peer, so the operator can read it after a
/// restart (<see cref="ListRetrievalsAsync"/>, <c>listpeerstorage</c>); a failed write is retried at every round.
/// </para>
/// </remarks>
public sealed partial class PeerStorageService : IPeerStorageService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPeerBackupBlobProvider _blobProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<PeerStorageService> _logger;

    // Asked before our backup goes to a peer (NL-1341): a fenced instance must not replace the peer's copy with an
    // older one
    private readonly INodeWriteFence? _writeFence;
    private readonly TimeProvider _timeProvider;
    private readonly PeerStorageOptions _options;
    private readonly FeatureSupport _offerStorage;

    private readonly ConcurrentDictionary<CompactPubKey, StoredEntry> _stored = new();
    private readonly ConcurrentDictionary<CompactPubKey, IPeerService> _storagePeers = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerBackupBlob> _lastSent = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerBackupRetrieval> _retrievals = new();
    private readonly ConcurrentDictionary<CompactPubKey, TaskCompletionSource> _awaitingRetrieval = new();
    private readonly ConcurrentDictionary<CompactPubKey, PendingBlob> _pendingWithoutChannel = new();
    private readonly ConcurrentDictionary<CompactPubKey, StoredPeerRetrieval> _unwrittenRetrievals = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerRefusal> _refusals = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _loadLock = new();
    private readonly Lock _timerLock = new();
    private readonly CancellationTokenSource _stopping = new();

    private volatile bool _loaded;
    private ITimer? _timer;
    private int _roundRunning;
    private volatile bool _disposed;
    private volatile bool _backupsHeld;

    /// <summary>At most this many peers' blobs are held while no channel with the peer exists (64 KiB each).</summary>
    internal const int MaxPendingWithoutChannel = 64;

    /// <summary>A warning is about peer storage only when it says so (LDK: "… bytes in peer storage.").</summary>
    [GeneratedRegex(@"peer\s*storage", RegexOptions.IgnoreCase)]
    private static partial Regex PeerStorageTopicRegex();

    /// <summary>
    /// What makes a peer-storage warning an unambiguous size refusal (NL-563): it names the limit itself
    /// ("<c>up to 1024 bytes</c>"); thousand separators are stripped first. A peer-storage warning that names no
    /// byte count (e.g. LDK's "supported only for peers with an active funded channel") is informational.
    /// </summary>
    [GeneratedRegex(@"(\d+)\s*bytes", RegexOptions.IgnoreCase)]
    private static partial Regex ByteLimitRegex();

    /// <summary>A held blob is forgotten when no channel with its peer appeared within this time.</summary>
    internal static readonly TimeSpan PendingWithoutChannelLifetime = TimeSpan.FromMinutes(30);

    public PeerStorageService(IServiceScopeFactory scopeFactory, IPeerBackupBlobProvider blobProvider,
                              IChannelMemoryRepository channelMemoryRepository, IOptions<NodeOptions> nodeOptions,
                              ILogger<PeerStorageService> logger, IOptions<PeerStorageOptions>? options = null,
                              TimeProvider? timeProvider = null, INodeWriteFence? writeFence = null)
    {
        _writeFence = writeFence;
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
    public bool BackupsHeldForDataLoss => _backupsHeld;

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

            // Client: our own blob to a peer that stores it. BOLT 1 lets a node send peer_storage to any peer that
            // offers option_provide_storage, whether or not we store blobs ourselves (NL-433), so the peer's own
            // advertisement decides here — the negotiated set would also fold our advertisement in
            if (!_options.SendBackups || peer.PeerFeatures.OptionProvideStorage == FeatureSupport.No)
                return;

            _storagePeers[peerId] = peer;
            peer.OnDisconnect += (_, _) => _storagePeers.TryRemove(new KeyValuePair<CompactPubKey, IPeerService>(
                                                                       peerId, peer));

            // Only when it holds something else than what we sent it last (or nothing from this process): a peer
            // hands the stored blob back after init, and a new encryption of the same backup would not match it.
            // Nothing sent to it yet by this process: what it keeps may be the only proof of a data loss, so its
            // retrieval is read first
            LastWork = _lastSent.ContainsKey(peerId) || _options.RetrievalWait <= TimeSpan.Zero
                           ? SendBackupAsync(peer, force: false)
                           : SendBackupAfterRetrievalAsync(peer);
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
    public void HandleWarning(IPeerService peer, string message)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            if (string.IsNullOrEmpty(message) || !PeerStorageTopicRegex().IsMatch(message))
                return;

            // Only a warning that names the limit it accepts is an unambiguous size refusal (NL-563): LDK answers
            // a blob it will not even look at because no channel is funded yet with a peer-storage warning that
            // names no byte count, and that must not move the learned limit or the refusal count
            var match = ByteLimitRegex().Match(message.Replace(",", string.Empty));
            if (match.Success
             && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var named))
            {
                RecordSizeRefusal(peer, named);
                return;
            }

            _logger.LogInformation(
                "Peer {Peer} sent a peer storage warning that names no size limit, so it is not taken as a refusal "
              + "of our backup's length: {Message}", peer.PeerPubKey, message);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Peer storage failed to handle a warning of peer {Peer}", peer.PeerPubKey);
        }
    }

    /// <summary>
    /// Records the size refusal of a peer-storage warning that names its byte limit (NL-559), and answers it at once
    /// with a backup that fits when what we last sent does not.
    /// </summary>
    private void RecordSizeRefusal(IPeerService peer, int named)
    {
        var limit = Math.Clamp(named, 1, PeerStorageConstants.MaxBlobLength - 1);

        var peerId = peer.PeerPubKey;
        var refusedLength = _lastSent.TryGetValue(peerId, out var last) ? last.Blob.Length : 0;
        var now = _timeProvider.GetUtcNow();
        var refusal = _refusals.AddOrUpdate(
            peerId,
            _ => new PeerRefusal(limit, refusedLength, now, 1),
            (_, existing) => existing with
            {
                // A later refusal can only lower the limit: a peer that grew it would keep refusing nothing
                AcceptedLimitBytes = Math.Min(existing.AcceptedLimitBytes, limit),
                LastRefusedBlobLength = refusedLength,
                LastRefusalAt = now,
                Count = existing.Count + 1
            });

        _logger.LogInformation(
            "Peer {Peer} refused our peer_storage backup ({Refused} bytes): it takes at most {Limit}; the next backup to it fits that",
            peerId, refusal.LastRefusedBlobLength, refusal.AcceptedLimitBytes);

        // What we last sent it does not fit: answer on this connection with one that does (a blob that fits is
        // not resent — nothing is sent when even one channel does not fit the limit)
        if (refusal.LastRefusedBlobLength > refusal.AcceptedLimitBytes
         && _storagePeers.TryGetValue(peerId, out var current) && ReferenceEquals(current, peer))
            LastWork = SendBackupAsync(peer, force: true);
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerStorageRefusalReport> GetRefusals() =>
        _refusals.Select(pair => new PeerStorageRefusalReport(pair.Key, pair.Value.Count,
                                                              pair.Value.AcceptedLimitBytes,
                                                              pair.Value.LastRefusedBlobLength,
                                                              pair.Value.LastRefusalAt))
                 .OrderBy(r => r.LastRefusalAt)
                 .ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeerStorageRetrievalReport>> ListRetrievalsAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var rows = (await unitOfWork.PeerStorageRetrievalDbRepository.GetAllAsync())
                  .ToDictionary(r => r.PeerNodeId, r => (Retrieval: r, Persisted: true));
        foreach (var (peerId, unwritten) in _unwrittenRetrievals)
            if (!rows.TryGetValue(peerId, out var row) || row.Retrieval.ReceivedAt <= unwritten.ReceivedAt)
                rows[peerId] = (unwritten, false);

        var reports = new List<PeerStorageRetrievalReport>(rows.Count);
        foreach (var (retrieval, persisted) in rows.Values.OrderBy(r => r.Retrieval.ReceivedAt))
        {
            var contents = await _blobProvider.TryReadBlobAsync(retrieval.Blob, cancellationToken);
            var unknownWhenReceived = retrieval.UnknownChannelIds.ToHashSet();
            var channels = new List<PeerBackupChannelStatus>();
            foreach (var channel in contents?.Channels ?? [])
                channels.Add(new PeerBackupChannelStatus(
                                 channel.ChannelId, channel.PeerNodeId, unknownWhenReceived.Contains(channel.ChannelId),
                                 await unitOfWork.ChannelDbRepository.ExistsAsync(channel.ChannelId)));

            reports.Add(new PeerStorageRetrievalReport(retrieval.PeerNodeId, retrieval.ReceivedAt, retrieval.Blob,
                                                       contents, retrieval.MatchesLastSent, channels, persisted));
        }

        return reports;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredPeerBlob>> ListStoredBlobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var blobs = new List<StoredPeerBlob>(_stored.Count);
        foreach (var (peerId, entry) in _stored)
            lock (entry)
                blobs.Add(new StoredPeerBlob(peerId, entry.Blob, entry.ReceivedAt));

        return Task.FromResult<IReadOnlyList<StoredPeerBlob>>(
            blobs.OrderBy(b => Convert.ToHexString(b.PeerNodeId)).ToList());
    }

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

    /// <inheritdoc />
    public async Task StopAsync()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            _stopping.Cancel();
        }

        try
        {
            await LastRound;
            await FlushAsync(force: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write the delayed peer storage blobs at shutdown");
        }
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
            // A peer sends its blob right after init, often before the channel we are about to open with it exists:
            // keep the latest one in memory (bounded) and adopt it at a round once a channel with the peer exists
            var pending = new PendingBlob(blob, _timeProvider.GetUtcNow());
            if (_pendingWithoutChannel.ContainsKey(peerId) || _pendingWithoutChannel.Count < MaxPendingWithoutChannel)
            {
                _pendingWithoutChannel[peerId] = pending;
                _logger.LogDebug("Holding peer_storage from peer {Peer} until a channel with it exists", peerId);
            }
            else
            {
                _logger.LogDebug("Ignoring peer_storage from peer {Peer}: no channel with it", peerId);
            }

            return;
        }

        _pendingWithoutChannel.TryRemove(peerId, out _);

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

            // Any node id can send this: keep a record only of peers we deal with (bounded by our channels and the
            // peers we sent a backup to) or of a blob only we can have made
            if (contents is null && lastSent is null && !_storagePeers.ContainsKey(peerId) && !HasChannelWith(peerId))
            {
                _logger.LogDebug(
                    "Ignoring peer_storage_retrieval ({Length} bytes) from peer {Peer}: no channel with it and no "
                  + "backup of ours sent to it", blob.Length, peerId);
                return;
            }

            var unknownChannels = new List<PeerBackupChannel>();
            if (contents is not null && contents.Channels.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                foreach (var channel in contents.Channels)
                    if (!await unitOfWork.ChannelDbRepository.ExistsAsync(channel.ChannelId))
                        unknownChannels.Add(channel);
            }

            // Before anything can be sent: the peer's copy proves the data loss and must not be overwritten
            if (unknownChannels.Count > 0)
                _backupsHeld = true;

            var receivedAt = _timeProvider.GetUtcNow();
            _retrievals[peerId] = new PeerBackupRetrieval(peerId, receivedAt, blob.Length, contents, matchesLastSent,
                                                          unknownChannels);

            // Kept across restarts (NL-432), before our backup can replace the peer's copy
            await WriteRetrievalAsync(new StoredPeerRetrieval(peerId, receivedAt, blob, matchesLastSent,
                                                              unknownChannels.Select(c => c.ChannelId).ToList()));

            // The peer lost (or never stored) what we sent it last: send the current backup again (a blob that holds
            // the same backup, e.g. the one sent before our restart, is kept). A peer not registered yet (its
            // retrieval was handled before our init hook ran) gets it at its next connection or round
            if (matchesLastSent == false && contents?.Fingerprint != lastSent?.Fingerprint)
            {
                if (_storagePeers.TryGetValue(peerId, out var storagePeer))
                    await SendBackupAsync(storagePeer, force: true);
                else
                    _lastSent.TryRemove(new KeyValuePair<CompactPubKey, PeerBackupBlob>(peerId, lastSent!));
            }

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
        finally
        {
            // Our backup waiting for this retrieval goes now (or stays held)
            if (_awaitingRetrieval.TryGetValue(peerId, out var waiting))
                waiting.TrySetResult();
        }
    }

    private async Task SendBackupAfterRetrievalAsync(IPeerService peer)
    {
        var peerId = peer.PeerPubKey;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaitingRetrieval[peerId] = waiting;
        try
        {
            // A peer that keeps nothing of ours sends no retrieval: the wait then ends at the timeout
            await Task.WhenAny(waiting.Task, Task.Delay(_options.RetrievalWait, _timeProvider, _stopping.Token));
            if (_stopping.IsCancellationRequested)
                return;

            // Only on the connection that asked (a newer one runs its own wait)
            if (!_storagePeers.TryGetValue(peerId, out var current) || !ReferenceEquals(current, peer))
                return;

            await SendBackupAsync(peer, force: false);
        }
        finally
        {
            _awaitingRetrieval.TryRemove(new KeyValuePair<CompactPubKey, TaskCompletionSource>(peerId, waiting));
        }
    }

    private async Task SendBackupAsync(IPeerService peer, bool force)
    {
        try
        {
            if (_backupsHeld)
            {
                _logger.LogDebug("Not sending our peer_storage backup to peer {Peer}: held after a possible data loss",
                                 peer.PeerPubKey);
                return;
            }

            var peerId = peer.PeerPubKey;
            var maxBlobLength = _refusals.TryGetValue(peerId, out var refusal)
                                    ? refusal.AcceptedLimitBytes
                                    : PeerStorageConstants.MaxBlobLength;
            var blob = await _blobProvider.CreateBlobAsync(maxBlobLength, _stopping.Token);
            if (blob is null)
            {
                if (maxBlobLength < PeerStorageConstants.MaxBlobLength)
                    _logger.LogWarning(
                        "Our peer_storage backup names more channels than fit the {Limit} bytes peer {Peer} accepts: it keeps nothing of ours",
                        maxBlobLength, peerId);
                return;
            }

            if (!force && _lastSent.TryGetValue(peerId, out var last) && last.Fingerprint == blob.Fingerprint)
                return;

            if (_writeFence is not null)
                await _writeFence.CheckEffectAsync(NodeEffect.PeerSend, _stopping.Token);

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
            AdoptPendingBlobs();
            await FlushAsync(force: false);

            if (_storagePeers.IsEmpty || !_options.SendBackups || _backupsHeld)
                return;

            // One blob per peer: a peer that refused the full size gets the same backup within its limit (NL-559)
            foreach (var peer in _storagePeers.Values)
                await SendBackupAsync(peer, force: false);
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
        // The retrievals whose write failed first (the evidence of a data loss)
        foreach (var unwritten in _unwrittenRetrievals.Values)
            await WriteRetrievalAsync(unwritten);

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

    /// <summary>
    /// Writes the peer's latest retrieval; on failure it is kept in memory (listed, and retried at every round and at
    /// the stop).
    /// </summary>
    private async Task WriteRetrievalAsync(StoredPeerRetrieval retrieval)
    {
        var peerId = retrieval.PeerNodeId;
        _unwrittenRetrievals.AddOrUpdate(peerId, retrieval,
                                         (_, existing) => existing.ReceivedAt > retrieval.ReceivedAt
                                                              ? existing
                                                              : retrieval);
        await _writeLock.WaitAsync();
        try
        {
            // A newer retrieval of the same peer was written meanwhile, or replaces this one
            if (!_unwrittenRetrievals.TryGetValue(peerId, out var latest) || !ReferenceEquals(latest, retrieval))
                return;

            using var scope = _scopeFactory.CreateScope();
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.PeerStorageRetrievalDbRepository.UpsertAsync(retrieval);
            await unitOfWork.SaveChangesAsync();
            _unwrittenRetrievals.TryRemove(new KeyValuePair<CompactPubKey, StoredPeerRetrieval>(peerId, retrieval));
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            _logger.LogWarning("Could not store the peer_storage_retrieval of peer {Peer} at shutdown", peerId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to store the peer_storage_retrieval of peer {Peer}; retrying in the next round",
                             peerId);
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
            if (_timer is not null || _disposed || _stopping.IsCancellationRequested)
                return;

            var interval = _options.BackupInterval > TimeSpan.Zero ? _options.BackupInterval : TimeSpan.FromMinutes(1);
            _timer = _timeProvider.CreateTimer(_ => _ = RunRoundAsync(), null, interval, interval);
        }
    }

    /// <summary>
    /// Keeps the held blobs of peers we now have a channel with and forgets the ones held longer than
    /// <see cref="PendingWithoutChannelLifetime"/>.
    /// </summary>
    private void AdoptPendingBlobs()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (peerId, pending) in _pendingWithoutChannel)
        {
            if (HasChannelWith(peerId))
            {
                if (_pendingWithoutChannel.TryRemove(new KeyValuePair<CompactPubKey, PendingBlob>(peerId, pending)))
                    HandlePeerStorage(peerId, pending.Blob);
            }
            else if (now - pending.ReceivedAt > PendingWithoutChannelLifetime)
            {
                _pendingWithoutChannel.TryRemove(new KeyValuePair<CompactPubKey, PendingBlob>(peerId, pending));
            }
        }
    }

    private sealed record PendingBlob(byte[] Blob, DateTimeOffset ReceivedAt);

    /// <summary>
    /// What a peer's refusals taught us (<see cref="PeerStorageRefusalReport"/> is what the operator sees); the limit
    /// lives in memory only, so a restart relearns it from the next refusal.
    /// </summary>
    private sealed record PeerRefusal(int AcceptedLimitBytes, int LastRefusedBlobLength, DateTimeOffset LastRefusalAt,
                                      int Count);

    private sealed class StoredEntry
    {
        public byte[] Blob { get; set; } = [];
        public DateTimeOffset ReceivedAt { get; set; }
        public DateTimeOffset? WrittenAt { get; set; }
        public bool Dirty { get; set; }
    }
}