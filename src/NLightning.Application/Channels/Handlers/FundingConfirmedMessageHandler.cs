using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;

public class FundingConfirmedMessageHandler
{
    /// <summary>
    /// How many random candidates may collide with a used scid before alias generation gives up.
    /// </summary>
    private const int MaxAliasGenerationAttempts = 100;

    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<FundingConfirmedMessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IUnitOfWork _uow;

    public event EventHandler<IChannelMessage>? OnMessageReady;

    public FundingConfirmedMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                          ILightningSigner lightningSigner,
                                          ILogger<FundingConfirmedMessageHandler> logger,
                                          IMessageFactory messageFactory,
                                          IUnitOfWork uow)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _uow = uow;
    }

    public async Task HandleAsync(ChannelModel channel)
    {
        try
        {
            // Check if the channel is in the right state
            if (channel.State is not (ChannelState.V1FundingSigned
                                   or ChannelState.ReadyForThem))
            {
                _logger.LogError(
                    "Received funding confirmation, but the channel {ChannelId} had a wrong state: {State}",
                    channel.ChannelId, Enum.GetName(channel.State));
                return;
            }

            var mustUseScidAlias = channel.ChannelConfig.UseScidAlias > FeatureSupport.No;

            // Create our new per-commitment point
            channel.UpdateCommitmentNumber(channel.CommitmentNumber.Increment());
            var newPerCommitmentPoint =
                _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, channel.CommitmentNumber.Value);
            channel.LocalKeySet.UpdatePerCommitmentPoint(newPerCommitmentPoint);

            // Handle ScidAlias. Aliases already sent to the peer must stay valid (BOLT 2 channel_ready: always
            // recognize them for incoming HTLCs), so they are reused instead of regenerated on a re-confirmation.
            if (mustUseScidAlias && channel.LocalAliases is not { Count: > 0 })
            {
                // Decide how many SCID aliases we need
                var scidAliasesCount = RandomNumberGenerator.GetInt32(2, 6); // Randomly choose between 2 and 5
                channel.LocalAliases = GenerateUniqueScidAliases(channel, scidAliasesCount);
            }

            if (channel.State == ChannelState.ReadyForThem)
            {
                // Valid transition: ReadyForThem -> Open
                channel.UpdateState(ChannelState.Open);
                await PersistChannelAsync(channel);

                _logger.LogInformation("Channel {ChannelId} is now open", channel.ChannelId);

                // TODO: Notify application layer that channel is fully open
                // TODO: Update routing tables
            }
            else if (channel.State == ChannelState.V1FundingSigned)
            {
                // Valid transition: V1FundingSigned -> ReadyForUs
                channel.UpdateState(ChannelState.ReadyForUs);
                await PersistChannelAsync(channel);

                _logger.LogInformation("Funding confirmed for us for channel {ChannelId}",
                                       channel.ChannelId);
            }

            if (channel.LocalAliases is { Count: > 0 })
            {
                // Create a ChannelReady message with the SCID aliases
                foreach (var alias in channel.LocalAliases)
                {
                    var channelReadyMessage =
                        _messageFactory.CreateChannelReadyMessage(channel.ChannelId, newPerCommitmentPoint, alias);

                    // Raise the event with the message
                    OnMessageReady?.Invoke(this, channelReadyMessage);
                }
            }
            else
            {
                var channelReadyMessage =
                    _messageFactory.CreateChannelReadyMessage(channel.ChannelId, newPerCommitmentPoint,
                                                              channel.ShortChannelId);

                // Raise the event with the message
                OnMessageReady?.Invoke(this, channelReadyMessage);
            }

            _logger.LogInformation("Channel {ChannelId} funding transaction confirmed", channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling funding confirmation for channel {ChannelId}", channel.ChannelId);
            throw;
        }
    }

    /// <summary>
    /// Creates a random scid alias candidate. Uniqueness is enforced by the caller.
    /// </summary>
    protected virtual ShortChannelId GenerateRandomScidAlias()
    {
        return new ShortChannelId(RandomNumberGenerator.GetBytes(ShortChannelId.Length));
    }

    /// <summary>
    /// Generates <paramref name="count"/> aliases that collide neither with each other nor with the real scid or the
    /// local aliases of any known channel (NL-103), so an incoming scid always maps to one channel.
    /// </summary>
    private List<ShortChannelId> GenerateUniqueScidAliases(ChannelModel channel, int count)
    {
        var usedScids = new HashSet<ulong>();
        AddIfSet(usedScids, channel.ShortChannelId);
        foreach (var otherChannel in _channelMemoryRepository.FindChannels(c => c.ChannelId != channel.ChannelId))
        {
            AddIfSet(usedScids, otherChannel.ShortChannelId);
            if (otherChannel.LocalAliases is null)
                continue;

            foreach (var alias in otherChannel.LocalAliases)
                AddIfSet(usedScids, alias);
        }

        var aliases = new List<ShortChannelId>(count);
        var attempts = 0;
        while (aliases.Count < count)
        {
            if (++attempts > count + MaxAliasGenerationAttempts)
                throw new InvalidOperationException(
                    $"Unable to generate {count} unique scid aliases for channel {channel.ChannelId}");

            var candidate = GenerateRandomScidAlias();
            if (!TryGetKey(candidate, out var key) || key == 0 || !usedScids.Add(key))
            {
                _logger.LogWarning("Discarding colliding scid alias {Alias} for channel {ChannelId}", candidate,
                                   channel.ChannelId);
                continue;
            }

            aliases.Add(candidate);
        }

        return aliases;
    }

    private static void AddIfSet(HashSet<ulong> usedScids, ShortChannelId scid)
    {
        if (TryGetKey(scid, out var key))
            usedScids.Add(key);
    }

    private static bool TryGetKey(ShortChannelId scid, out ulong key)
    {
        // default(ShortChannelId) has no backing bytes (e.g. an unconfirmed channel)
        byte[]? bytes = scid;
        if (bytes is not { Length: ShortChannelId.Length })
        {
            key = 0;
            return false;
        }

        key = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        return true;
    }

    private async Task PersistChannelAsync(ChannelModel channel)
    {
        _channelMemoryRepository.UpdateChannel(channel);
        await _uow.ChannelDbRepository.UpdateAsync(channel);

        await _uow.SaveChangesAsync();
    }
}