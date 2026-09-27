using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
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
/// so. Running <c>restorechanbackup</c> again looks the spend up again for every recovery channel still Failed (after
/// a restart that interrupted the background search, or a chain error).</para>
/// <para>A peer is tried at every address known for it (NL-431): its <c>node_announcement</c> in the gossip graph,
/// the peer row, then every address of the backup. When none answers, a background loop retries them (the graph
/// read again each round) with the node's reconnect backoff until the peer is connected, its recovery channels are
/// no longer waiting for its close, or the node stops.</para>
/// <para>Before anything is stored, the key manager's last used channel index is advanced past the restored channels'
/// (<see cref="IChannelKeyIndexReserver"/>), so a stale key file never hands a restored channel's keys to a new
/// one.</para>
/// </remarks>
public sealed class ChannelRestoreService : IChannelRestoreService, IDisposable
{
    private static readonly TimeSpan s_disconnectPollInterval = TimeSpan.FromMilliseconds(100);

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
    private readonly SemaphoreSlim _restoreLock = new(1, 1);
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly ConcurrentDictionary<ChannelId, Task> _rescans = new();
    private readonly ConcurrentDictionary<CompactPubKey, Task> _reconnects = new();

    /// <summary>How long a restore waits for a connected peer's connection to close before it connects again.</summary>
    internal TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

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
                                 IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _graphStore = graphStore;
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
    }

    /// <summary>Stops the background spend searches and reconnections.</summary>
    public void Dispose()
    {
        if (!_backgroundCts.IsCancellationRequested)
            _backgroundCts.Cancel();
        _backgroundCts.Dispose();
        _restoreLock.Dispose();
    }

    /// <summary>Completes when every background spend search and reconnection started so far has ended.</summary>
    internal Task WaitForBackgroundWorkAsync() => Task.WhenAll(_rescans.Values.Concat(_reconnects.Values));

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
                                                  _signer.GetChannelBasepoints);

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

        ChannelModel channel;
        WatchedOutpointModel fundingWatch;
        try
        {
            channel = RecoveryChannels.Create(entry, item.LocalBasepoints!.Value, _sha256);
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
        var spendDetail = await HandleEarlierSpendAsync(entry, fundingWatch, cancellationToken);
        return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.Restore,
                                               spendDetail
                                            ?? "recovery channel stored; the peer is asked to force close and our "
                                             + "output is swept once its commitment confirms");
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
    /// watcher. Returns the channel's result detail when the funding output is not simply unspent (null otherwise).
    /// </summary>
    private async Task<string?> HandleEarlierSpendAsync(ChannelBackupEntry entry, WatchedOutpointModel fundingWatch,
                                                        CancellationToken cancellationToken)
    {
        if (_spendLocator is null)
            return null;

        var location = await _spendLocator.LocateAsync(entry, cancellationToken);
        switch (location.Status)
        {
            case FundingSpendStatus.SpentFound when location.Spend is { } spend:
                return await HandOverEarlierSpendAsync(entry, fundingWatch, spend, cancellationToken);
            case FundingSpendStatus.SpentNotFound when location.HasOlderBlocksToSearch && _onchainWatcher is not null:
                StartRescan(entry, fundingWatch, location.SearchedFromHeight);
                _logger.LogWarning("The funding output of recovery channel {ChannelId} is already spent, but not in the "
                                 + "blocks from {Searched} to the tip; searching blocks {Below} down to {Floor} in the "
                                 + "background", entry.ChannelId, location.SearchedFromHeight,
                                   location.SearchedFromHeight - 1, location.FloorHeight);
                return "FundingSpendRescan: recovery channel stored; its funding output is already spent and the spend "
                     + $"is not in the recent blocks searched (from {location.SearchedFromHeight} to the tip): blocks "
                     + $"{location.SearchedFromHeight - 1} down to {location.FloorHeight} are searched in the "
                     + "background and our output is swept once the spend is found";
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
            switch (location?.Status)
            {
                case FundingSpendStatus.SpentFound when location.Spend is { } spend:
                    var detail = await HandOverEarlierSpendAsync(entry, fundingWatch, spend, cancellationToken);
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
                case FundingSpendStatus.Unspent:
                    _logger.LogWarning("The funding output of recovery channel {ChannelId} is unspent again (a reorg?); "
                                     + "the chain monitor watches it", entry.ChannelId);
                    break;
                default:
                    _logger.LogError("The background search for the funding spend of recovery channel {ChannelId} "
                                   + "failed ({Error}); run restorechanbackup again to retry it", entry.ChannelId,
                                     location?.Error ?? "no result");
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the next restorechanbackup searches again
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The background search for the funding spend of recovery channel {ChannelId} failed; "
                              + "run restorechanbackup again to retry it", entry.ChannelId);
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

        var errors = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryConnectAsync(nodeId, address) is not { } error)
                return new ChannelRestorePeerResult(nodeId, address, true, null);

            errors.Add(addresses.Count == 1 ? error : $"{address}: {error}");
        }

        StartReconnect(nodeId, entry);
        return addresses.Count == 0
                   ? new ChannelRestorePeerResult(nodeId, null, false,
                                                  "no address known: retried in the background as the gossip graph "
                                                + "learns one; the close is also asked for when the peer connects")
                   : new ChannelRestorePeerResult(nodeId, addresses[0], false,
                                                  $"{string.Join("; ", errors)} (retried in the background; the close "
                                                + "is also asked for when the peer connects)");
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
    private void StartReconnect(CompactPubKey nodeId, ChannelBackupEntry entry)
    {
        if (_reconnects.TryGetValue(nodeId, out var running) && !running.IsCompleted)
            return;

        var token = _backgroundCts.Token;
        _reconnects[nodeId] = Task.Run(() => ReconnectAsync(nodeId, entry, token), token);
    }

    private async Task ReconnectAsync(CompactPubKey nodeId, ChannelBackupEntry entry,
                                      CancellationToken cancellationToken)
    {
        var delay = ReconnectInitialDelay > TimeSpan.Zero ? ReconnectInitialDelay : TimeSpan.FromSeconds(5);
        var maxDelay = ReconnectMaxDelay >= delay ? ReconnectMaxDelay : delay;
        try
        {
            while (true)
            {
                await Task.Delay(delay, cancellationToken);
                if (_peerManager.GetPeer(nodeId) is not null || !HasWaitingRecoveryChannel(nodeId))
                    return;

                foreach (var address in await GetAddressesAsync(nodeId, entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await TryConnectAsync(nodeId, address) is null)
                    {
                        _logger.LogInformation("Connected to peer {Peer} at {Address}: the close of its recovery "
                                             + "channels is asked for", nodeId, address);
                        return;
                    }
                }

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
            if (await unitOfWork.PeerDbRepository.GetByNodeIdAsync(nodeId) is { } peer)
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