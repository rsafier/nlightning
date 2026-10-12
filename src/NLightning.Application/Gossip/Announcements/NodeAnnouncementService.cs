using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Enums;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Gossip.Persistence;
using Domain.LiquidityAds;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Signing.Vls;
using Infrastructure.Bitcoin.Wallet.Interfaces;
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
    private readonly IReadOnlyList<IAnnouncedAddressSource> _addressSources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly AnnouncedChannels2? _announced2;
    private readonly IBlockchainMonitor? _blockchainMonitor;

    private NodeAnnouncementPayload? _current;
    private DateTimeOffset _currentMadeAt;
    private NodeAnnouncement2Payload? _currentV2;
    private DateTimeOffset _currentV2MadeAt;

    public NodeAnnouncementService(IChannelMemoryRepository channelMemoryRepository,
                                   ILightningSigner lightningSigner, ILogger<NodeAnnouncementService> logger,
                                   OwnGossipPublisher publisher, IServiceProvider serviceProvider,
                                   IOptions<NodeOptions> nodeOptions, IOptions<GossipOptions>? gossipOptions = null,
                                   TimeProvider? timeProvider = null,
                                   IEnumerable<IAnnouncedAddressSource>? addressSources = null,
                                   AnnouncedChannels2? announcedChannels2 = null,
                                   IBlockchainMonitor? blockchainMonitor = null)
    {
        _announced2 = announcedChannels2;
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _publisher = publisher;
        _serviceProvider = serviceProvider;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _addressSources = addressSources?.ToList() ?? [];

        // A run-time address (our onion service coming up) goes out at once, once we have an announced channel
        foreach (var source in _addressSources)
            source.AnnouncedAddressesChanged += (_, _) => RequestAnnouncement();
    }

    /// <inheritdoc />
    public NodeAnnouncementPayload? Current => _current;

    /// <summary>The last <see cref="RequestAnnouncement"/> (for tests).</summary>
    internal Task LastRequest { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public async Task<NodeAnnouncementPayload?> AnnounceAsync(CancellationToken cancellationToken = default)
    {
        // Taproot gossip (NL-878): a node_announcement_2 once a channel of ours has a channel_announcement_2
        await AnnounceV2Async(cancellationToken);

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
    /// <summary>The node_announcement_2 last made in this process (tests).</summary>
    public NodeAnnouncement2Payload? CurrentV2 => _currentV2;

    /// <summary>
    /// Our <c>node_announcement_2</c> (BOLTs PR #1059): the same features, alias, color and addresses as the v1 one,
    /// a block-height timestamp (the tip, above our previous one; the draft lets a node announce it only after a
    /// <c>channel_announcement_2</c> of its own) and a BIP 340 signature. An unchanged recent one is published again;
    /// a second one in the same block waits for the next block.
    /// </summary>
    private async Task AnnounceV2Async(CancellationToken cancellationToken)
    {
        if (_announced2 is not { Any: true } || _blockchainMonitor is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fields = BuildFields();
            var descriptors = AddressDescriptorCodec.DecodeList(fields.Addresses).Addresses;
            var alias = TrimAlias(fields.Alias);
            var now = _timeProvider.GetUtcNow();
            var tip = _blockchainMonitor.LastProcessedBlockHeight;

            // After a restart the stored row says which block height our last node_announcement_2 took (NL-1142)
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var stored = await unitOfWork.GraphDbRepository.GetNodeAsync(_lightningSigner.GetNodePublicKey());
            if (_currentV2 is null && stored?.BlockHeight is { } storedHeight && storedHeight >= tip)
            {
                _logger.LogDebug("Our node_announcement_2 of block {Stored} is not older than the tip {Tip}; waiting "
                               + "for a new block", storedHeight, tip);
                return;
            }
            if (_currentV2 is { } current
             && ((current.Features.Span.SequenceEqual(fields.Features)
               && (current.Alias ?? ReadOnlyMemory<byte>.Empty).Span.SequenceEqual(alias)
               && (current.Color ?? ReadOnlyMemory<byte>.Empty).Span.SequenceEqual(fields.Color)
               && current.Addresses.SequenceEqual(descriptors)
               && now - _currentV2MadeAt < _gossipOptions.NodeAnnouncementRefreshInterval)
              || current.BlockHeight >= tip))
            {
                _publisher.PublishNodeAnnouncement2(current);
                return;
            }

            var unsigned = NodeAnnouncement2Payload.Create(fields.Features, tip, _lightningSigner.GetNodePublicKey(),
                                                           fields.Color, alias, descriptors);
            var announcement =
                unsigned.WithSignature(_lightningSigner.SignNodeMessageBip340(unsigned.GetSignatureHash()));

            // Saved before it is published, as the v1 one is (G1-T6); the row's v1 columns are kept
            cancellationToken.ThrowIfCancellationRequested();
            await unitOfWork.GraphDbRepository.UpsertNodeAsync(
                (stored ?? new GraphNodeRecord(announcement.NodeId, 0, fields.Features, fields.Alias, fields.Color,
                                               fields.Addresses, [], now)) with
                {
                    Versions = GraphGossipVersions.V2 | (stored?.Versions ?? GraphGossipVersions.None)
                                                      & GraphGossipVersions.V1,
                    BlockHeight = announcement.BlockHeight,
                    RawAnnouncement2 = announcement.GetBytes()
                });
            await unitOfWork.SaveChangesAsync();
            _currentV2 = announcement;
            _currentV2MadeAt = now;
            _publisher.PublishNodeAnnouncement2(announcement);
            _logger.LogInformation("Announcing our node with node_announcement_2 at block height {BlockHeight}", tip);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The v1 alias without its zero padding (v2's alias is plain UTF-8, at most 32 bytes).</summary>
    private static byte[] TrimAlias(byte[] alias)
    {
        var length = alias.Length;
        while (length > 0 && alias[length - 1] == 0)
            length--;
        return alias[..length];
    }

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
                                                   nodeId, fields.Color, fields.Alias, fields.Addresses,
                                                   fields.ExtraData);
        var signature = _lightningSigner is IVlsGossipSigner vls
                            ? vls.SignNodeAnnouncement(unsigned.GetSignedData())
                            : _lightningSigner.SignNodeMessage(unsigned.GetSignatureHash());
        var announcement = unsigned.WithSignature(signature);

        cancellationToken.ThrowIfCancellationRequested();
        // Our row has one writer per protocol here; the v2 columns of the stored row are kept (NL-1142)
        await unitOfWork.GraphDbRepository.UpsertNodeAsync(
            new GraphNodeRecord(nodeId, timestamp, fields.Features, fields.Alias, fields.Color, fields.Addresses,
                                announcement.GetBytes(), now)
            {
                Versions = GraphGossipVersions.V1 | (stored?.Versions ?? GraphGossipVersions.None)
                                                  & GraphGossipVersions.V2,
                BlockHeight = stored?.BlockHeight,
                RawAnnouncement2 = stored?.RawAnnouncement2
            });
        await unitOfWork.SaveChangesAsync();
        return announcement;
    }

    /// <summary>
    /// The configured fields: node features (node_announcement context, wire order), alias, color, addresses and, when
    /// we sell liquidity, our <c>option_will_fund</c> rates as the extra data's TLV stream (NL-850, as Eclair).
    /// </summary>
    private AnnouncementFields BuildFields()
    {
        var features = _nodeOptions.Features.GetNodeFeatures(FeatureContext.NodeAnnouncement).GetWireBytes() ?? [];
        var alias = NodeAnnouncementPayload.EncodeAlias(_nodeOptions.Alias ?? string.Empty);
        var descriptors = MergeAddresses(_gossipOptions.GetAnnounceAddressDescriptors(),
                                         _addressSources.SelectMany(GetSourceAddresses));
        var extraData = NodeAnnouncementRates.EncodeExtraData(_nodeOptions.LiquidityAds.GetWillFundRates());
        return new AnnouncementFields(features, alias, _nodeOptions.GetColorBytes(),
                                      AddressDescriptorCodec.EncodeList(descriptors), descriptors.Count, extraData);
    }

    /// <summary>
    /// The configured addresses and the run-time ones (our onion service), each once, in the ascending type order BOLT 7
    /// requires. A run-time address that would break the origin rules (a second DNS name) is left out.
    /// </summary>
    internal static List<AddressDescriptor> MergeAddresses(IReadOnlyList<AddressDescriptor> configured,
                                                           IEnumerable<AddressDescriptor> runtime)
    {
        var merged = configured.ToList();
        foreach (var descriptor in runtime)
        {
            if (merged.Contains(descriptor) || descriptor.Port == 0
                                            || descriptor.Type is AddressDescriptorType.TorV2
             || (descriptor.Type == AddressDescriptorType.Dns && merged.Any(d => d.Type == AddressDescriptorType.Dns)))
                continue;

            merged.Add(descriptor);
        }

        return [.. merged.OrderBy(d => d.Type)];
    }

    private IReadOnlyList<AddressDescriptor> GetSourceAddresses(IAnnouncedAddressSource source)
    {
        try
        {
            return source.GetAnnouncedAddresses();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read the run-time addresses of {Source}", source.GetType().Name);
            return [];
        }
    }

    private sealed record AnnouncementFields(byte[] Features, byte[] Alias, byte[] Color, byte[] Addresses,
                                             int AddressCount, byte[] ExtraData)
    {
        /// <summary>Whether <paramref name="announcement"/> announces these fields; a change of our liquidity rates
        /// (the extra data) re-announces.</summary>
        public bool Matches(NodeAnnouncementPayload announcement) =>
            announcement.Features.Span.SequenceEqual(Features) && announcement.Alias.Span.SequenceEqual(Alias)
         && announcement.RgbColor.Span.SequenceEqual(Color)
         && announcement.Addresses.Span.SequenceEqual(Addresses)
         && announcement.ExtraData.Span.SequenceEqual(ExtraData);
    }
}