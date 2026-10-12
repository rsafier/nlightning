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
/// <para>
/// These removals (disconnection, lazy expiry on any lookup or add) run outside the channel lock, which is benign: the
/// handler that holds the lock (accept_channel as opener, funding_created as fundee) keeps its own reference to the
/// temporary channel, and <see cref="UpgradeChannel"/> tolerates a missing temporary entry (it only logs). The
/// opener's failed-open cleanup, which also returns the funding UTXOs, does take the lock
/// (<c>OpenChannelClientHandler</c>).
/// </para>
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

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelOpened;

    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelAdded;
    public event EventHandler<ChannelUpdatedEventArgs>? OnChannelRemoved;

    /// <summary>The longest a temporary channel is kept (<see cref="DefaultTemporaryChannelTimeout"/>).</summary>
    public TimeSpan TemporaryChannelTimeout { get; init; } = DefaultTemporaryChannelTimeout;

    /// <summary>
    /// CI guard for NL-138: when a test enables it, <see cref="TryGetChannel"/> throws when the shared model's state
    /// drifted from the last one published with <see cref="UpdateChannel"/> — someone mutated the model handed out by
    /// <see cref="TryGetChannel"/> without calling <see cref="UpdateChannel"/>, so <see cref="OnChannelUpdated"/> and
    /// <see cref="OnChannelOpened"/> never fired. Off by default (the models are shared by design, and a close that
    /// mutates before removing the channel may race a lookup); tests opt in per repository instance.
    /// </summary>
    public bool DetectUnpublishedMutations { get; init; }

    public ChannelMemoryRepository(ILogger<ChannelMemoryRepository> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public bool TryGetChannel(ChannelId channelId, [MaybeNullWhen(false)] out ChannelModel channel)
    {
        if (!_channels.TryGetValue(channelId, out channel))
            return false;

        // NL-138: the published state (AddChannel/UpdateChannel) is the only one subscribers were told about
        if (DetectUnpublishedMutations && _channelStates.TryGetValue(channelId, out var published)
         && published != channel.State)
            throw new InvalidOperationException(
                $"Channel {channelId} was mutated without UpdateChannel: its state is {channel.State} but the last " +
                $"published one is {published} (NL-138), so OnChannelUpdated never fired for the change.");

        return true;
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
    public void LoadChannel(ChannelModel channel) => AddChannelCore(channel);

    public void AddChannel(ChannelModel channel)
    {
        AddChannelCore(channel);
        NotifyLifecycle(OnChannelAdded, channel);
    }

    private void AddChannelCore(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!_channels.TryAdd(channel.ChannelId, channel))
            throw new InvalidOperationException($"Channel with Id {channel.ChannelId} already exists.");

        _channelStates[channel.ChannelId] = channel.State;
    }

    /// <inheritdoc/>
    /// <remarks>The state before the update decides <see cref="OnChannelOpened"/>: only a move into
    /// <see cref="ChannelState.Open"/> raises it, exactly once per channel (NL-054).</remarks>
    public void UpdateStagedChannel(ChannelModel channel) => UpdateChannelCore(channel, false);

    public void UpdateChannel(ChannelModel channel) => UpdateChannelCore(channel, true);

    private void UpdateChannelCore(ChannelModel channel, bool isPersisted)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!_channels.ContainsKey(channel.ChannelId))
            throw new KeyNotFoundException($"Channel with Id {channel.ChannelId} does not exist.");

        var wasOpen = _channelStates.TryGetValue(channel.ChannelId, out var previous)
                   && previous == ChannelState.Open;

        _channels[channel.ChannelId] = channel;
        _channelStates[channel.ChannelId] = channel.State;

        OnChannelUpdated?.Invoke(this, new ChannelUpdatedEventArgs(channel, isPersisted));

        // NL-054: the application notification that the channel became ready/usable (both channel_ready exchanged)
        if (!wasOpen && channel.State == ChannelState.Open)
            OnChannelOpened?.Invoke(this, new ChannelUpdatedEventArgs(channel));
    }

    /// <inheritdoc/>
    public bool TryRemoveChannel(ChannelId channelId)
    {
        var removed = _channels.TryRemove(channelId, out var channel);
        if (!removed)
            return false;
        _channelStates.TryRemove(channelId, out _);
        NotifyLifecycle(OnChannelRemoved, channel!);
        return true;
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

    private void NotifyLifecycle(EventHandler<ChannelUpdatedEventArgs>? handlers, ChannelModel channel)
    {
        if (handlers is null)
            return;
        foreach (var callback in handlers.GetInvocationList().Cast<EventHandler<ChannelUpdatedEventArgs>>())
            try
            {
                callback(this, new ChannelUpdatedEventArgs(channel));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Channel lifecycle observer failed for {ChannelId}", channel.ChannelId);
            }
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