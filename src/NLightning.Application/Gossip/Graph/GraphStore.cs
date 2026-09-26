using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Graph;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Gossip.Persistence;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Payloads;
using Interfaces;
using GraphVerification = Domain.Gossip.Graph.GraphChannelVerification;
using StoredVerification = Domain.Gossip.Persistence.GraphChannelVerification;

/// <inheritdoc cref="IGraphStore"/>
/// <remarks>
/// <para>
/// Write-behind (plan D2): every change marks its key dirty; <see cref="FlushAsync"/> writes the current value of every
/// dirty key (or deletes it) in one unit of work of its own scope, so a burst of updates to one channel costs one row
/// write. A crash loses at most the changes since the last flush, which peers send again (the plan accepts this).
/// </para>
/// <para>
/// Not persisted: the funding transaction ids (<see cref="TryGetFundingTxId"/>; the graph tables have no column for
/// them), which the chain check, our own channel or the <see cref="GraphPruner"/>'s startup lookup
/// (<see cref="TrySetFundingTxId"/>) fills in again after a restart.
/// </para>
/// </remarks>
public sealed class GraphStore : IGraphStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GraphStore> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Dictionary<ShortChannelId, GraphChannel> _channels = new();
    private readonly Dictionary<ShortChannelId, DateTimeOffset> _channelReceivedAt = new();
    private readonly Dictionary<ShortChannelId, TxId> _fundingTxIds = new();
    private readonly Dictionary<(TxId, uint), ShortChannelId> _channelsByFundingOutpoint = new();
    private readonly Dictionary<CompactPubKey, GraphNode> _nodes = new();
    private readonly Dictionary<CompactPubKey, DateTimeOffset> _nodeReceivedAt = new();
    private readonly Dictionary<CompactPubKey, int> _channelCountByNode = new();
    private readonly Dictionary<CompactPubKey, GraphBannedNodeRecord> _bans = new();

    private readonly HashSet<ShortChannelId> _dirtyChannels = [];
    private readonly HashSet<(ShortChannelId, byte)> _dirtyPolicies = [];
    private readonly HashSet<ShortChannelId> _deletedChannels = [];
    private readonly HashSet<CompactPubKey> _dirtyNodes = [];
    private readonly HashSet<CompactPubKey> _deletedNodes = [];
    private readonly HashSet<CompactPubKey> _dirtyBans = [];

    private long _version;
    private long _snapshotVersion = -1;
    private GraphSnapshot _snapshot = GraphSnapshot.Empty;
    private volatile bool _isLoaded;

    /// <inheritdoc />
    public event EventHandler<GraphChannel>? ChannelAdded;

    public GraphStore(IServiceScopeFactory scopeFactory, ILogger<GraphStore> logger, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsLoaded => _isLoaded;

    /// <inheritdoc />
    public int ChannelCount
    {
        get
        {
            lock (_lock)
                return _channels.Count;
        }
    }

    /// <inheritdoc />
    public int NodeCount
    {
        get
        {
            lock (_lock)
                return _nodes.Count;
        }
    }

    /// <inheritdoc />
    public int PendingChanges
    {
        get
        {
            lock (_lock)
                return _dirtyChannels.Count + _dirtyPolicies.Count + _deletedChannels.Count + _dirtyNodes.Count
                     + _deletedNodes.Count + _dirtyBans.Count;
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded)
            return;

        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            if (_isLoaded)
                return;

            IReadOnlyList<GraphChannelRecord> channels;
            IReadOnlyList<GraphPolicyRecord> policies;
            IReadOnlyList<GraphNodeRecord> nodes;
            IReadOnlyList<GraphBannedNodeRecord> bans;
            using (var scope = _scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().GraphDbRepository;
                channels = await repository.GetChannelsAsync(cancellationToken);
                policies = await repository.GetAllPoliciesAsync(cancellationToken);
                nodes = await repository.GetNodesAsync(cancellationToken);
                bans = await repository.GetActiveBansAsync(_timeProvider.GetUtcNow());
            }

            var skipped = 0;
            lock (_lock)
            {
                foreach (var record in channels)
                {
                    // Changes made before the load are newer than the database
                    if (_channels.ContainsKey(record.ShortChannelId) || _deletedChannels.Contains(record.ShortChannelId))
                        continue;

                    var channel = MapChannel(record);
                    if (channel is null)
                    {
                        skipped++;
                        continue;
                    }

                    _channels[record.ShortChannelId] = channel;
                    _channelReceivedAt[record.ShortChannelId] = record.ReceivedAt;
                    CountChannelEnds(channel, +1);
                }

                foreach (var record in policies)
                {
                    if (!_channels.TryGetValue(record.ShortChannelId, out var channel))
                        continue;

                    var policy = MapPolicy(record);
                    var current = channel.GetPolicy(policy.Direction);
                    if (current is null || current.Timestamp < policy.Timestamp)
                        _channels[record.ShortChannelId] = channel.WithPolicy(policy);
                }

                foreach (var record in nodes)
                {
                    if (_nodes.ContainsKey(record.NodeId) || _deletedNodes.Contains(record.NodeId))
                        continue;

                    _nodes[record.NodeId] = MapNode(record);
                    _nodeReceivedAt[record.NodeId] = record.ReceivedAt;
                }

                foreach (var ban in bans)
                    _bans.TryAdd(ban.NodeId, ban);

                _version++;
                _isLoaded = true;
            }

            _logger.LogInformation("Loaded the graph: {Channels} channels, {Policies} policies, {Nodes} nodes{Skipped}",
                                   channels.Count - skipped, policies.Count, nodes.Count,
                                   skipped > 0 ? $" ({skipped} unreadable channels skipped)" : "");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            FlushWork work;
            lock (_lock)
            {
                work = TakeWorkLocked();
            }

            if (work.IsEmpty)
                return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var repository = unitOfWork.GraphDbRepository;
                foreach (var shortChannelId in work.DeletedChannels)
                    await repository.DeleteChannelAsync(shortChannelId);
                foreach (var nodeId in work.DeletedNodes)
                    await repository.DeleteNodeAsync(nodeId);
                foreach (var channel in work.Channels)
                    await repository.UpsertChannelAsync(channel);
                foreach (var policy in work.Policies)
                    await repository.UpsertPolicyAsync(policy);
                foreach (var node in work.Nodes)
                    await repository.UpsertNodeAsync(node);
                foreach (var ban in work.Bans)
                    await repository.UpsertBanAsync(ban);

                await unitOfWork.SaveChangesAsync();
                _logger.LogDebug(
                    "Graph flushed: {Channels} channels, {Policies} policies, {Nodes} nodes, {Deleted} deletions",
                    work.Channels.Count, work.Policies.Count, work.Nodes.Count,
                    work.DeletedChannels.Count + work.DeletedNodes.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Failed to write the graph; the changes stay pending");
                lock (_lock)
                    RestoreWorkLocked(work);
            }
            catch
            {
                lock (_lock)
                    RestoreWorkLocked(work);
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc />
    public IGraphView GetSnapshot()
    {
        lock (_lock)
        {
            if (_snapshotVersion != _version)
            {
                _snapshot = new GraphSnapshot(_channels.Values.ToList(), _nodes.Values.ToList());
                _snapshotVersion = _version;
            }

            return _snapshot;
        }
    }

    /// <inheritdoc />
    public bool TryGetChannel(ShortChannelId shortChannelId, [NotNullWhen(true)] out GraphChannel? channel)
    {
        lock (_lock)
            return _channels.TryGetValue(shortChannelId, out channel);
    }

    /// <inheritdoc />
    public bool TryGetNode(CompactPubKey nodeId, [NotNullWhen(true)] out GraphNode? node)
    {
        lock (_lock)
            return _nodes.TryGetValue(nodeId, out node);
    }

    /// <inheritdoc />
    public bool NodeHasChannels(CompactPubKey nodeId)
    {
        lock (_lock)
            return _channelCountByNode.ContainsKey(nodeId);
    }

    /// <inheritdoc />
    public bool TryGetFundingTxId(ShortChannelId shortChannelId, out TxId fundingTxId)
    {
        lock (_lock)
            return _fundingTxIds.TryGetValue(shortChannelId, out fundingTxId);
    }

    /// <inheritdoc />
    public bool TrySetFundingTxId(ShortChannelId shortChannelId, TxId fundingTxId)
    {
        lock (_lock)
        {
            if (!_channels.ContainsKey(shortChannelId))
                return false;

            SetFundingTxIdLocked(shortChannelId, fundingTxId);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetChannelByFundingOutpoint(TxId transactionId, uint outputIndex,
                                               out ShortChannelId shortChannelId)
    {
        lock (_lock)
            return _channelsByFundingOutpoint.TryGetValue((transactionId, outputIndex), out shortChannelId);
    }

    /// <inheritdoc />
    public IReadOnlyList<ShortChannelId> GetChannelsWithoutFundingTxId()
    {
        lock (_lock)
            return _channels.Keys.Where(s => !_fundingTxIds.ContainsKey(s)).ToList();
    }

    /// <inheritdoc />
    public bool TryGetChannelReceivedAt(ShortChannelId shortChannelId, out DateTimeOffset receivedAt)
    {
        lock (_lock)
            return _channelReceivedAt.TryGetValue(shortChannelId, out receivedAt);
    }

    /// <inheritdoc />
    public bool IsBanned(CompactPubKey nodeId)
    {
        lock (_lock)
            return _bans.TryGetValue(nodeId, out var ban) && ban.Until > _timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    public bool TryAddChannel(GraphChannel channel, TxId? fundingTxId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_lock)
        {
            if (!_channels.TryAdd(channel.ShortChannelId, channel))
                return false;

            _channelReceivedAt[channel.ShortChannelId] = _timeProvider.GetUtcNow();
            if (fundingTxId is { } txId)
                SetFundingTxIdLocked(channel.ShortChannelId, txId);
            CountChannelEnds(channel, +1);
            _deletedChannels.Remove(channel.ShortChannelId);
            _dirtyChannels.Add(channel.ShortChannelId);
            if (channel.Policy1 is not null)
                _dirtyPolicies.Add((channel.ShortChannelId, 0));
            if (channel.Policy2 is not null)
                _dirtyPolicies.Add((channel.ShortChannelId, 1));
            _version++;
        }

        ChannelAdded?.Invoke(this, channel);
        return true;
    }

    /// <inheritdoc />
    public bool TryApplyPolicy(ShortChannelId shortChannelId, GraphPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_lock)
        {
            if (!_channels.TryGetValue(shortChannelId, out var channel))
                return false;

            var current = channel.GetPolicy(policy.Direction);
            if (current is not null && current.Timestamp >= policy.Timestamp)
                return false;

            _channels[shortChannelId] = channel.WithPolicy(policy);
            _dirtyPolicies.Add((shortChannelId, policy.Direction));
            _version++;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryApplyNode(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.NodeId, out var current) && current.Timestamp >= node.Timestamp)
                return false;

            _nodes[node.NodeId] = node;
            _nodeReceivedAt[node.NodeId] = _timeProvider.GetUtcNow();
            _deletedNodes.Remove(node.NodeId);
            _dirtyNodes.Add(node.NodeId);
            _version++;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryApplyOwnNode(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.NodeId, out var current) && current.Timestamp >= node.Timestamp)
                return false;

            _nodes[node.NodeId] = node;
            _nodeReceivedAt[node.NodeId] = _timeProvider.GetUtcNow();
            _deletedNodes.Remove(node.NodeId);
            _dirtyNodes.Remove(node.NodeId);
            _version++;
            return true;
        }
    }

    /// <inheritdoc />
    public void Ban(CompactPubKey nodeId, string reason, DateTimeOffset until)
    {
        lock (_lock)
        {
            if (_bans.TryGetValue(nodeId, out var current) && current.Until >= until)
                return;

            _bans[nodeId] = new GraphBannedNodeRecord(nodeId, reason, until);
            _dirtyBans.Add(nodeId);
        }
    }

    /// <inheritdoc />
    public bool MarkSpent(ShortChannelId shortChannelId, uint height)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(shortChannelId, out var channel))
                return false;

            if (channel.SpentAtHeight == height)
                return true;

            _channels[shortChannelId] = channel.WithSpentAtHeight(height);
            _dirtyChannels.Add(shortChannelId);
            _version++;
            return true;
        }
    }

    /// <inheritdoc />
    public int ClearSpentAbove(uint height)
    {
        lock (_lock)
        {
            var reorged = _channels.Values.Where(c => c.SpentAtHeight > height).ToList();
            foreach (var channel in reorged)
            {
                _channels[channel.ShortChannelId] = channel.WithSpentAtHeight(null);
                _dirtyChannels.Add(channel.ShortChannelId);
            }

            if (reorged.Count > 0)
                _version++;

            return reorged.Count;
        }
    }

    /// <inheritdoc />
    public bool RemoveChannel(ShortChannelId shortChannelId)
    {
        lock (_lock)
        {
            if (!_channels.Remove(shortChannelId, out var channel))
                return false;

            _channelReceivedAt.Remove(shortChannelId);
            if (_fundingTxIds.Remove(shortChannelId, out var fundingTxId))
                _channelsByFundingOutpoint.Remove((fundingTxId, shortChannelId.OutputIndex));
            CountChannelEnds(channel, -1);
            _dirtyChannels.Remove(shortChannelId);
            _dirtyPolicies.Remove((shortChannelId, 0));
            _dirtyPolicies.Remove((shortChannelId, 1));
            _deletedChannels.Add(shortChannelId);
            _version++;
            return true;
        }
    }

    /// <inheritdoc />
    public bool RemoveNode(CompactPubKey nodeId)
    {
        lock (_lock)
        {
            if (!_nodes.Remove(nodeId))
                return false;

            _nodeReceivedAt.Remove(nodeId);
            _dirtyNodes.Remove(nodeId);
            _deletedNodes.Add(nodeId);
            _version++;
            return true;
        }
    }

    private void SetFundingTxIdLocked(ShortChannelId shortChannelId, TxId fundingTxId)
    {
        if (_fundingTxIds.TryGetValue(shortChannelId, out var previous))
            _channelsByFundingOutpoint.Remove((previous, shortChannelId.OutputIndex));

        _fundingTxIds[shortChannelId] = fundingTxId;
        _channelsByFundingOutpoint[(fundingTxId, shortChannelId.OutputIndex)] = shortChannelId;
    }

    private void CountChannelEnds(GraphChannel channel, int delta)
    {
        foreach (var nodeId in (ReadOnlySpan<CompactPubKey>)[channel.NodeId1, channel.NodeId2])
        {
            var count = _channelCountByNode.GetValueOrDefault(nodeId) + delta;
            if (count > 0)
                _channelCountByNode[nodeId] = count;
            else
                _channelCountByNode.Remove(nodeId);
        }
    }

    private FlushWork TakeWorkLocked()
    {
        var channels = new List<GraphChannelRecord>(_dirtyChannels.Count);
        foreach (var shortChannelId in _dirtyChannels)
        {
            if (_channels.TryGetValue(shortChannelId, out var channel))
                channels.Add(ToRecord(channel, _channelReceivedAt.GetValueOrDefault(shortChannelId)));
        }

        var policies = new List<GraphPolicyRecord>(_dirtyPolicies.Count);
        foreach (var (shortChannelId, direction) in _dirtyPolicies)
        {
            if (_channels.TryGetValue(shortChannelId, out var channel) && channel.GetPolicy(direction) is { } policy)
                policies.Add(ToRecord(shortChannelId, policy));
        }

        var nodes = new List<GraphNodeRecord>(_dirtyNodes.Count);
        foreach (var nodeId in _dirtyNodes)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
                nodes.Add(ToRecord(node, _nodeReceivedAt.GetValueOrDefault(nodeId)));
        }

        var bans = _dirtyBans.Where(_bans.ContainsKey).Select(n => _bans[n]).ToList();
        var work = new FlushWork(channels, policies, nodes, bans, _deletedChannels.ToList(), _deletedNodes.ToList());

        _dirtyChannels.Clear();
        _dirtyPolicies.Clear();
        _dirtyNodes.Clear();
        _dirtyBans.Clear();
        _deletedChannels.Clear();
        _deletedNodes.Clear();
        return work;
    }

    private void RestoreWorkLocked(FlushWork work)
    {
        // Keys only: the next flush writes whatever the values are by then
        foreach (var shortChannelId in work.DeletedChannels.Where(s => !_channels.ContainsKey(s)))
            _deletedChannels.Add(shortChannelId);
        foreach (var nodeId in work.DeletedNodes.Where(n => !_nodes.ContainsKey(n)))
            _deletedNodes.Add(nodeId);
        foreach (var channel in work.Channels.Where(c => _channels.ContainsKey(c.ShortChannelId)))
            _dirtyChannels.Add(channel.ShortChannelId);
        foreach (var policy in work.Policies.Where(p => _channels.ContainsKey(p.ShortChannelId)))
            _dirtyPolicies.Add((policy.ShortChannelId, policy.Direction));
        foreach (var node in work.Nodes.Where(n => _nodes.ContainsKey(n.NodeId)))
            _dirtyNodes.Add(node.NodeId);
        foreach (var ban in work.Bans)
            _dirtyBans.Add(ban.NodeId);
    }

    private static GraphChannelRecord ToRecord(GraphChannel channel, DateTimeOffset receivedAt) =>
        new(channel.ShortChannelId, channel.NodeId1, channel.NodeId2, channel.BitcoinKey1, channel.BitcoinKey2,
            channel.CapacitySat ?? 0, channel.Features.ToArray(), channel.RawAnnouncement.ToArray(),
            (StoredVerification)(byte)channel.Verification, channel.SpentAtHeight, receivedAt);

    private static GraphPolicyRecord ToRecord(ShortChannelId shortChannelId, GraphPolicy policy) =>
        new(shortChannelId, policy.Direction, policy.Timestamp, policy.MessageFlags, policy.ChannelFlags,
            policy.CltvExpiryDelta, policy.HtlcMinimumMsat, policy.HtlcMaximumMsat, policy.FeeBaseMsat,
            policy.FeeProportionalMillionths, policy.RawUpdate.ToArray());

    private static GraphNodeRecord ToRecord(GraphNode node, DateTimeOffset receivedAt)
    {
        // The raw addresses field as signed (unknown descriptors included), from the announcement when we have it
        var addresses = NodeAnnouncementPayload.TryParse(node.RawAnnouncement.Span, out var payload)
                            ? payload.Addresses.ToArray()
                            : AddressDescriptorCodec.EncodeList(node.Addresses);
        return new GraphNodeRecord(node.NodeId, node.Timestamp, node.Features.ToArray(), node.Alias.ToArray(),
                                   node.RgbColor.ToArray(), addresses, node.RawAnnouncement.ToArray(), receivedAt);
    }

    private GraphChannel? MapChannel(GraphChannelRecord record)
    {
        try
        {
            var verification = (GraphVerification)(byte)record.Verification;
            return new GraphChannel(record.ShortChannelId, record.NodeId1, record.NodeId2, record.BitcoinKey1,
                                    record.BitcoinKey2,
                                    verification == GraphVerification.Unverified ? null : record.CapacitySat,
                                    record.Features, verification)
            {
                SpentAtHeight = record.SpentAtHeight,
                RawAnnouncement = record.RawAnnouncement
            };
        }
        catch (ArgumentException e)
        {
            _logger.LogWarning(e, "Skipping stored graph channel {ShortChannelId}", record.ShortChannelId);
            return null;
        }
    }

    private static GraphPolicy MapPolicy(GraphPolicyRecord record)
    {
        // The unknown trailing fields live only in the signed bytes (BOLT 7 compares them at the same timestamp)
        if (ChannelUpdatePayload.TryParse(record.RawUpdate, out var update))
            return GraphPolicy.FromChannelUpdate(update) with { RawUpdate = record.RawUpdate };

        return new GraphPolicy(record.Timestamp, record.MessageFlags, record.ChannelFlags, record.CltvExpiryDelta,
                               record.HtlcMinimumMsat, record.HtlcMaximumMsat, record.FeeBaseMsat,
                               record.FeeProportionalMillionths)
        { RawUpdate = record.RawUpdate };
    }

    private static GraphNode MapNode(GraphNodeRecord record) =>
        new(record.NodeId, record.Timestamp, record.Features, record.Alias, record.Color,
            AddressDescriptorCodec.DecodeList(record.Addresses).Addresses)
        {
            RawAnnouncement = record.RawAnnouncement
        };

    private sealed record FlushWork(
        List<GraphChannelRecord> Channels,
        List<GraphPolicyRecord> Policies,
        List<GraphNodeRecord> Nodes,
        List<GraphBannedNodeRecord> Bans,
        List<ShortChannelId> DeletedChannels,
        List<CompactPubKey> DeletedNodes)
    {
        public bool IsEmpty => Channels.Count == 0 && Policies.Count == 0 && Nodes.Count == 0 && Bans.Count == 0
                            && DeletedChannels.Count == 0 && DeletedNodes.Count == 0;
    }
}