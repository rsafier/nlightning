using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Gossip.Graph.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Models;
using Onchain.Interfaces;

/// <summary>
/// Restores a static channel backup as recovery-only channels (see <see cref="IChannelRestoreService"/> and
/// <see cref="RecoveryChannels"/>).
/// </summary>
/// <remarks>
/// <para>Per restored channel, one save holds the peer row (when the database has none: the backup's first address),
/// the recovery channel (Failed, data loss, its <c>error</c> stored) and the persisted watch of its funding output;
/// then the channel is registered (memory; the signer loads it with its data-loss mark) and the watch followed. A
/// channel already in the database is never touched.</para>
/// <para>Then every peer of a restored channel is connected: a peer that is connected already is disconnected first,
/// so the new connection runs <c>IChannelManager.OnPeerConnectedAsync</c>, which sends the data-loss
/// <c>channel_reestablish</c> and the error. A peer that can't be reached gets them on its next connection (it keeps
/// reconnecting to us while it has the channel, and every start connects to the peers of stored channels).</para>
/// <para>A funding output the peer spent before the restore (the chain monitor watches from its current height on) is
/// looked up through <see cref="IFundingSpendLocator"/>: a spend found is marked on the watch and handed to
/// <see cref="IOnchainChannelWatcher"/> like one the monitor saw. One older than the search depth is searched in the
/// background down to the funding block and handed over the same way once found (NL-430); the channel's result says
/// so. The search lives in memory: <see cref="ResumeSpendSearches"/> (called by the host at every start) looks the
/// spend up again for every recovery channel still waiting, and so does running <c>restorechanbackup</c> again
/// (after a chain error). A spend below the bitcoin node's pruned blocks can't be searched: the result says so
/// (<c>FundingSpendPruned</c>) instead of asking for a retry.</para>
/// <para>A peer is tried at every address known for it (NL-431): its <c>node_announcement</c> in the gossip graph,
/// the peer row, then every address of the backup. The restore itself tries the first one and the next ones only
/// within <see cref="ConnectBudget"/> (it holds the restore lock); the rest are tried at once by a background loop,
/// which then retries all of them (the graph read again each round) with the node's reconnect backoff until the peer
/// is connected, its recovery channels are no longer waiting for its close, or the node stops.</para>
/// <para>Before anything is stored, the key manager's last used channel index is advanced past the restored channels'
/// (<see cref="IChannelKeyIndexReserver"/>), so a stale key file never hands a restored channel's keys to a new
/// one.</para>
/// <para>Splices (lane SP2-E, NL-478): a backup's funding is the channel's current one when the backup was written, so
/// a channel spliced since then has that funding spent by the splice. Before a recovery channel is made, the funding
/// is followed from spend to spend (<see cref="IFundingSpendLocator.FollowSpliceAsync"/>: a pending splice of the
/// backup, our deterministic next funding keys, or the witness of the new output's spend) until it is unspent or spent
/// by a commitment, and the channel is made at that funding (its outpoint, capacity, keys, key index and short channel
/// id; the key index and pending splices are stored as its funding rows). A recovery channel already stored (a
/// splice confirmed after the restore, or found by the background search) is moved the same way
/// (<c>IChannelFundingDbRepository.ApplyLockAsync</c>, its memory model, the signer and a watch of the new outpoint).
/// </para>
/// <para>A splice the chain monitor reports for a recovery channel (it confirmed after the restore) reaches
/// <see cref="TryHandleRecoveryFundingSpendAsync"/> before the on-chain watcher (<see cref="SpliceFollowingOnchainChannelWatcher"/>)
/// and moves the channel the same way instead of closing it. A spend that is no commitment and can't be followed yet
/// (the peer rotated its funding key too and the splice's output is unspent) is never handed to the on-chain watcher
/// as a close: the channel stays a recovery channel at the spent funding and every new block (and every start, and
/// <c>restorechanbackup</c> again) checks the P2WSH outputs of that transaction until one is spent with a witness that
/// names our key (the channel moves there and the peer's commitment on it is handed over), or all of them are spent by
/// something else (then it was no splice of ours and is handed over). The result detail starts
/// <c>FundingSplicedUnresolved:</c> meanwhile.</para>
/// <para>A dual-funded open still unconfirmed when the backup was written (lane SP2-E review): the backup names every
/// signed candidate of its RBF as a pending funding with the channel's keys; the restore locates each and makes the
/// channel at the one that confirmed (<c>FundingRbf:</c>), else at the backed-up one with the others stored as pending
/// rows, which the start-up resume and a second <c>restorechanbackup</c> check again.</para>
/// </remarks>
public sealed class ChannelRestoreService : IChannelRestoreService, IDisposable
{
    private static readonly TimeSpan s_disconnectPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_registrationPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IChannelBackupService _backupService;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<ChannelRestoreService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IMessageSerializer _messageSerializer;
    private readonly NodeOptions _nodeOptions;
    private readonly IOutpointWatcher _outpointWatcher;
    private readonly IPeerManager _peerManager;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISha256 _sha256;
    private readonly ILightningSigner _signer;
    private readonly IFundingSpendLocator? _spendLocator;
    private readonly IOnchainChannelWatcher? _onchainWatcher;
    private readonly IChannelKeyIndexReserver? _keyIndexReserver;
    private readonly IGraphStore? _graphStore;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IChannelFundingKeySource _fundingKeySource;
    private readonly IChannelLockProvider? _channelLockProvider;
    private readonly SemaphoreSlim _restoreLock = new(1, 1);
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly ConcurrentDictionary<ChannelId, Task> _rescans = new();
    private readonly ConcurrentDictionary<CompactPubKey, Task> _reconnects = new();
    private readonly Lock _resumeGate = new();
    private readonly ConcurrentDictionary<ChannelId, SpliceWait> _spliceWaits = new();
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Lock _spliceWaitGate = new();
    private readonly TimeProvider _timeProvider;
    private Task? _resume;
    private Task? _spliceWaitRound;
    private bool _spliceWaitRoundRequested;

    /// <summary>How long a restore waits for a connected peer's connection to close before it connects again.</summary>
    internal TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a restore spends connecting to one peer before it hands the addresses not tried yet to the background
    /// reconnection (NL-431): the first address is always tried, the next ones only while this has not passed.
    /// </summary>
    internal TimeSpan ConnectBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long <see cref="ResumeSpendSearches"/> waits for a recovery channel to be loaded by the start-up
    /// registration before it gives up on that channel.
    /// </summary>
    internal TimeSpan ResumeRegistrationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The first wait of the background reconnection to an unreachable peer (<c>Node:ReconnectInitialDelay</c>).
    /// </summary>
    internal TimeSpan ReconnectInitialDelay { get; init; }

    /// <summary>The longest wait of the background reconnection (<c>Node:ReconnectMaxDelay</c>).</summary>
    internal TimeSpan ReconnectMaxDelay { get; init; }

    public ChannelRestoreService(IChannelBackupService backupService, IChannelManager channelManager,
                                 IMessageFactory messageFactory, IMessageSerializer messageSerializer,
                                 IOptions<NodeOptions> nodeOptions, IOutpointWatcher outpointWatcher,
                                 IPeerManager peerManager, ISecureKeyManager secureKeyManager,
                                 IServiceScopeFactory serviceScopeFactory, ISha256 sha256, ILightningSigner signer,
                                 ILogger<ChannelRestoreService>? logger = null,
                                 IFundingSpendLocator? spendLocator = null,
                                 IOnchainChannelWatcher? onchainWatcher = null,
                                 IChannelKeyIndexReserver? keyIndexReserver = null,
                                 IGraphStore? graphStore = null,
                                 IChannelMemoryRepository? channelMemoryRepository = null,
                                 IChannelFundingKeySource? fundingKeySource = null,
                                 IChannelLockProvider? channelLockProvider = null,
                                 TimeProvider? timeProvider = null)
    {
        _graphStore = graphStore;
        _fundingKeySource = fundingKeySource ?? new SignerChannelFundingKeySource(signer);
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _spendLocator = spendLocator;
        _onchainWatcher = onchainWatcher;
        _keyIndexReserver = keyIndexReserver;
        _backupService = backupService;
        _channelManager = channelManager;
        _messageFactory = messageFactory;
        _messageSerializer = messageSerializer;
        _nodeOptions = nodeOptions.Value;
        _outpointWatcher = outpointWatcher;
        _peerManager = peerManager;
        _secureKeyManager = secureKeyManager;
        _serviceScopeFactory = serviceScopeFactory;
        _sha256 = sha256;
        _signer = signer;
        _logger = logger ?? NullLogger<ChannelRestoreService>.Instance;
        ReconnectInitialDelay = _nodeOptions.ReconnectInitialDelay;
        ReconnectMaxDelay = _nodeOptions.ReconnectMaxDelay;

        // The chain monitor is the outpoint watcher: its blocks drive the checks of unresolved splices
        _blockchainMonitor = outpointWatcher as IBlockchainMonitor;
        if (_blockchainMonitor is not null)
            _blockchainMonitor.OnNewBlockDetected += HandleNewBlock;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Stops the background spend searches and reconnections.</summary>
    public void Dispose()
    {
        if (_blockchainMonitor is not null)
            _blockchainMonitor.OnNewBlockDetected -= HandleNewBlock;
        if (!_backgroundCts.IsCancellationRequested)
            _backgroundCts.Cancel();
        _backgroundCts.Dispose();
        _restoreLock.Dispose();
    }

    /// <summary>Completes when every background spend search and reconnection started so far has ended.</summary>
    internal async Task WaitForBackgroundWorkAsync()
    {
        if (_resume is { } resume)
            await resume;

        await Task.WhenAll(_rescans.Values.Concat(_reconnects.Values));
        Task? round;
        lock (_spliceWaitGate)
            round = _spliceWaitRound;
        if (round is not null)
            await round;
    }

    /// <summary>The recovery channels waiting for an unresolved splice of their funding (tests).</summary>
    internal IReadOnlyCollection<ChannelId> ChannelsWaitingForSplice => _spliceWaits.Keys.ToList();

    /// <inheritdoc />
    public void ResumeSpendSearches()
    {
        if (_spendLocator is null || _onchainWatcher is null || _backgroundCts.IsCancellationRequested)
            return;

        lock (_resumeGate)
        {
            if (_resume is not null)
                return;

            var token = _backgroundCts.Token;
            _resume = Task.Run(() => ResumeSpendSearchesAsync(token), token);
        }
    }

    /// <summary>
    /// The start-up half of NL-430: every recovery channel still waiting for its peer's close
    /// (<see cref="RecoveryChannels.IsRecoveryChannel"/>) has its funding spend looked up again, as a second
    /// <c>restorechanbackup</c> would, once the channel is registered (the on-chain watcher ignores a channel that is
    /// not loaded). An unspent funding output costs one <c>gettxout</c>; a spend older than the recent window starts
    /// the background search again.
    /// </summary>
    internal async Task ResumeSpendSearchesAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<ChannelBackupEntry> entries;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                entries = [];
                foreach (var channel in (await unitOfWork.ChannelDbRepository.GetAllAsync())
                                       .Where(c => RecoveryChannels.IsRecoveryChannel(c)
                                                && c.FundingOutput is { TransactionId: not null, Index: not null }
                                                && c.RemoteKeySet is not null))
                    entries.Add(ChannelBackupService.CreateEntry(
                                    channel, null, null,
                                    await ChannelBackupService.GetFundingSetAsync(unitOfWork, channel.ChannelId,
                                                                                  _logger)));
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await WaitUntilRegisteredAsync(entry.ChannelId, cancellationToken))
                {
                    _logger.LogWarning("Recovery channel {ChannelId} was not loaded within {Timeout}; the search for "
                                     + "its funding spend is not resumed (run restorechanbackup again)",
                                       entry.ChannelId, ResumeRegistrationTimeout);
                    continue;
                }

                await _restoreLock.WaitAsync(cancellationToken);
                try
                {
                    if (IsRescanRunning(entry.ChannelId))
                        continue;

                    var fundingWatch = new WatchedOutpointModel(entry.FundingTxId, entry.FundingOutputIndex,
                                                                entry.ChannelId,
                                                                WatchedOutpointPurpose.FundingOutput);
                    if (await HandleEarlierSpendAsync(entry, fundingWatch, cancellationToken) is { } detail)
                        _logger.LogWarning("Recovery channel {ChannelId} at start: {Detail}", entry.ChannelId, detail);
                }
                finally
                {
                    _restoreLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not resume the funding spend searches of the recovery channels; run "
                              + "restorechanbackup again to look them up");
        }
    }

    /// <summary>Waits until <paramref name="channelId"/> is in channel memory (true at once without one).</summary>
    private async Task<bool> WaitUntilRegisteredAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        if (_channelMemoryRepository is null)
            return true;

        var deadline = DateTime.UtcNow + ResumeRegistrationTimeout;
        while (!_channelMemoryRepository.TryGetChannel(channelId, out _))
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(s_registrationPollInterval, cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<ChannelRestoreResult> RestoreAsync(ReadOnlyMemory<byte> backup,
                                                         CancellationToken cancellationToken)
    {
        var snapshot = _backupService.Decrypt(backup.Span);
        ChannelRestorePlanner.Validate(snapshot, _nodeOptions.BitcoinNetwork.ChainHash,
                                       _secureKeyManager.GetNodePubKey());

        // One restore at a time: two would race on the same rows
        await _restoreLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadExistingStatesAsync(snapshot.Channels);
            var plan = ChannelRestorePlanner.Plan(snapshot.Channels,
                                                  id => existing.TryGetValue(id, out var row)
                                                            ? (true, row.State)
                                                            : (false, null),
                                                  _signer.GetChannelBasepoints, _fundingKeySource.GetFundingPubKey);

            await ReserveKeyIndexesAsync(plan, cancellationToken);

            var results = new List<ChannelRestoreChannelResult>(plan.Count);
            // A peer of a new recovery channel is reconnected; one of a recovery channel restored before only when it
            // is not connected (a connection sends the data-loss reestablish)
            var restoredPeers = new Dictionary<CompactPubKey, (ChannelBackupEntry Entry, bool OnlyIfDisconnected)>();
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waitingRecovery = item.Action == ChannelRestoreAction.AlreadyExists
                                   && existing.TryGetValue(item.Entry.ChannelId, out var row)
                                   && row.IsWaitingRecovery;
                var result = waitingRecovery
                                 ? await RecheckRecoveryChannelAsync(item.Entry, cancellationToken)
                                 : await ApplyAsync(item, cancellationToken);
                results.Add(result);
                if (result.Action == ChannelRestoreAction.Restore)
                    restoredPeers[item.Entry.RemoteNodeId] = (item.Entry, false);
                else if (waitingRecovery)
                    restoredPeers.TryAdd(item.Entry.RemoteNodeId, (item.Entry, true));
            }

            var peers = new List<ChannelRestorePeerResult>(restoredPeers.Count);
            foreach (var (nodeId, (entry, onlyIfDisconnected)) in restoredPeers)
                peers.Add(await ConnectAsync(nodeId, entry, onlyIfDisconnected, cancellationToken));

            _logger.LogWarning("Restored {Count} of {Total} backed-up channel(s) as recovery channels; their peers are "
                             + "asked to force close", results.Count(r => r.Action == ChannelRestoreAction.Restore),
                               snapshot.Channels.Count);
            return new ChannelRestoreResult(snapshot, results, peers);
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    /// <summary>
    /// The database row of every backed-up channel that has one: its state (null when it can't be read) and whether
    /// it is a recovery channel still waiting for the peer's close (<see cref="RecoveryChannels.IsRecoveryChannel"/>).
    /// </summary>
    private async Task<Dictionary<ChannelId, (ChannelState? State, bool IsWaitingRecovery)>> LoadExistingStatesAsync(
        IReadOnlyList<ChannelBackupEntry> entries)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var states = new Dictionary<ChannelId, (ChannelState? State, bool IsWaitingRecovery)>();
        foreach (var entry in entries)
        {
            if (states.ContainsKey(entry.ChannelId))
                continue;

            try
            {
                if (await unitOfWork.ChannelDbRepository.GetByIdAsync(entry.ChannelId) is { } channel)
                    states[entry.ChannelId] = (channel.State, RecoveryChannels.IsRecoveryChannel(channel));
            }
            catch (Exception e)
            {
                // A row the repository refuses (legacy HTLC state) still exists: never overwrite it
                _logger.LogWarning(e, "Channel {ChannelId} is in the database but can't be read; not restoring it",
                                   entry.ChannelId);
                states[entry.ChannelId] = (null, false);
            }
        }

        return states;
    }

    /// <summary>Advances the key manager's channel index past every channel about to be restored.</summary>
    private async Task ReserveKeyIndexesAsync(IReadOnlyList<ChannelRestorePlanItem> plan,
                                              CancellationToken cancellationToken)
    {
        if (_keyIndexReserver is null)
            return;

        var restored = plan.Where(i => i.Action == ChannelRestoreAction.Restore).ToList();
        if (restored.Count == 0)
            return;

        var highest = restored.Max(i => i.Entry.KeyIndex);
        try
        {
            await _keyIndexReserver.ReserveThroughAsync(highest, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The index is advanced in memory before the write: only its persistence failed
            _logger.LogError(e, "Could not persist the channel key index {Index} of the restored channels; make sure "
                              + "the key file's last used index is at least that before opening channels", highest);
        }
    }

    private async Task<ChannelRestoreChannelResult> ApplyAsync(ChannelRestorePlanItem item,
                                                               CancellationToken cancellationToken)
    {
        var entry = item.Entry;
        switch (item.Action)
        {
            case ChannelRestoreAction.AlreadyExists:
                return new ChannelRestoreChannelResult(entry, item.Action,
                                                       $"already in the database ({item.ExistingState?.ToString() ?? "unreadable"}); left as it is");
            case ChannelRestoreAction.KeysMismatch:
                _logger.LogError("Channel {ChannelId}: key index {Index} does not derive the backed-up keys; not "
                               + "restored", entry.ChannelId, entry.KeyIndex);
                return new ChannelRestoreChannelResult(entry, item.Action,
                                                       "its key index does not derive the backed-up keys (another key file?)");
            case ChannelRestoreAction.Duplicate:
                return new ChannelRestoreChannelResult(entry, item.Action, "repeated in the backup");
        }

        // A dual-funded open unconfirmed at the backup: made at the candidate of its RBF that confirmed
        var backedUp = entry;
        var (selected, selectedLocation) = await SelectConfirmedCandidateAsync(entry, cancellationToken);
        var rbfPrefix = selected.FundingTxId == entry.FundingTxId
                            ? string.Empty
                            : $"FundingRbf: the dual-funded open confirmed as {selected.FundingTxId}:"
                            + $"{selected.FundingOutputIndex}, not the backed-up {entry.FundingTxId}; ";

        // The backed-up funding may have been spliced since the backup: the channel is made at its current funding
        var (current, location, splices) = await FollowSplicesAsync(selected, selectedLocation, cancellationToken);
        entry = current;

        ChannelModel channel;
        WatchedOutpointModel fundingWatch;
        try
        {
            channel = RecoveryChannels.Create(entry, item.LocalBasepoints!.Value, entry.LocalFundingPubKey, _sha256);
            var error = _messageFactory.CreateErrorMessage(RecoveryChannels.PeerErrorMessage, entry.ChannelId);
            using (var errorStream = new MemoryStream())
            {
                await _messageSerializer.SerializeAsync(error, errorStream);
                channel.MarkErrorSent(errorStream.ToArray());
            }

            fundingWatch = new WatchedOutpointModel(entry.FundingTxId, entry.FundingOutputIndex, entry.ChannelId,
                                                    WatchedOutpointPurpose.FundingOutput);

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.PeerDbRepository.GetByNodeIdAsync(entry.RemoteNodeId) is null)
                await unitOfWork.PeerDbRepository.AddOrUpdateAsync(CreatePeer(entry));
            await unitOfWork.ChannelDbRepository.AddAsync(channel);
            await StageSpliceFundingsAsync(unitOfWork, entry);
            if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(fundingWatch.TransactionId,
                                                                      fundingWatch.OutputIndex) is null)
                unitOfWork.WatchedOutpointDbRepository.Add(fundingWatch);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not store the recovery channel {ChannelId}", entry.ChannelId);
            return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.Failed,
                                                   $"could not be stored: {e.Message}");
        }

        try
        {
            await _channelManager.RegisterExistingChannelAsync(channel);
            _outpointWatcher.TrackWatchedOutpoint(fundingWatch);
        }
        catch (Exception e)
        {
            // Stored: the next start registers it and the chain monitor loads the watch
            _logger.LogError(e, "Recovery channel {ChannelId} is stored but could not be registered; restart the node",
                             entry.ChannelId);
            return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.Failed,
                                                   $"stored, but not registered ({e.Message}): restart the node");
        }

        _logger.LogWarning("Recovery channel {ChannelId} with {Peer} restored (funding {FundingTxId}:{Index}, "
                         + "{Capacity} sat); the peer is asked to force close", entry.ChannelId, entry.RemoteNodeId,
                           entry.FundingTxId, entry.FundingOutputIndex, entry.CapacitySat);
        var spendDetail = location is null
                              ? null
                              : await HandleLocationAsync(entry, fundingWatch, location, cancellationToken);
        return new ChannelRestoreChannelResult(backedUp, ChannelRestoreAction.Restore,
                                               rbfPrefix + SplicePrefix(splices, entry)
                                             + (spendDetail
                                             ?? "recovery channel stored; the peer is asked to force close and our "
                                              + "output is swept once its commitment confirms"));
    }

    /// <summary>
    /// The result detail's prefix for a channel followed through <paramref name="splices"/> (empty when none):
    /// <c>FundingSpliced:</c> with the splices and the funding the channel was restored at.
    /// </summary>
    private static string SplicePrefix(IReadOnlyList<TxId> splices, ChannelBackupEntry current) =>
        splices.Count == 0
            ? string.Empty
            : $"FundingSpliced: followed {splices.Count} splice(s) ({string.Join(", ", splices)}) to the funding "
            + $"{current.FundingTxId}:{current.FundingOutputIndex} ({current.CapacitySat} sat, funding key index "
            + $"{current.LocalFundingKeyIndex}); ";

    /// <summary>
    /// Follows <paramref name="entry"/>'s funding through the splices that spent it (NL-478): while the funding output
    /// is spent and the spend is a splice of the channel (<see cref="IFundingSpendLocator.FollowSpliceAsync"/>), the
    /// channel moves to the splice's funding output and that one is located. Ends at an unspent funding, a commitment
    /// (or any other spend that is no splice), a chain error or a spend not found. Without a locator: the entry as it
    /// is, no location.
    /// </summary>
    /// <param name="entry">The channel at the funding to start from.</param>
    /// <param name="location">That funding's location when already known.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    private async Task<(ChannelBackupEntry Current, FundingSpendLocation? Location, IReadOnlyList<TxId> Splices)>
        FollowSplicesAsync(ChannelBackupEntry entry, FundingSpendLocation? location,
                           CancellationToken cancellationToken)
    {
        if (_spendLocator is null)
            return (entry, location, []);

        var current = entry;
        var splices = new List<TxId>();
        location ??= await _spendLocator.LocateAsync(current, cancellationToken);
        while (location is { Status: FundingSpendStatus.SpentFound, Spend: { } spend }
            && splices.Count < SpliceSpendFollower.MaxSplices)
        {
            var keyIndex = current.KeyIndex;
            var next = await _spendLocator.FollowSpliceAsync(
                           current, spend, index => _fundingKeySource.GetFundingPubKey(keyIndex, index),
                           cancellationToken);
            if (next is null)
                break;

            _logger.LogWarning("The funding {FundingTxId}:{Index} of channel {ChannelId} was spliced by {SpliceTxId} at "
                             + "height {Height}: following it to {NewFundingTxId}:{NewIndex}", current.FundingTxId,
                               current.FundingOutputIndex, current.ChannelId, spend.SpendingTransaction.TxId,
                               spend.BlockHeight, next.FundingTxId, next.FundingOutputIndex);
            splices.Add(spend.SpendingTransaction.TxId);
            current = next;
            location = await _spendLocator.LocateAsync(current, cancellationToken);
        }

        return (current, location, splices);
    }

    /// <summary>
    /// Stages the funding rows of a recovery channel whose current funding is a splice's, or whose backup named pending
    /// splices (<see cref="RecoveryChannels.CreateFundings"/>), on the unit of work that added the channel: the channel
    /// then reloads with the splice's keys and our rotated key index. Without a funding repository (a unit of work that
    /// stores none) the channel is stored without them and the loss is logged.
    /// </summary>
    private async Task StageSpliceFundingsAsync(IUnitOfWork unitOfWork, ChannelBackupEntry entry)
    {
        if (!RecoveryChannels.HasSpliceFundings(entry))
            return;

        if (GetFundingRepository(unitOfWork) is not { } fundings)
        {
            _logger.LogError("Recovery channel {ChannelId} is at a spliced funding (key index {Index}) but this "
                           + "database stores no channel fundings: it reloads with the original funding key",
                             entry.ChannelId, entry.LocalFundingKeyIndex);
            return;
        }

        var (current, pending) = RecoveryChannels.CreateFundings(entry);
        await fundings.UpsertAsync(entry.ChannelId, current);
        foreach (var funding in pending)
            await fundings.UpsertAsync(entry.ChannelId, funding);
    }

    /// <summary>The unit of work's funding repository, or null when it stores none.</summary>
    private static IChannelFundingDbRepository? GetFundingRepository(IUnitOfWork unitOfWork)
    {
        try
        {
            return unitOfWork.ChannelFundingDbRepository;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves a stored recovery channel from <paramref name="from"/>'s funding to <paramref name="to"/>'s (a splice found
    /// after the channel was made, NL-478): the lock's save (<c>ApplyLockAsync</c>: the channel row's funding columns and
    /// short channel id, the new funding row as current and the old one replaced) with the watch of the new outpoint,
    /// then the memory model under the channel's lock, the signer (registered and locked like a splice) and the chain
    /// monitor's watch. False when it could not be saved (logged): the channel stays at the old funding.
    /// </summary>
    private async Task<bool> MoveRecoveryChannelAsync(ChannelBackupEntry from, ChannelBackupEntry to,
                                                      CancellationToken cancellationToken,
                                                      ChannelFundingKind kind = ChannelFundingKind.Splice)
    {
        var channelId = from.ChannelId;
        var newWatch = new WatchedOutpointModel(to.FundingTxId, to.FundingOutputIndex, channelId,
                                                WatchedOutpointPurpose.FundingOutput);
        var locked = new ChannelFunding(to.FundingTxId, to.FundingOutputIndex, to.CapacitySat, to.LocalFundingPubKey,
                                        to.RemoteFundingPubKey, to.LocalFundingKeyIndex, 0, 0,
                                        kind, ChannelFundingStatus.Current,
                                        ConfirmedHeight: to.FundingHeight, ShortChannelId: to.ShortChannelId);
        var lockHandle = _channelLockProvider is null
                             ? null
                             : await _channelLockProvider.AcquireAsync(channelId, cancellationToken);
        try
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
                if (stored is null || !RecoveryChannels.IsRecoveryChannel(stored))
                {
                    _logger.LogWarning("Channel {ChannelId} is no longer a recovery channel; it is not moved to the "
                                     + "splice {FundingTxId}", channelId, to.FundingTxId);
                    return false;
                }

                // Moved already (a restore run again with a backup older than the move): nothing to write
                if (stored.FundingOutput is { TransactionId: { } storedTxId, Index: { } storedIndex }
                 && storedTxId == to.FundingTxId && storedIndex == to.FundingOutputIndex)
                    return true;

                if (GetFundingRepository(unitOfWork) is not { } fundings)
                {
                    _logger.LogError("Recovery channel {ChannelId} was spliced to {FundingTxId}:{Index}, but this "
                                   + "database stores no channel fundings: it can't be moved (sweep by hand if the peer "
                                   + "closes it there)", channelId, to.FundingTxId, to.FundingOutputIndex);
                    return false;
                }

                var rows = await fundings.GetByChannelIdAsync(channelId);
                if (rows.All(f => f.FundingTxId != to.FundingTxId))
                    await fundings.UpsertAsync(channelId, locked with { Status = ChannelFundingStatus.Pending });

                var set = await fundings.GetFundingSetAsync(channelId);
                var retired = new List<ChannelFunding>();
                if (set is not null)
                {
                    retired.Add(set.Current with { Status = ChannelFundingStatus.Replaced });
                    retired.AddRange(set.Pending.Where(f => f.FundingTxId != to.FundingTxId)
                                        .Select(f => f with { Status = ChannelFundingStatus.Discarded }));
                }

                await fundings.ApplyLockAsync(channelId, locked, retired);
                if (await unitOfWork.WatchedOutpointDbRepository.GetAsync(newWatch.TransactionId,
                                                                          newWatch.OutputIndex) is null)
                    unitOfWork.WatchedOutpointDbRepository.Add(newWatch);
                await unitOfWork.SaveChangesAsync();
            }

            if (_channelMemoryRepository is not null && _channelMemoryRepository.TryGetChannel(channelId, out var live))
            {
                live.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(to.CapacitySat),
                                                                to.LocalFundingPubKey, to.RemoteFundingPubKey,
                                                                to.FundingTxId, to.FundingOutputIndex));
                live.SetLocalFundingKeyIndex(to.LocalFundingKeyIndex);
                if (to.ShortChannelId is { } shortChannelId)
                    live.ShortChannelId = shortChannelId;
                live.FundingCreatedAtBlockHeight = to.FundingHeight;

                // NL-138: the backup monitor (a new SCB entry for the splice) and the channel update service (its
                // channel_update follows the new short channel id) only learn of the move through OnChannelUpdated
                _channelMemoryRepository.UpdateChannel(live);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not move recovery channel {ChannelId} to its splice {FundingTxId}:{Index}",
                             channelId, to.FundingTxId, to.FundingOutputIndex);
            return false;
        }
        finally
        {
            lockHandle?.Dispose();
        }

        // The signer follows the lock (it may also load the channel from the database at its new funding already)
        try
        {
            _signer.RegisterFunding(channelId, locked with { Status = ChannelFundingStatus.Pending });
            _signer.LockFunding(channelId, to.FundingTxId, to.ShortChannelId);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The signer did not take the splice {FundingTxId} of recovery channel {ChannelId} "
                              + "(it loads the channel from the database)", to.FundingTxId, channelId);
        }

        _outpointWatcher.TrackWatchedOutpoint(newWatch);
        _logger.LogWarning("Recovery channel {ChannelId} moved to its splice {FundingTxId}:{Index} ({Capacity} sat)",
                           channelId, to.FundingTxId, to.FundingOutputIndex, to.CapacitySat);
        return true;
    }

    /// <summary>
    /// A channel restored by an earlier <c>restorechanbackup</c> that is still waiting for the peer's close: its
    /// funding spend is looked up again (an interrupted background search, a chain error) and its peer asked again.
    /// The channel itself is left as it is.
    /// </summary>
    private async Task<ChannelRestoreChannelResult> RecheckRecoveryChannelAsync(ChannelBackupEntry entry,
                                                                                CancellationToken cancellationToken)
    {
        const string prefix = "already in the database as a recovery channel waiting for the peer's close";
        var fundingWatch = new WatchedOutpointModel(entry.FundingTxId, entry.FundingOutputIndex, entry.ChannelId,
                                                    WatchedOutpointPurpose.FundingOutput);
        if (IsRescanRunning(entry.ChannelId))
            return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.AlreadyExists,
                                                   $"{prefix}; the search for its funding spend is still running in "
                                                 + "the background");

        var spendDetail = await HandleEarlierSpendAsync(entry, fundingWatch, cancellationToken);
        return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.AlreadyExists,
                                               $"{prefix}; {spendDetail ?? "the peer is asked again to force close"}");
    }

    /// <summary>
    /// Looks for a spend of the funding output mined before the restore and hands a found one to the on-chain
    /// watcher (a splice moves the channel first). Returns the channel's result detail when the funding output is not
    /// simply unspent (null otherwise).
    /// </summary>
    /// <param name="entry">The recovery channel as stored.</param>
    /// <param name="fundingWatch">The watch of its funding output.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <param name="known">The funding output's location when already known (a spend the chain monitor reported);
    /// null: located here, and a dual-funded open's other candidates checked first.</param>
    private async Task<string?> HandleEarlierSpendAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch,
                                                        CancellationToken cancellationToken,
                                                        FundingSpendLocation? known = null)
    {
        if (_spendLocator is null)
            return null;

        var prefix = string.Empty;
        if (known is null)
        {
            // A dual-funded open restored before it confirmed: another candidate of its RBF may be the one that did
            var (selected, selectedLocation) = await SelectConfirmedCandidateAsync(entry, cancellationToken);
            if (selected.FundingTxId != entry.FundingTxId || selected.FundingOutputIndex != entry.FundingOutputIndex)
            {
                if (!await MoveRecoveryChannelAsync(entry, selected, cancellationToken, ChannelFundingKind.Initial))
                    return $"FundingRbf: the dual-funded open confirmed as {selected.FundingTxId}:"
                         + $"{selected.FundingOutputIndex}, but the recovery channel could not be moved there: run "
                         + "restorechanbackup again";

                prefix = $"FundingRbf: moved to {selected.FundingTxId}:{selected.FundingOutputIndex}, the candidate of "
                       + "the dual-funded open that confirmed; ";
                entry = selected;
                fundingWatch = new WatchedOutpointModel(selected.FundingTxId, selected.FundingOutputIndex,
                                                        selected.ChannelId, WatchedOutpointPurpose.FundingOutput);
            }

            known = selectedLocation;
        }

        var (current, location, splices) = await FollowSplicesAsync(entry, known, cancellationToken);
        if (splices.Count == 0)
            return await HandleLocationAsync(entry, fundingWatch, location!, cancellationToken) is { } detail
                       ? prefix + detail
                       : prefix.Length == 0
                           ? null
                           : prefix + "the peer is asked to force close";

        if (!await MoveRecoveryChannelAsync(entry, current, cancellationToken))
            return $"{prefix}FundingSpliced: the funding was spliced by {splices[0]}, but the recovery channel could "
                 + "not be moved to the splice: run restorechanbackup again";

        var moved = new WatchedOutpointModel(current.FundingTxId, current.FundingOutputIndex, current.ChannelId,
                                             WatchedOutpointPurpose.FundingOutput);
        return prefix + SplicePrefix(splices, current)
             + (await HandleLocationAsync(current, moved, location!, cancellationToken)
             ?? "the peer is asked to force close and our output is swept once its commitment confirms");
    }

    /// <summary>
    /// Whether a location tells that its funding transaction confirmed: its output is unspent in a block, or spent.
    /// </summary>
    private static bool IsConfirmed(FundingSpendLocation location) =>
        location.Status is FundingSpendStatus.Unspent or FundingSpendStatus.SpentFound
                        or FundingSpendStatus.SpentNotFound or FundingSpendStatus.BlocksPruned;

    /// <summary>
    /// For a dual-funded open that had not confirmed when the backup was written (no short channel id; lane SP2-E
    /// review): the backup names every signed candidate of its RBF as a pending funding with the channel's keys, and
    /// any of them may be the one that confirmed. Returns the entry at the first of the backed-up funding and its
    /// candidates that confirmed, without pending fundings, with its location; when none did (or the chain can't tell),
    /// the entry as it is (its candidates stay pending, checked again later) with the backed-up funding's location.
    /// Any other entry: itself and no location.
    /// </summary>
    private async Task<(ChannelBackupEntry Entry, FundingSpendLocation? Location)> SelectConfirmedCandidateAsync(
        ChannelBackupEntry entry, CancellationToken cancellationToken)
    {
        if (_spendLocator is null || entry.ShortChannelId is not null)
            return (entry, null);

        var candidates = entry.PendingFundings
                              .Where(f => f.LocalFundingPubKey == entry.LocalFundingPubKey
                                       && f.RemoteFundingPubKey == entry.RemoteFundingPubKey
                                       && f.FundingTxId != entry.FundingTxId)
                              .ToList();
        if (candidates.Count == 0)
            return (entry, null);

        var alone = entry with { PendingFundings = [] };
        var location = await _spendLocator.LocateAsync(alone, cancellationToken);
        if (IsConfirmed(location))
            return (alone, location);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moved = alone with
            {
                FundingTxId = candidate.FundingTxId,
                FundingOutputIndex = candidate.FundingOutputIndex,
                CapacitySat = candidate.CapacitySat,
                LocalFundingKeyIndex = candidate.LocalFundingKeyIndex
            };
            var candidateLocation = await _spendLocator.LocateAsync(moved, cancellationToken);
            if (!IsConfirmed(candidateLocation))
                continue;

            _logger.LogWarning("The dual-funded open of channel {ChannelId} confirmed as {FundingTxId}:{Index}, an "
                             + "earlier candidate of its RBF than the backed-up {BackedUpTxId}", entry.ChannelId,
                               moved.FundingTxId, moved.FundingOutputIndex, entry.FundingTxId);
            return (moved, candidateLocation);
        }

        return (entry, location);
    }

    /// <summary>
    /// Acts on where the funding output of <paramref name="entry"/> stands: a spend found is handed to the on-chain
    /// watcher, an older one searched in the background. Returns the channel's result detail when the funding output is
    /// not simply unspent (null otherwise).
    /// </summary>
    private async Task<string?> HandleLocationAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch,
                                                    FundingSpendLocation location,
                                                    CancellationToken cancellationToken)
    {
        switch (location.Status)
        {
            case FundingSpendStatus.SpentFound when location.Spend is { } spend:
                return await HandleSpentFoundAsync(entry, fundingWatch, spend, cancellationToken);
            case FundingSpendStatus.SpentNotFound when location.HasOlderBlocksToSearch && _onchainWatcher is not null:
                StartRescan(entry, fundingWatch, location.SearchedFromHeight);
                _logger.LogWarning("The funding output of recovery channel {ChannelId} is already spent, but not in the "
                                 + "blocks from {Searched} to the tip; searching blocks {Below} down to {Floor} in the "
                                 + "background", entry.ChannelId, location.SearchedFromHeight,
                                   location.SearchedFromHeight - 1, location.FloorHeight);
                return "FundingSpendRescan: recovery channel stored; its funding output is already spent and the spend "
                     + $"is not in the recent blocks searched (from {location.SearchedFromHeight} to the tip): blocks "
                     + $"{location.SearchedFromHeight - 1} down to {location.FloorHeight} are searched in the "
                     + "background (the search starts again at every start of the node until the spend is found) and "
                     + "our output is swept once it is found";
            case FundingSpendStatus.SpentNotFound:
                var rescanFrom = entry.FundingHeight is > 0 and var fundingHeight
                                     ? Math.Min(fundingHeight, location.SearchedFromHeight)
                                     : location.SearchedFromHeight;
                _logger.LogError("The funding output of recovery channel {ChannelId} is already spent, but the spend is "
                               + "not in the blocks searched (from {Searched} to the tip): rescan from height "
                               + "{Height} to sweep our output", entry.ChannelId, location.SearchedFromHeight,
                                 rescanFrom);
                return "FundingAlreadySpent: recovery channel stored, but its funding output is already spent and "
                     + $"the spend is not in the blocks searched: rescan from height {rescanFrom} to sweep our output";
            case FundingSpendStatus.BlocksPruned:
                _logger.LogError("The funding output of recovery channel {ChannelId} is already spent, but the bitcoin "
                               + "node has pruned block {Pruned} and below, where the spend must be: sweep our output "
                               + "by hand", entry.ChannelId, location.PrunedHeight);
                return PrunedDetail(location);
            case FundingSpendStatus.ChainUnavailable:
                return "recovery channel stored; the peer is asked to force close, but whether the funding output is "
                     + $"already spent could not be checked ({location.Error}): run restorechanbackup again to "
                     + "check it";
            case FundingSpendStatus.NotConfirmed when location.FloorHeight > 0:
                return "recovery channel stored; its funding transaction is in no block from height "
                     + $"{location.FloorHeight} to the tip (it may never have confirmed); the peer is asked to force "
                     + "close";
            default:
                return null;
        }
    }

    /// <summary>The result detail for a spend below the bitcoin node's pruned blocks.</summary>
    private static string PrunedDetail(FundingSpendLocation location) =>
        "FundingSpendPruned: recovery channel stored; its funding output is already spent, but the spend is not in "
      + $"the blocks from {location.SearchedFromHeight} to the tip and the bitcoin node has pruned block "
      + $"{location.PrunedHeight} and below, so it can't be searched on this node (searching again fails the same "
      + "way): find the spend with an unpruned node or a block explorer and sweep our output by hand";

    /// <summary>
    /// A spend of <paramref name="entry"/>'s funding output that no splice of the backup, no key of ours at the next
    /// indexes and no witness of its outputs' spends identified as a splice (<see cref="FollowSplicesAsync"/> stopped at
    /// it). A commitment (or a transaction that can't be read, or has no P2WSH output) is a close: handed to the on-chain
    /// watcher. Anything else may be a splice whose peer rotated its key and whose output is still unspent: it is never
    /// handed over as a close; the channel waits at this funding (<see cref="CheckSpliceWaitAsync"/>, again at every
    /// block) until one of its P2WSH outputs is spent with a witness naming our key, or all of them are spent by
    /// something else.
    /// </summary>
    private async Task<string> HandleSpentFoundAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch,
                                                     OutpointSpentEventArgs spend,
                                                     CancellationToken cancellationToken)
    {
        var outputs = SpliceSpendFollower.GetSpliceCandidateOutputs(spend.SpendingTransaction.RawTxBytes);
        if (_spendLocator is null || outputs is null || outputs.Count == 0)
            return await HandOverEarlierSpendAsync(entry, fundingWatch, spend, cancellationToken);

        try
        {
            await MarkWatchSpentAsync(fundingWatch, spend);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not record the spend of the funding output of recovery channel {ChannelId}",
                               entry.ChannelId);
        }

        var wait = new SpliceWait(entry, spend, outputs);
        _spliceWaits[entry.ChannelId] = wait;
        return await CheckSpliceWaitAsync(wait, cancellationToken);
    }

    /// <summary>
    /// Checks the P2WSH outputs of the unresolved spend of <paramref name="wait"/>: one spent with a 2-of-2 witness
    /// naming our key is the channel's next funding (the channel is moved there and followed on); one spent otherwise
    /// is no funding of ours; when none is left, the spend was no splice of ours and is handed to the on-chain watcher.
    /// Returns the channel's result detail. Call it with the restore lock held.
    /// </summary>
    private async Task<string> CheckSpliceWaitAsync(SpliceWait wait, CancellationToken cancellationToken)
    {
        var entry = wait.Entry;
        var keyIndex = entry.KeyIndex;
        foreach (var vout in wait.Remaining.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var check = await _spendLocator!.CheckSpliceOutputAsync(
                            entry, wait.Spend, vout, index => _fundingKeySource.GetFundingPubKey(keyIndex, index),
                            cancellationToken);
            switch (check)
            {
                case { Status: SpliceOutputStatus.Followed, Next: { } next }:
                    _spliceWaits.TryRemove(new KeyValuePair<ChannelId, SpliceWait>(entry.ChannelId, wait));
                    _logger.LogWarning("The funding {FundingTxId}:{Index} of recovery channel {ChannelId} was spliced by "
                                     + "{SpliceTxId}: its output {Vout} was spent with our funding key; following it",
                                       entry.FundingTxId, entry.FundingOutputIndex, entry.ChannelId, wait.SpliceTxId,
                                       vout);
                    return await ContinueAfterSpliceAsync(wait, next, cancellationToken);
                case { Status: SpliceOutputStatus.NotTheChannel }:
                    wait.Remaining.Remove(vout);
                    break;
            }
        }

        if (wait.Remaining.Count == 0)
        {
            _spliceWaits.TryRemove(new KeyValuePair<ChannelId, SpliceWait>(entry.ChannelId, wait));
            _logger.LogWarning("No output of {SpliceTxId}, which spent the funding output of recovery channel "
                             + "{ChannelId}, was spent with our funding key: it is no splice of the channel",
                               wait.SpliceTxId, entry.ChannelId);
            var fundingWatch = new WatchedOutpointModel(entry.FundingTxId, entry.FundingOutputIndex, entry.ChannelId,
                                                        WatchedOutpointPurpose.FundingOutput);
            return await HandOverEarlierSpendAsync(entry, fundingWatch, wait.Spend, cancellationToken);
        }

        _logger.LogWarning("The funding output of recovery channel {ChannelId} was spent by {SpliceTxId} at height "
                         + "{Height}, which is no commitment and whose new funding output is not recognized yet (the "
                         + "peer rotated its funding key?); outputs {Outputs} are checked at every block",
                           entry.ChannelId, wait.SpliceTxId, wait.Spend.BlockHeight, string.Join(", ", wait.Remaining));
        return $"FundingSplicedUnresolved: recovery channel stored; its funding output was spent by {wait.SpliceTxId} "
             + $"at height {wait.Spend.BlockHeight}, which is no commitment (a splice whose new funding key of the "
             + $"peer is unknown): its outputs {string.Join(", ", wait.Remaining)} are checked at every block and "
             + "the channel follows the one the peer's commitment spends with our key; the peer is asked to force "
             + "close";
    }

    /// <summary>
    /// Moves the recovery channel of <paramref name="wait"/> to <paramref name="next"/> (the splice's output found by
    /// its spend), follows any later splice and acts on where that funding stands.
    /// </summary>
    private async Task<string> ContinueAfterSpliceAsync(SpliceWait wait, ChannelBackupEntry next,
                                                        CancellationToken cancellationToken)
    {
        var (current, location, splices) = await FollowSplicesAsync(next, null, cancellationToken);
        if (!await MoveRecoveryChannelAsync(wait.Entry, current, cancellationToken))
            return $"FundingSpliced: the funding was spliced by {wait.SpliceTxId}, but the recovery channel could not "
                 + "be moved to the splice: run restorechanbackup again";

        var moved = new WatchedOutpointModel(current.FundingTxId, current.FundingOutputIndex, current.ChannelId,
                                             WatchedOutpointPurpose.FundingOutput);
        return SplicePrefix([wait.SpliceTxId, .. splices], current)
             + (await HandleLocationAsync(current, moved, location!, cancellationToken)
             ?? "the peer is asked to force close and our output is swept once its commitment confirms");
    }

    /// <inheritdoc />
    public async Task<bool> TryHandleRecoveryFundingSpendAsync(OutpointSpentEventArgs spend,
                                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spend);
        if (_spendLocator is null || _backgroundCts.IsCancellationRequested)
            return false;

        // Only a recovery channel, and only a spend that is no close: a commitment goes to the on-chain watcher
        if (_channelMemoryRepository is not null
         && (!_channelMemoryRepository.TryGetChannel(spend.ChannelId, out var live)
          || !RecoveryChannels.IsRecoveryChannel(live)))
            return false;

        if (SpliceSpendFollower.GetSpliceCandidateOutputs(spend.SpendingTransaction.RawTxBytes) is not
            { Count: > 0 })
            return false;

        await _restoreLock.WaitAsync(cancellationToken);
        try
        {
            if (await LoadRecoveryEntryAsync(spend.ChannelId) is not { } entry
             || (spend.SpentTransactionId is { } spentTxId
              && (spentTxId != entry.FundingTxId || spend.SpentOutputIndex != entry.FundingOutputIndex)))
                return false;

            // A replayed block: the same spend is waited for already
            if (_spliceWaits.TryGetValue(entry.ChannelId, out var waiting)
             && waiting.SpliceTxId == spend.SpendingTransaction.TxId
             && waiting.Entry.FundingTxId == entry.FundingTxId
             && waiting.Entry.FundingOutputIndex == entry.FundingOutputIndex)
                return true;

            var fundingWatch = new WatchedOutpointModel(entry.FundingTxId, entry.FundingOutputIndex, entry.ChannelId,
                                                        WatchedOutpointPurpose.FundingOutput);
            var detail = await HandleEarlierSpendAsync(entry, fundingWatch, cancellationToken,
                                                       new FundingSpendLocation(FundingSpendStatus.SpentFound, spend));
            _logger.LogWarning("The funding output of recovery channel {ChannelId} was spent by {TxId}: {Detail}",
                               entry.ChannelId, spend.SpendingTransaction.TxId, detail);
            return true;
        }
        finally
        {
            _restoreLock.Release();
        }
    }

    /// <summary>
    /// The backup entry of a stored recovery channel (its current funding, key index and pending rows), or null when
    /// the channel is not one (any more).
    /// </summary>
    private async Task<ChannelBackupEntry?> LoadRecoveryEntryAsync(ChannelId channelId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel is null || !RecoveryChannels.IsRecoveryChannel(channel)
                            || channel.FundingOutput is not { TransactionId: not null, Index: not null }
                            || channel.RemoteKeySet is null)
            return null;

        return ChannelBackupService.CreateEntry(channel, null, null,
                                                await ChannelBackupService.GetFundingSetAsync(unitOfWork, channelId,
                                                                                              _logger));
    }

    private void HandleNewBlock(object? sender, NewBlockEventArgs args)
    {
        if (!_spliceWaits.IsEmpty)
            ScheduleSpliceWaitRound();
    }

    /// <summary>Runs <see cref="CheckSpliceWaitsAsync"/> in the background, one round at a time, coalesced.</summary>
    private void ScheduleSpliceWaitRound()
    {
        lock (_spliceWaitGate)
        {
            if (_backgroundCts.IsCancellationRequested)
                return;

            if (_spliceWaitRound is not null)
            {
                _spliceWaitRoundRequested = true;
                return;
            }

            var token = _backgroundCts.Token;
            _spliceWaitRound = Task.Run(() => RunSpliceWaitRoundsAsync(token), token);
        }
    }

    private async Task RunSpliceWaitRoundsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await CheckSpliceWaitsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopping: the next start of the node looks the spends up again (ResumeSpendSearches)
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not check the unresolved splices of the recovery channels");
            }

            lock (_spliceWaitGate)
            {
                if (!_spliceWaitRoundRequested || cancellationToken.IsCancellationRequested)
                {
                    _spliceWaitRound = null;
                    return;
                }

                _spliceWaitRoundRequested = false;
            }
        }
    }

    /// <summary>
    /// One round over the recovery channels waiting for an unresolved splice (after every block): each is checked
    /// again (<see cref="CheckSpliceWaitAsync"/>) under the restore lock, unless it is no longer a recovery channel at
    /// that funding.
    /// </summary>
    internal async Task CheckSpliceWaitsAsync(CancellationToken cancellationToken)
    {
        foreach (var (channelId, wait) in _spliceWaits.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _restoreLock.WaitAsync(cancellationToken);
            try
            {
                if (!_spliceWaits.TryGetValue(channelId, out var current) || !ReferenceEquals(current, wait))
                    continue;

                if (await LoadRecoveryEntryAsync(channelId) is not { } stored
                 || stored.FundingTxId != wait.Entry.FundingTxId
                 || stored.FundingOutputIndex != wait.Entry.FundingOutputIndex)
                {
                    _spliceWaits.TryRemove(new KeyValuePair<ChannelId, SpliceWait>(channelId, wait));
                    continue;
                }

                var detail = await CheckSpliceWaitAsync(wait, cancellationToken);
                if (!_spliceWaits.ContainsKey(channelId))
                    _logger.LogWarning("Recovery channel {ChannelId}: {Detail}", channelId, detail);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Could not check the unresolved splice of recovery channel {ChannelId}", channelId);
            }
            finally
            {
                _restoreLock.Release();
            }
        }
    }

    /// <summary>
    /// A recovery channel whose funding output was spent by <see cref="Spend"/>, a transaction that is no commitment
    /// and was not recognized as a splice yet: <see cref="Remaining"/> are its P2WSH outputs not ruled out. Touched only
    /// under the restore lock.
    /// </summary>
    private sealed class SpliceWait(ChannelBackupEntry entry, OutpointSpentEventArgs spend,
                                    IReadOnlyList<ushort> outputs)
    {
        public ChannelBackupEntry Entry { get; } = entry;
        public OutpointSpentEventArgs Spend { get; } = spend;
        public TxId SpliceTxId => Spend.SpendingTransaction.TxId;
        public List<ushort> Remaining { get; } = outputs.ToList();
    }

    private async Task<string> HandOverEarlierSpendAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch,
                                                         OutpointSpentEventArgs spend,
                                                         CancellationToken cancellationToken)
    {
        var spentBy = $"{spend.SpendingTransaction.TxId} at height {spend.BlockHeight}";
        _logger.LogWarning("The funding output of recovery channel {ChannelId} was already spent by {SpentBy}; "
                         + "resolving its outputs", entry.ChannelId, spentBy);
        if (_onchainWatcher is null)
        {
            _logger.LogCritical("No on-chain watcher is registered to resolve recovery channel {ChannelId}",
                                entry.ChannelId);
            return $"FundingAlreadySpent: recovery channel stored; the peer already closed it ({spentBy}), but no "
                 + "on-chain watcher is registered to sweep our output";
        }

        try
        {
            await MarkWatchSpentAsync(fundingWatch, spend);
            await _onchainWatcher.HandleFundingSpentAsync(spend, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Could not hand the earlier funding spend of recovery channel {ChannelId} to the "
                              + "on-chain watcher", entry.ChannelId);
            return $"FundingAlreadySpent: recovery channel stored; the peer already closed it ({spentBy}), but "
                 + $"handling that failed ({e.Message}): rescan from height {spend.BlockHeight}";
        }

        return $"recovery channel stored; the peer already closed it ({spentBy}): our output is swept from that "
             + "transaction";
    }

    private bool IsRescanRunning(ChannelId channelId) =>
        _rescans.TryGetValue(channelId, out var running) && !running.IsCompleted;

    /// <summary>
    /// Starts the background search for a funding spend older than the recent window (NL-430), unless one runs for
    /// the channel. Restores run one at a time, so the check and the start don't race.
    /// </summary>
    private void StartRescan(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch, uint belowHeight)
    {
        if (IsRescanRunning(entry.ChannelId))
            return;

        var token = _backgroundCts.Token;
        _rescans[entry.ChannelId] = Task.Run(() => RescanAsync(entry, fundingWatch, belowHeight, token), token);
    }

    private async Task RescanAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch, uint belowHeight,
                                   CancellationToken cancellationToken)
    {
        try
        {
            var location = await _spendLocator!.RescanAsync(entry, belowHeight, cancellationToken);
            for (var hops = 0;
                 location is { Status: FundingSpendStatus.SpentFound } && hops < SpliceSpendFollower.MaxSplices;
                 hops++)
            {
                // A splice of the channel found below the window: the channel moves to it, and its funding is
                // searched in turn (NL-478)
                var (current, followed, splices) = await FollowSplicesAsync(entry, location, cancellationToken);
                if (splices.Count == 0)
                    break;

                if (!await MoveRecoveryChannelAsync(entry, current, cancellationToken))
                {
                    _logger.LogError("Recovery channel {ChannelId} was spliced by {SpliceTxId} but could not be moved "
                                   + "to the splice; run restorechanbackup again", entry.ChannelId, splices[0]);
                    return;
                }

                entry = current;
                fundingWatch = new WatchedOutpointModel(current.FundingTxId, current.FundingOutputIndex,
                                                        current.ChannelId, WatchedOutpointPurpose.FundingOutput);
                location = followed is { HasOlderBlocksToSearch: true }
                               ? await _spendLocator.RescanAsync(current, followed.SearchedFromHeight,
                                                                 cancellationToken)
                               : followed;
                if (location is null or { Status: not FundingSpendStatus.SpentFound })
                    break;
            }

            switch (location?.Status)
            {
                case FundingSpendStatus.SpentFound when location.Spend is { } spend:
                    string detail;
                    await _restoreLock.WaitAsync(cancellationToken);
                    try
                    {
                        detail = await HandleSpentFoundAsync(entry, fundingWatch, spend, cancellationToken);
                    }
                    finally
                    {
                        _restoreLock.Release();
                    }

                    _logger.LogWarning("Background search for the funding spend of recovery channel {ChannelId}: "
                                     + "{Detail}", entry.ChannelId, detail);
                    break;
                case FundingSpendStatus.SpentNotFound:
                    _logger.LogError("The funding output of recovery channel {ChannelId} is spent, but the spend is in "
                                   + "no block from {Floor} to the tip: sweep our output by hand", entry.ChannelId,
                                     location.FloorHeight);
                    break;
                case FundingSpendStatus.NotConfirmed:
                    _logger.LogWarning("The funding transaction of recovery channel {ChannelId} is in no block from "
                                     + "{Floor} to the tip: it may never have confirmed", entry.ChannelId,
                                       location.FloorHeight);
                    break;
                case FundingSpendStatus.BlocksPruned:
                    _logger.LogError("Background search for the funding spend of recovery channel {ChannelId}: {Detail}",
                                     entry.ChannelId, PrunedDetail(location));
                    break;
                case FundingSpendStatus.Unspent:
                    _logger.LogWarning("The funding output of recovery channel {ChannelId} is unspent again (a reorg?); "
                                     + "the chain monitor watches it", entry.ChannelId);
                    break;
                default:
                    _logger.LogError("The background search for the funding spend of recovery channel {ChannelId} "
                                   + "failed ({Error}); it starts again at the next start of the node, or run "
                                   + "restorechanbackup again to retry it now", entry.ChannelId,
                                     location?.Error ?? "no result");
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the next start of the node searches again (ResumeSpendSearches)
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The background search for the funding spend of recovery channel {ChannelId} failed; "
                              + "it starts again at the next start of the node, or run restorechanbackup again to "
                              + "retry it now", entry.ChannelId);
        }
    }

    /// <summary>Records the earlier spend on the funding watch, as the chain monitor would have.</summary>
    private async Task MarkWatchSpentAsync(WatchedOutpointModel fundingWatch, OutpointSpentEventArgs spend)
    {
        if (spend.BlockHash is not { } blockHash)
            return;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.WatchedOutpointDbRepository.MarkSpentAsync(fundingWatch.TransactionId,
                                                                     fundingWatch.OutputIndex,
                                                                     spend.SpendingTransaction.TxId,
                                                                     spend.BlockHeight, blockHash);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>The peer row of a restored channel: the backup's first address (empty when it has none).</summary>
    private static PeerModel CreatePeer(ChannelBackupEntry entry)
    {
        var address = entry.Addresses.FirstOrDefault();
        return new PeerModel(entry.RemoteNodeId, address?.Host ?? string.Empty, address?.Port ?? 0u,
                             address?.Type ?? "IPv4")
        {
            LastSeenAt = DateTime.UtcNow
        };
    }

    private async Task<ChannelRestorePeerResult> ConnectAsync(CompactPubKey nodeId, ChannelBackupEntry entry,
                                                              bool onlyIfDisconnected,
                                                              CancellationToken cancellationToken)
    {
        var addresses = await GetAddressesAsync(nodeId, entry);

        // A connection that exists already sent its channel_reestablish before the channel existed: a new one sends
        // the data-loss reestablish. A recovery channel restored earlier was registered before it: nothing to do
        if (_peerManager.GetPeer(nodeId) is not null)
        {
            if (onlyIfDisconnected)
                return new ChannelRestorePeerResult(nodeId, null, true, null);

            _peerManager.DisconnectPeer(nodeId);
            var deadline = DateTime.UtcNow + DisconnectTimeout;
            while (_peerManager.GetPeer(nodeId) is not null && DateTime.UtcNow < deadline)
                await Task.Delay(s_disconnectPollInterval, cancellationToken);

            // Still connected: no new connection ran, so no data-loss channel_reestablish went out
            if (_peerManager.GetPeer(nodeId) is not null)
            {
                _logger.LogWarning("Peer {Peer} did not disconnect within {Timeout}; the close is asked for on its "
                                 + "next connection", nodeId, DisconnectTimeout);
                return new ChannelRestorePeerResult(nodeId, addresses.FirstOrDefault(), false,
                                                    $"the existing connection did not close within "
                                                  + $"{DisconnectTimeout}: the close is asked for on the peer's next "
                                                  + "connection");
            }
        }

        // The restore holds its lock while it connects: the first address is always tried, the next ones only within
        // ConnectBudget; those left go to the background reconnection at once
        var errors = new List<string>(addresses.Count);
        var started = _timeProvider.GetUtcNow();
        var tried = 0;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tried > 0 && _timeProvider.GetUtcNow() - started >= ConnectBudget)
                break;

            tried++;
            if (await TryConnectAsync(nodeId, address) is not { } error)
                return new ChannelRestorePeerResult(nodeId, address, true, null);

            errors.Add(addresses.Count == 1 ? error : $"{address}: {error}");
        }

        var untried = addresses.Skip(tried).ToList();
        StartReconnect(nodeId, entry, untried);
        if (addresses.Count == 0)
            return new ChannelRestorePeerResult(nodeId, null, false,
                                                "no address known: retried in the background as the gossip graph "
                                              + "learns one; the close is also asked for when the peer connects");

        var pending = untried.Count > 0
                          ? $"{untried.Count} more known address(es) tried in the background now, then all of them "
                          : "retried in the background";
        return new ChannelRestorePeerResult(nodeId, addresses[0], false,
                                            $"{string.Join("; ", errors)} ({pending}; the close is also asked for "
                                          + "when the peer connects)");
    }

    /// <summary>One connection attempt; null when the peer is connected afterwards, else why not.</summary>
    private async Task<string?> TryConnectAsync(CompactPubKey nodeId, string address)
    {
        try
        {
            await _peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{nodeId}@{address}"));
            return null;
        }
        catch (InvalidOperationException)
        {
            // The peer connected to us again first (the old connection is gone, checked by the caller): that new
            // connection ran after the channel was registered, so it sent the data-loss reestablish
            return null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not connect to peer {Peer} at {Address} after the restore", nodeId, address);
            return e.Message;
        }
    }

    /// <summary>
    /// Keeps trying every known address of an unreachable peer, with the node's reconnect backoff, until it is
    /// connected (by us or by itself), none of its recovery channels waits for its close any more, or the node stops.
    /// </summary>
    /// <param name="nodeId">The peer.</param>
    /// <param name="entry">A backed-up channel with the peer (its addresses).</param>
    /// <param name="untried">Addresses the restore did not get to (<see cref="ConnectBudget"/>): tried at once, before
    /// the first wait.</param>
    private void StartReconnect(CompactPubKey nodeId, ChannelBackupEntry entry, IReadOnlyList<string> untried)
    {
        if (_reconnects.TryGetValue(nodeId, out var running) && !running.IsCompleted)
            return;

        var token = _backgroundCts.Token;
        _reconnects[nodeId] = Task.Run(() => ReconnectAsync(nodeId, entry, untried, token), token);
    }

    private async Task ReconnectAsync(CompactPubKey nodeId, ChannelBackupEntry entry, IReadOnlyList<string> untried,
                                      CancellationToken cancellationToken)
    {
        var delay = ReconnectInitialDelay > TimeSpan.Zero ? ReconnectInitialDelay : TimeSpan.FromSeconds(5);
        var maxDelay = ReconnectMaxDelay >= delay ? ReconnectMaxDelay : delay;
        try
        {
            var first = untried.Count > 0;
            while (true)
            {
                if (!first)
                    await Task.Delay(delay, cancellationToken);

                if (_peerManager.GetPeer(nodeId) is not null || !HasWaitingRecoveryChannel(nodeId))
                    return;

                var immediate = first;
                var round = first ? untried : await GetAddressesAsync(nodeId, entry);
                first = false;
                foreach (var address in round)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await TryConnectAsync(nodeId, address) is null)
                    {
                        _logger.LogInformation("Connected to peer {Peer} at {Address}: the close of its recovery "
                                             + "channels is asked for", nodeId, address);
                        return;
                    }
                }

                if (!immediate)
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
                _logger.LogDebug("Peer {Peer} of restored channels still unreachable, retrying in {Delay}", nodeId,
                                 delay);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: every start connects to the peers of stored channels
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Stopped reconnecting to peer {Peer} of restored channels", nodeId);
        }
    }

    /// <summary>
    /// Whether a channel with <paramref name="nodeId"/> is a recovery channel still waiting for its close (true
    /// when no channel memory is registered).
    /// </summary>
    private bool HasWaitingRecoveryChannel(CompactPubKey nodeId) =>
        _channelMemoryRepository is null
     || _channelMemoryRepository.FindChannels(c => c.RemoteNodeId == nodeId && RecoveryChannels.IsRecoveryChannel(c))
                                .Count > 0;

    /// <summary>
    /// Every address known for the peer, most trusted first, without repeats (NL-431): its own
    /// <c>node_announcement</c> in the gossip graph (IPv4, IPv6 and DNS; Tor is not supported), the stored peer row
    /// (it may be newer than the backup's), then every address of the backup.
    /// </summary>
    internal async Task<IReadOnlyList<string>> GetAddressesAsync(CompactPubKey nodeId, ChannelBackupEntry entry)
    {
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? host, uint port)
        {
            if (string.IsNullOrWhiteSpace(host) || port is 0 or > ushort.MaxValue)
                return;

            var address = FormatAddress(host, port);
            if (seen.Add(address))
                addresses.Add(address);
        }

        try
        {
            if (_graphStore is not null && _graphStore.TryGetNode(nodeId, out var node))
                foreach (var descriptor in ChannelBackupService.ConnectableAddresses(node))
                    Add(descriptor.Host, descriptor.Port);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the graph node {Peer}", nodeId);
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            // An inbound-only peer's row holds no address of it (NL-497)
            if (await unitOfWork.PeerDbRepository.GetByNodeIdAsync(nodeId) is { IsInboundOnly: false } peer)
                Add(peer.Host, peer.Port);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read peer {Peer}", nodeId);
        }

        foreach (var address in entry.Addresses)
            Add(address.Host, address.Port);

        return addresses;
    }

    private static string FormatAddress(string host, uint port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
}