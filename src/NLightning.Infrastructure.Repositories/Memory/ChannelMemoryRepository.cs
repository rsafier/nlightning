using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Repositories.Memory;

using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <remarks>
/// A temporary channel (an open between open_channel and funding_created, or accept_channel for the opener) lives at
/// most <see cref="TemporaryChannelTimeout"/>: once expired it is no longer found and is dropped, so an open the peer
/// abandons while it stays connected does not stay in memory (NL-392). A disconnection removes the peer's temporary
/// channels at once (<see cref="RemoveTemporaryChannels"/>).
/// </remarks>
public class ChannelMemoryRepository : IChannelMemoryRepository
{
    /// <summary>
    /// How long an open may take from its temporary channel being stored to funding_created (fundee) or to our
    /// processing of accept_channel (opener). The default of the anchors reserve's <c>Node:Anchors:PendingOpenTimeout</c>
    /// is the same.
    /// </summary>
    public static readonly TimeSpan DefaultTemporaryChannelTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<ChannelMemoryRepository> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ChannelId, ChannelModel> _channels = [];
    private readonly ConcurrentDictionary<ChannelId, ChannelState> _channelStates = [];
    private readonly ConcurrentDictionary<(CompactPubKey, ChannelId), ChannelModel> _temporaryChannels = [];
    private readonly ConcurrentDictionary<(CompactPubKey, ChannelId), ChannelState> _temporaryChannelStates = [];
    private readonly ConcurrentDictionary<(CompactPubKey, ChannelId), DateTimeOffset> _temporaryChannelsAddedAt = [];

    /// <inheritdoc/>
    public event EventHandler<ChannelUpgradedEventArgs>? OnChannelUpgraded;

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelUpdated;

    /// <summary>The longest a temporary channel is kept (<see cref="DefaultTemporaryChannelTimeout"/>).</summary>
    public TimeSpan TemporaryChannelTimeout { get; init; } = DefaultTemporaryChannelTimeout;

    public ChannelMemoryRepository(ILogger<ChannelMemoryRepository> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public bool TryGetChannel(ChannelId channelId, [MaybeNullWhen(false)] out ChannelModel channel)
    {
        return _channels.TryGetValue(channelId, out channel);
    }

    /// <inheritdoc/>
    public List<ChannelModel> FindChannels(Func<ChannelModel, bool> predicate)
    {
        return _channels
              .Values
              .Where(predicate)
              .ToList();
    }

    /// <inheritdoc/>
    public bool TryGetChannelState(ChannelId channelId, out ChannelState channelState)
    {
        return _channelStates.TryGetValue(channelId, out channelState);
    }

    /// <inheritdoc/>
    public void AddChannel(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!_channels.TryAdd(channel.ChannelId, channel))
            throw new InvalidOperationException($"Channel with Id {channel.ChannelId} already exists.");

        _channelStates[channel.ChannelId] = channel.State;
    }

    /// <inheritdoc/>
    public void UpdateChannel(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!_channels.ContainsKey(channel.ChannelId))
            throw new KeyNotFoundException($"Channel with Id {channel.ChannelId} does not exist.");

        _channels[channel.ChannelId] = channel;
        _channelStates[channel.ChannelId] = channel.State;

        OnChannelUpdated?.Invoke(this, new ChannelUpdatedEventArgs(channel));
    }

    /// <inheritdoc/>
    public bool TryRemoveChannel(ChannelId channelId)
    {
        var removed = _channels.TryRemove(channelId, out _);
        return removed && _channelStates.TryRemove(channelId, out _);
    }

    /// <inheritdoc/>
    public bool TryGetTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId,
                                       [MaybeNullWhen(false)] out ChannelModel channel)
    {
        if (RemoveIfExpired((compactPubKey, channelId)))
        {
            channel = null;
            return false;
        }

        return _temporaryChannels.TryGetValue((compactPubKey, channelId), out channel);
    }

    /// <inheritdoc/>
    public bool TryGetTemporaryChannelState(CompactPubKey compactPubKey, ChannelId channelId,
                                            out ChannelState channelState)
    {
        if (RemoveIfExpired((compactPubKey, channelId)))
        {
            channelState = ChannelState.None;
            return false;
        }

        return _temporaryChannelStates.TryGetValue((compactPubKey, channelId), out channelState);
    }

    /// <inheritdoc/>
    public void AddTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel)
    {
        // Drop the opens that timed out, so abandoned ones never pile up (NL-392)
        foreach (var key in _temporaryChannelsAddedAt.Keys)
            RemoveIfExpired(key);

        if (!_temporaryChannels.TryAdd((compactPubKey, channel.ChannelId), channel))
            throw new InvalidOperationException(
                $"Temporary channel with Id {channel.ChannelId} for CompactPubKey {compactPubKey} already exists.");

        _temporaryChannelsAddedAt[(compactPubKey, channel.ChannelId)] = _timeProvider.GetUtcNow();
        _temporaryChannelStates[(compactPubKey, channel.ChannelId)] = channel.State;
    }

    /// <inheritdoc/>
    public void UpdateTemporaryChannel(CompactPubKey compactPubKey, ChannelModel channel)
    {
        if (!_temporaryChannels.ContainsKey((compactPubKey, channel.ChannelId)))
            throw new KeyNotFoundException(
                $"Temporary channel with Id {channel.ChannelId} for CompactPubKey {compactPubKey} does not exist.");

        _temporaryChannels[(compactPubKey, channel.ChannelId)] = channel;
        _temporaryChannelStates[(compactPubKey, channel.ChannelId)] = channel.State;
    }

    /// <inheritdoc/>
    public bool TryRemoveTemporaryChannel(CompactPubKey compactPubKey, ChannelId channelId)
    {
        var removed = _temporaryChannels.TryRemove((compactPubKey, channelId), out _);
        _temporaryChannelsAddedAt.TryRemove((compactPubKey, channelId), out _);
        return removed && _temporaryChannelStates.TryRemove((compactPubKey, channelId), out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChannelId> RemoveTemporaryChannels(CompactPubKey compactPubKey)
    {
        var removed = new List<ChannelId>();
        foreach (var (peer, channelId) in _temporaryChannels.Keys)
        {
            if (peer == compactPubKey && TryRemoveTemporaryChannel(peer, channelId))
                removed.Add(channelId);
        }

        return removed;
    }

    /// <inheritdoc/>
    public void UpgradeChannel(ChannelId oldChannelId, ChannelModel tempChannel)
    {
        AddChannel(tempChannel);
        if (!TryRemoveTemporaryChannel(tempChannel.RemoteNodeId, oldChannelId))
            _logger.LogWarning(
                "Unable to remove Temporary Channel with Id {oldChannelId} while upgrading Channel {channelId}.",
                oldChannelId, tempChannel.ChannelId);

        OnChannelUpgraded?.Invoke(this, new ChannelUpgradedEventArgs(oldChannelId, tempChannel.ChannelId));
    }

    /// <summary>
    /// Removes the temporary channel at <paramref name="key"/> when it is older than
    /// <see cref="TemporaryChannelTimeout"/>.
    /// </summary>
    /// <returns><c>true</c> when it had expired (and is gone now).</returns>
    private bool RemoveIfExpired((CompactPubKey Peer, ChannelId ChannelId) key)
    {
        if (!_temporaryChannelsAddedAt.TryGetValue(key, out var addedAt)
         || _timeProvider.GetUtcNow() - addedAt < TemporaryChannelTimeout)
            return false;

        if (TryRemoveTemporaryChannel(key.Peer, key.ChannelId))
            _logger.LogInformation("Forgetting temporary channel {ChannelId} of peer {Peer}: the open timed out",
                                   key.ChannelId, key.Peer);

        return true;
    }
}