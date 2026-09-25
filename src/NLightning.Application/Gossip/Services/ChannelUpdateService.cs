using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Events;
using Interfaces;

/// <inheritdoc cref="IChannelUpdateService"/>
/// <remarks>
/// <para>
/// Sending: the service listens to <see cref="IChannelMemoryRepository.OnChannelUpdated"/>. The first time a channel
/// is seen <c>Open</c> with a short channel id, it builds our update on another task that first takes the channel's
/// lock. That event is raised while the handler that opened the channel still holds the lock and before it queues
/// its own <c>channel_ready</c>, so waiting for the lock puts our update after that <c>channel_ready</c> in the peer's
/// outbox.
/// </para>
/// <para>
/// Fields (BOLT 7): <c>must_be_one</c> and <c>dont_forward</c> (we never announce channels), <c>direction</c> = 1
/// when our node id is the greater one, the real short channel id, the fee and CLTV delta of
/// <c>NodeOptions.Routing</c>, <c>htlc_minimum_msat</c> = the larger of the peer's <c>htlc_minimum_msat</c> and
/// <c>Routing.HtlcMinimumMsat</c>, <c>htlc_maximum_msat</c> = the smallest of the capacity, the peer's
/// <c>max_htlc_value_in_flight_msat</c> and <c>Routing.HtlcMaximumMsat</c> (never below the minimum).
/// </para>
/// <para>
/// Everything is in memory: after a restart we don't resend (the peer keeps our last update) and the peer's update is
/// forgotten until it sends a new one.
/// </para>
/// </remarks>
public sealed class ChannelUpdateService : IChannelUpdateService, IDisposable
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly ILightningSigner _lightningSigner;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ILogger<ChannelUpdateService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<ChannelId, byte> _sentOnOpen = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdateMessage> _localUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, ChannelUpdatePayload> _remoteUpdates = new();
    private readonly ConcurrentDictionary<ChannelId, uint> _lastLocalTimestamps = new();

    /// <inheritdoc/>
    public event EventHandler<ChannelUpdateReadyEventArgs>? OnChannelUpdateReady;

    public ChannelUpdateService(IChannelMemoryRepository channelMemoryRepository,
                                IChannelLockProvider channelLockProvider, ILightningSigner lightningSigner,
                                ISecureKeyManager secureKeyManager, IOptions<NodeOptions> nodeOptions,
                                ILogger<ChannelUpdateService> logger, TimeProvider? timeProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelLockProvider = channelLockProvider;
        _lightningSigner = lightningSigner;
        _secureKeyManager = secureKeyManager;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _channelMemoryRepository.OnChannelUpdated += HandleChannelUpdated;
    }

    /// <inheritdoc/>
    public ChannelUpdateMessage CreateChannelUpdate(ChannelModel channel, bool disabled = false)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.ShortChannelId == default)
            throw new InvalidOperationException($"Channel {channel.ChannelId} has no short channel id yet");

        var capacityMsat = channel.FundingOutput?.Amount.MilliSatoshi
                        ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding output");

        var routing = _nodeOptions.Routing;
        var htlcMinimumMsat = Math.Max(channel.ChannelParams.Remote.HtlcMinimumAmount.MilliSatoshi,
                                       routing.HtlcMinimumMsat);
        var htlcMaximumMsat = capacityMsat;
        var remoteMaxInFlight = channel.ChannelParams.Remote.MaxHtlcValueInFlight.MilliSatoshi;
        if (remoteMaxInFlight > 0)
            htlcMaximumMsat = Math.Min(htlcMaximumMsat, remoteMaxInFlight);
        if (routing.HtlcMaximumMsat is { } configuredMaximum)
            htlcMaximumMsat = Math.Min(htlcMaximumMsat, configuredMaximum);
        htlcMaximumMsat = Math.Max(htlcMaximumMsat, htlcMinimumMsat);

        var channelFlags = IsNode2(_secureKeyManager.GetNodePubKey(), channel.RemoteNodeId)
                               ? ChannelUpdatePayload.ChannelFlagDirection
                               : (byte)0;
        if (disabled)
            channelFlags |= ChannelUpdatePayload.ChannelFlagDisable;

        var unsigned = new ChannelUpdatePayload(ChannelUpdatePayload.EmptySignature,
                                                _nodeOptions.BitcoinNetwork.ChainHash, channel.ShortChannelId,
                                                NextTimestamp(channel.ChannelId),
                                                ChannelUpdatePayload.MessageFlagMustBeOne
                                              | ChannelUpdatePayload.MessageFlagDontForward, channelFlags,
                                                routing.CltvExpiryDelta, htlcMinimumMsat, routing.FeeBaseMsat,
                                                routing.FeeProportionalMillionths, htlcMaximumMsat);
        var signature = _lightningSigner.SignNodeMessage(unsigned.GetSignatureHash());
        var message = new ChannelUpdateMessage(unsigned.WithSignature(signature));

        _localUpdates[channel.ChannelId] = message;
        return message;
    }

    /// <inheritdoc/>
    public async Task SendChannelUpdateAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel) || channel.State != ChannelState.Open
         || channel.ShortChannelId == default || channel.FundingOutput is null)
        {
            _logger.LogDebug("Not sending a channel_update for channel {ChannelId}: it is not open", channelId);
            return;
        }

        var message = CreateChannelUpdate(channel);
        _logger.LogInformation("Sending channel_update for channel {ChannelId} ({ShortChannelId}) to peer {Peer}",
                               channelId, channel.ShortChannelId, channel.RemoteNodeId);

        // Only enqueues (we hold the channel lock)
        OnChannelUpdateReady?.Invoke(this, new ChannelUpdateReadyEventArgs(channel.RemoteNodeId, message));
    }

    /// <inheritdoc/>
    public bool HandleRemoteChannelUpdate(CompactPubKey peerPubKey, ChannelUpdateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var update = message.Payload;

        // BOLT 7: MUST ignore an update for a chain we don't know
        if (update.ChainHash != _nodeOptions.BitcoinNetwork.ChainHash)
            return Ignore(peerPubKey, update, "it is for another chain");

        var channel = FindChannel(peerPubKey, update.ShortChannelId);
        if (channel is null)
            return Ignore(peerPubKey, update, "we have no such channel with that peer");

        // Only the peer's own direction: an update in our direction would claim to be ours
        if (update.Direction != IsNode2(peerPubKey, _secureKeyManager.GetNodePubKey()))
            return Ignore(peerPubKey, update, "its direction is not the peer's");

        if (!_lightningSigner.VerifyNodeMessage(update.GetSignatureHash(), update.Signature, peerPubKey))
            return Ignore(peerPubKey, update, "its signature is invalid");

        while (true)
        {
            if (_remoteUpdates.TryGetValue(channel.ChannelId, out var current))
            {
                // BOLT 7: SHOULD ignore an update whose timestamp is not greater than the last one
                if (update.Timestamp <= current.Timestamp)
                    return Ignore(peerPubKey, update, "it is not newer than the one we have");

                if (_remoteUpdates.TryUpdate(channel.ChannelId, update, current))
                    break;
            }
            else if (_remoteUpdates.TryAdd(channel.ChannelId, update))
            {
                break;
            }
        }

        _logger.LogInformation(
            "Stored channel_update of peer {Peer} for channel {ChannelId}: base {FeeBase} msat, {FeePpm} ppm, "
          + "cltv delta {CltvDelta}{Disabled}", peerPubKey, channel.ChannelId, update.FeeBaseMsat,
            update.FeeProportionalMillionths, update.CltvExpiryDelta, update.IsDisabled ? ", disabled" : "");
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetRemoteChannelUpdate(ChannelId channelId, out ChannelUpdatePayload? update)
    {
        return _remoteUpdates.TryGetValue(channelId, out update);
    }

    /// <inheritdoc/>
    public bool TryGetLocalChannelUpdate(ChannelId channelId, out ChannelUpdateMessage? update)
    {
        return _localUpdates.TryGetValue(channelId, out update);
    }

    public void Dispose()
    {
        _channelMemoryRepository.OnChannelUpdated -= HandleChannelUpdated;
    }

    /// <summary>
    /// Runs while the caller holds the channel's lock: only schedules the send, which waits for that lock.
    /// </summary>
    private void HandleChannelUpdated(object? sender, ChannelUpdatedEventArgs args)
    {
        var channel = args.Channel;
        if (channel.State != ChannelState.Open || channel.ShortChannelId == default
                                               || !_sentOnOpen.TryAdd(channel.ChannelId, 0))
            return;

        var channelId = channel.ChannelId;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendChannelUpdateAsync(channelId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to send the channel_update for channel {ChannelId}", channelId);
            }
        });
    }

    private ChannelModel? FindChannel(CompactPubKey peerPubKey, ShortChannelId shortChannelId)
    {
        // The peer may name the channel by its real scid or by an alias (ours or its own)
        return _channelMemoryRepository
              .FindChannels(c => c.RemoteNodeId == peerPubKey
                              && (c.ShortChannelId == shortChannelId || c.RemoteAlias == shortChannelId
                               || (c.LocalAliases?.Contains(shortChannelId) ?? false)))
              .FirstOrDefault();
    }

    private bool Ignore(CompactPubKey peerPubKey, ChannelUpdatePayload update, string reason)
    {
        _logger.LogDebug("Ignoring channel_update for {ShortChannelId} from peer {Peer}: {Reason}",
                         update.ShortChannelId, peerPubKey, reason);
        return false;
    }

    /// <summary>
    /// UNIX time, but always after the last update we made for the channel (BOLT 7: timestamps must increase).
    /// </summary>
    private uint NextTimestamp(ChannelId channelId)
    {
        var now = (uint)Math.Clamp(_timeProvider.GetUtcNow().ToUnixTimeSeconds(), 0, uint.MaxValue);
        return _lastLocalTimestamps.AddOrUpdate(channelId, now, (_, last) => Math.Max(now, last + 1));
    }

    /// <summary>
    /// Whether <paramref name="origin"/> is <c>node_id_2</c> (the greater compressed key) of the channel with
    /// <paramref name="other"/> (BOLT 7 <c>direction</c> bit).
    /// </summary>
    private static bool IsNode2(CompactPubKey origin, CompactPubKey other)
    {
        return ((ReadOnlySpan<byte>)origin).SequenceCompareTo(other) > 0;
    }
}