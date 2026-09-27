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
/// <see cref="IOnchainChannelWatcher"/> like one the monitor saw; one not found within the search depth is reported
/// in the channel's result with the height to rescan from.</para>
/// <para>Before anything is stored, the key manager's last used channel index is advanced past the restored channels'
/// (<see cref="IChannelKeyIndexReserver"/>), so a stale key file never hands a restored channel's keys to a new
/// one.</para>
/// </remarks>
public sealed class ChannelRestoreService : IChannelRestoreService
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
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    /// <summary>How long a restore waits for a connected peer's connection to close before it connects again.</summary>
    internal TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public ChannelRestoreService(IChannelBackupService backupService, IChannelManager channelManager,
                                 IMessageFactory messageFactory, IMessageSerializer messageSerializer,
                                 IOptions<NodeOptions> nodeOptions, IOutpointWatcher outpointWatcher,
                                 IPeerManager peerManager, ISecureKeyManager secureKeyManager,
                                 IServiceScopeFactory serviceScopeFactory, ISha256 sha256, ILightningSigner signer,
                                 ILogger<ChannelRestoreService>? logger = null,
                                 IFundingSpendLocator? spendLocator = null,
                                 IOnchainChannelWatcher? onchainWatcher = null,
                                 IChannelKeyIndexReserver? keyIndexReserver = null)
    {
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
                                                  id => existing.TryGetValue(id, out var state)
                                                            ? (true, state)
                                                            : (false, null),
                                                  _signer.GetChannelBasepoints);

            await ReserveKeyIndexesAsync(plan, cancellationToken);

            var results = new List<ChannelRestoreChannelResult>(plan.Count);
            var restoredPeers = new Dictionary<CompactPubKey, ChannelBackupEntry>();
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ApplyAsync(item, cancellationToken);
                results.Add(result);
                if (result.Action == ChannelRestoreAction.Restore)
                    restoredPeers.TryAdd(item.Entry.RemoteNodeId, item.Entry);
            }

            var peers = new List<ChannelRestorePeerResult>(restoredPeers.Count);
            foreach (var (nodeId, entry) in restoredPeers)
                peers.Add(await ConnectAsync(nodeId, entry, cancellationToken));

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

    private async Task<Dictionary<ChannelId, ChannelState?>> LoadExistingStatesAsync(
        IReadOnlyList<ChannelBackupEntry> entries)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var states = new Dictionary<ChannelId, ChannelState?>();
        foreach (var entry in entries)
        {
            if (states.ContainsKey(entry.ChannelId))
                continue;

            try
            {
                if (await unitOfWork.ChannelDbRepository.GetByIdAsync(entry.ChannelId) is { } channel)
                    states[entry.ChannelId] = channel.State;
            }
            catch (Exception e)
            {
                // A row the repository refuses (legacy HTLC state) still exists: never overwrite it
                _logger.LogWarning(e, "Channel {ChannelId} is in the database but can't be read; not restoring it",
                                   entry.ChannelId);
                states[entry.ChannelId] = null;
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
                     + $"already spent could not be checked ({location.Error})";
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
                                                              CancellationToken cancellationToken)
    {
        var address = await GetAddressAsync(nodeId, entry);

        // A connection that exists already sent its channel_reestablish before the channel existed: a new one sends
        // the data-loss reestablish
        if (_peerManager.GetPeer(nodeId) is not null)
        {
            _peerManager.DisconnectPeer(nodeId);
            var deadline = DateTime.UtcNow + DisconnectTimeout;
            while (_peerManager.GetPeer(nodeId) is not null && DateTime.UtcNow < deadline)
                await Task.Delay(s_disconnectPollInterval, cancellationToken);

            // Still connected: no new connection ran, so no data-loss channel_reestablish went out
            if (_peerManager.GetPeer(nodeId) is not null)
            {
                _logger.LogWarning("Peer {Peer} did not disconnect within {Timeout}; the close is asked for on its "
                                 + "next connection", nodeId, DisconnectTimeout);
                return new ChannelRestorePeerResult(nodeId, address, false,
                                                    $"the existing connection did not close within "
                                                  + $"{DisconnectTimeout}: the close is asked for on the peer's next "
                                                  + "connection");
            }
        }

        if (address is null)
            return new ChannelRestorePeerResult(nodeId, null, false,
                                                "no address known: the close is asked for when the peer connects");

        try
        {
            await _peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{nodeId}@{address}"));
            return new ChannelRestorePeerResult(nodeId, address, true, null);
        }
        catch (InvalidOperationException)
        {
            // The peer connected to us again first (the old connection is gone, checked above): that new connection
            // ran after the channel was registered, so it sent the data-loss reestablish
            return new ChannelRestorePeerResult(nodeId, address, true, null);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not connect to peer {Peer} at {Address} after the restore; the close is "
                                + "asked for on its next connection", nodeId, address);
            return new ChannelRestorePeerResult(nodeId, address, false, e.Message);
        }
    }

    /// <summary>The stored peer's address (it may be newer than the backup's), else the backup's first one.</summary>
    private async Task<string?> GetAddressAsync(CompactPubKey nodeId, ChannelBackupEntry entry)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await unitOfWork.PeerDbRepository.GetByNodeIdAsync(nodeId) is { Port: > 0 } peer
             && !string.IsNullOrWhiteSpace(peer.Host))
                return FormatAddress(peer.Host, peer.Port);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read peer {Peer}", nodeId);
        }

        return entry.Addresses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Host) && a.Port > 0) is { } address
                   ? FormatAddress(address.Host, address.Port)
                   : null;
    }

    private static string FormatAddress(string host, uint port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
}