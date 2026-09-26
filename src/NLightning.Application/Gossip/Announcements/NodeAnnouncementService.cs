using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Enums;
using Domain.Gossip.Addresses;
using Domain.Gossip.Persistence;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Payloads;
using Interfaces;

/// <inheritdoc cref="INodeAnnouncementService"/>
/// <remarks>A singleton; one announcement is made at a time.</remarks>
public sealed class NodeAnnouncementService : INodeAnnouncementService
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<NodeAnnouncementService> _logger;
    private readonly OwnGossipPublisher _publisher;
    private readonly IServiceProvider _serviceProvider;
    private readonly NodeOptions _nodeOptions;
    private readonly GossipOptions _gossipOptions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NodeAnnouncementPayload? _current;
    private DateTimeOffset _currentMadeAt;

    public NodeAnnouncementService(IChannelMemoryRepository channelMemoryRepository,
                                   ILightningSigner lightningSigner, ILogger<NodeAnnouncementService> logger,
                                   OwnGossipPublisher publisher, IServiceProvider serviceProvider,
                                   IOptions<NodeOptions> nodeOptions, IOptions<GossipOptions>? gossipOptions = null,
                                   TimeProvider? timeProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _publisher = publisher;
        _serviceProvider = serviceProvider;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public NodeAnnouncementPayload? Current => _current;

    /// <summary>The last <see cref="RequestAnnouncement"/> (for tests).</summary>
    internal Task LastRequest { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public async Task<NodeAnnouncementPayload?> AnnounceAsync(CancellationToken cancellationToken = default)
    {
        // BOLT 7: others ignore the node_announcement of a node they know no channel_announcement of
        if (_channelMemoryRepository.FindChannels(ChannelAnnouncementService.IsAnnounced).Count == 0)
        {
            _logger.LogDebug("No node_announcement: none of our channels is announced");
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fields = BuildFields();
            var now = _timeProvider.GetUtcNow();
            if (_current is { } current && fields.Matches(current)
                                        && now - _currentMadeAt < _gossipOptions.NodeAnnouncementRefreshInterval)
            {
                _publisher.PublishNodeAnnouncement(current);
                return current;
            }

            var announcement = await CreateAndSaveAsync(fields, now, cancellationToken);
            _current = announcement;
            _currentMadeAt = now;
            _publisher.PublishNodeAnnouncement(announcement);
            _logger.LogInformation("Announcing our node (alias '{Alias}', {AddressCount} address(es)), timestamp {Timestamp}",
                                   announcement.GetAliasText(), fields.AddressCount, announcement.Timestamp);
            return announcement;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void RequestAnnouncement()
    {
        LastRequest = Task.Run(async () =>
        {
            try
            {
                await AnnounceAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not make our node_announcement");
            }
        });
    }

    /// <summary>
    /// Signs a new announcement with a timestamp after every one made before (the stored one included) and saves it
    /// as our node's row before it is handed out, so a restart never reuses a timestamp.
    /// </summary>
    private async Task<NodeAnnouncementPayload> CreateAndSaveAsync(AnnouncementFields fields, DateTimeOffset now,
                                                                   CancellationToken cancellationToken)
    {
        var nodeId = _lightningSigner.GetNodePublicKey();

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var stored = await unitOfWork.GraphDbRepository.GetNodeAsync(nodeId);

        var timestamp = (uint)Math.Clamp(now.ToUnixTimeSeconds(), 1, uint.MaxValue);
        if (stored is not null && stored.Timestamp >= timestamp)
            timestamp = stored.Timestamp + 1;
        if (_current is not null && _current.Timestamp >= timestamp)
            timestamp = _current.Timestamp + 1;

        var unsigned = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, fields.Features, timestamp,
                                                   nodeId, fields.Color, fields.Alias, fields.Addresses);
        var announcement = unsigned.WithSignature(_lightningSigner.SignNodeMessage(unsigned.GetSignatureHash()));

        cancellationToken.ThrowIfCancellationRequested();
        await unitOfWork.GraphDbRepository.UpsertNodeAsync(
            new GraphNodeRecord(nodeId, timestamp, fields.Features, fields.Alias, fields.Color, fields.Addresses,
                                announcement.GetBytes(), now));
        await unitOfWork.SaveChangesAsync();
        return announcement;
    }

    /// <summary>
    /// The configured fields: node features (node_announcement context, wire order), alias, color and addresses.
    /// </summary>
    private AnnouncementFields BuildFields()
    {
        var features = _nodeOptions.Features.GetNodeFeatures(FeatureContext.NodeAnnouncement).GetWireBytes() ?? [];
        var alias = NodeAnnouncementPayload.EncodeAlias(_nodeOptions.Alias ?? string.Empty);
        var descriptors = _gossipOptions.GetAnnounceAddressDescriptors();
        return new AnnouncementFields(features, alias, _nodeOptions.GetColorBytes(),
                                      AddressDescriptorCodec.EncodeList(descriptors), descriptors.Count);
    }

    private sealed record AnnouncementFields(byte[] Features, byte[] Alias, byte[] Color, byte[] Addresses,
                                             int AddressCount)
    {
        public bool Matches(NodeAnnouncementPayload announcement) =>
            announcement.Features.Span.SequenceEqual(Features) && announcement.Alias.Span.SequenceEqual(Alias)
         && announcement.RgbColor.Span.SequenceEqual(Color)
         && announcement.Addresses.Span.SequenceEqual(Addresses);
    }
}