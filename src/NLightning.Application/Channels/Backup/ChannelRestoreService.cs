using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

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
/// <para>Known gap: a funding output the peer spent before the restore is not found (the chain monitor watches from
/// its current height on); the operator must rescan from <see cref="ChannelBackupEntry.FundingHeight"/>.</para>
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
    private readonly SemaphoreSlim _restoreLock = new(1, 1);

    /// <summary>How long a restore waits for a connected peer's connection to close before it connects again.</summary>
    internal TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public ChannelRestoreService(IChannelBackupService backupService, IChannelManager channelManager,
                                 IMessageFactory messageFactory, IMessageSerializer messageSerializer,
                                 IOptions<NodeOptions> nodeOptions, IOutpointWatcher outpointWatcher,
                                 IPeerManager peerManager, ISecureKeyManager secureKeyManager,
                                 IServiceScopeFactory serviceScopeFactory, ISha256 sha256, ILightningSigner signer,
                                 ILogger<ChannelRestoreService>? logger = null)
    {
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

            var results = new List<ChannelRestoreChannelResult>(plan.Count);
            var restoredPeers = new Dictionary<CompactPubKey, ChannelBackupEntry>();
            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ApplyAsync(item);
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

    private async Task<ChannelRestoreChannelResult> ApplyAsync(ChannelRestorePlanItem item)
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
        return new ChannelRestoreChannelResult(entry, ChannelRestoreAction.Restore,
                                               "recovery channel stored; the peer is asked to force close and our "
                                             + "output is swept once its commitment confirms");
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
            // The peer connected to us first: that connection sent the data-loss reestablish
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