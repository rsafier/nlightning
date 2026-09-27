using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Receive;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Gossip.Announcements;
using OnionMessages;

/// <summary>
/// Our BOLT 12 offers (<see cref="IOfferService"/>; plan B3-T1, §3.7 step 1): creates, stores, lists and disables them.
/// </summary>
/// <remarks>
/// <para>An offer (BOLT 12 "Offers" writer, B12-OFR-01/02): <c>offer_chains</c> only off mainnet (the regtest, testnet
/// or signet chain hash), 16 random bytes of <c>offer_metadata</c>, <c>offer_amount</c> in msat (never a currency),
/// <c>offer_description</c>, <c>offer_absolute_expiry</c>, <c>offer_issuer</c>, <c>offer_quantity_max</c>,
/// <c>offer_issuer_id</c> = our node id (plan D2), and <c>offer_paths</c> when we have no announced open channel or
/// the request forces them: one two-hop message path per connected onion-message peer with an open channel (only when
/// none has one, other connected peers, with a warning; <see cref="SelectIntroductionNodes"/>,
/// <see cref="OfferOptions.MaxOfferPaths"/>), the peer as introduction node and our hop's <c>path_id</c> from
/// <see cref="OfferPathIds"/>. Our <c>offer_id</c> is SHA256 of the offer bytes; the string is <c>lno1...</c>.</para>
/// <para>The offer is saved (<see cref="IUnitOfWork.OfferDbRepository"/>, one save in its own scope) before it is
/// returned. Invoice_requests for it are answered by <see cref="InvoiceRequestHandler"/>.</para>
/// <para>Singleton; thread-safe.</para>
/// </remarks>
public sealed class OfferService : IOfferService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly OfferOptions _offerOptions;
    private readonly OfferPathIds _pathIds;
    private readonly IBlindedMessagePathBuilder _pathBuilder;
    private readonly IPeerManager _peerManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OfferService> _logger;

    public OfferService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                        IOptions<NodeOptions> nodeOptions, OfferPathIds pathIds,
                        IBlindedMessagePathBuilder pathBuilder, IPeerManager peerManager,
                        IChannelMemoryRepository channelMemoryRepository, IServiceProvider serviceProvider,
                        ILogger<OfferService> logger, IOptions<OfferOptions>? offerOptions = null,
                        TimeProvider? timeProvider = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _nodeOptions = nodeOptions;
        _pathIds = pathIds;
        _pathBuilder = pathBuilder;
        _peerManager = peerManager;
        _channelMemoryRepository = channelMemoryRepository;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _offerOptions = offerOptions?.Value ?? new OfferOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>Also false while no <see cref="IBolt12Signer"/> is registered: nobody could sign the invoices.
    /// </remarks>
    public bool IsAvailable =>
        _serviceProvider.GetService<IBolt12Signer>() is not null
     && IsOffersAvailable(_nodeOptions.Value, _serviceProvider.GetService<IOnionMessageService>());

    /// <summary>
    /// Whether offers work on a node with <paramref name="nodeOptions"/>: onion messages on
    /// (<paramref name="onionMessageService"/> available) and <c>option_route_blinding</c> advertised.
    /// </summary>
    public static bool IsOffersAvailable(NodeOptions nodeOptions, IOnionMessageService? onionMessageService)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        return onionMessageService is { IsAvailable: true }
            && nodeOptions.Features.OptionOnionMessages != FeatureSupport.No
            && nodeOptions.Features.OptionRouteBlinding != FeatureSupport.No;
    }

    /// <inheritdoc />
    public async Task<OfferModel> CreateOfferAsync(CreateOfferRequest request,
                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _timeProvider.GetUtcNow();
        Validate(request, now);
        if (!IsAvailable)
            throw new InvalidOperationException("Offers need option_onion_messages and option_route_blinding.");

        var metadata = RandomNumberGenerator.GetBytes(Bolt12Constants.OurOfferMetadataLength);
        var paths = request.ForcePaths || !HasAnnouncedChannel() ? CreateOfferPaths(metadata) : [];
        var nodeOptions = _nodeOptions.Value;
        var records = BuildRecords(request, nodeOptions.BitcoinNetwork, metadata, paths,
                                   _secureKeyManager.GetNodePubKey());
        var offerBytes = new Bolt12TlvStream(records).Encode();
        var offer = new OfferModel(new Hash(SHA256.HashData(offerBytes)),
                                   Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, offerBytes), offerBytes,
                                   request.Description, request.Amount, null, request.Issuer, request.QuantityMax,
                                   request.AbsoluteExpiry is { } expiry
                                       ? DateTimeOffset.FromUnixTimeSeconds(expiry.ToUnixTimeSeconds())
                                       : null,
                                   metadata, OfferIssuerKind.NodeId, paths.Count > 0,
                                   DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));

        cancellationToken.ThrowIfCancellationRequested();
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.OfferDbRepository.AddAsync(offer);
            await unitOfWork.SaveChangesAsync();
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Created offer {OfferId} for {Amount} with {PathCount} path(s)", offer.OfferId,
                                   offer.Amount is null ? "any amount" : $"{offer.Amount.MilliSatoshi} msat",
                                   paths.Count);

        return offer;
    }

    /// <inheritdoc />
    public async Task<OfferModel?> GetOfferAsync(Hash offerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OfferDbRepository.GetByIdAsync(offerId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OfferModel>> ListOffersAsync(bool activeOnly, int skip, int take,
                                                                 CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceScopeFactory.CreateScope();
        var offers = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OfferDbRepository
                                .ListAsync(activeOnly, skip, take);
        if (!activeOnly)
            return offers;

        var now = _timeProvider.GetUtcNow();
        return offers.Where(o => o.IsActive(now)).ToList();
    }

    /// <inheritdoc />
    public async Task<OfferInvoiceCounts> GetInvoiceCountsAsync(Hash offerId,
                                                                CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OfferDbRepository
                          .GetInvoiceCountsAsync(offerId, _timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task<OfferModel?> DisableOfferAsync(Hash offerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var offer = await unitOfWork.OfferDbRepository.GetByIdAsync(offerId);
        if (offer is not { Status: OfferStatus.Active })
            return offer;

        offer.Disable(_timeProvider.GetUtcNow());
        await unitOfWork.OfferDbRepository.UpdateAsync(offer);
        await unitOfWork.SaveChangesAsync();

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Disabled offer {OfferId}", offerId);

        return offer;
    }

    /// <summary>
    /// The offer's TLV records in ascending type order (BOLT 12 "Offers" writer).
    /// </summary>
    internal static List<Bolt12TlvRecord> BuildRecords(CreateOfferRequest request, BitcoinNetwork network,
                                                       ReadOnlyMemory<byte> metadata,
                                                       IReadOnlyList<WireBlindedPath> paths, CompactPubKey issuerId)
    {
        var records = new List<Bolt12TlvRecord>();
        // SHOULD omit offer_chains for bitcoin only; MUST name the chain otherwise
        if (network != BitcoinNetwork.Mainnet)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferChains, (byte[])network.ChainHash));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferMetadata, metadata));
        if (request.Amount is { } amount)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferAmount, TruncatedInt.EncodeTu64(amount.MilliSatoshi)));
        if (request.Description is not null)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferDescription,
                                            Encoding.UTF8.GetBytes(request.Description)));
        if (request.AbsoluteExpiry is { } expiry)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferAbsoluteExpiry,
                                            TruncatedInt.EncodeTu64((ulong)expiry.ToUnixTimeSeconds())));
        if (paths.Count > 0)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferPaths, BlindedPathCodec.EncodeList(paths)));
        if (request.Issuer is not null)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferIssuer, Encoding.UTF8.GetBytes(request.Issuer)));
        if (request.QuantityMax is { } quantityMax)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferQuantityMax, TruncatedInt.EncodeTu64(quantityMax)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferIssuerId, (byte[])issuerId));
        return records;
    }

    private static void Validate(CreateOfferRequest request, DateTimeOffset now)
    {
        if (request.Amount is { IsZero: true })
            throw new ArgumentException("An offer amount must be positive; leave it out for any amount.",
                                        nameof(request));
        if (request.Amount is not null && string.IsNullOrEmpty(request.Description))
            throw new ArgumentException("An offer with an amount needs a description.", nameof(request));
        if (request.AbsoluteExpiry is { } expiry && expiry <= now)
            throw new ArgumentException("The offer's absolute expiry is in the past.", nameof(request));
    }

    private bool HasAnnouncedChannel() =>
        _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open)
                                .Any(ChannelAnnouncementService.IsAnnounced);

    /// <summary>
    /// Two-hop message paths to us, introduced by connected onion-message peers (<see cref="SelectIntroductionNodes"/>).
    /// </summary>
    private List<WireBlindedPath> CreateOfferPaths(byte[] metadata)
    {
        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var pathFinder = new OnionMessagePathFinder(_peerManager, _channelMemoryRepository, ourNodeId, 0,
                                                    outbox: _serviceProvider.GetService<IPeerOnionMessageOutbox>());
        var peers = pathFinder.ListOnionMessagePeers();
        if (peers.Count == 0)
            throw new InvalidOperationException("The offer needs offer_paths (no announced channel), and no connected "
                                              + "peer supports onion messages to introduce one.");

        var introductionNodes = SelectIntroductionNodes(peers, pathFinder.HasOpenChannelWith,
                                                        _offerOptions.MaxOfferPaths, out var withoutChannel);
        if (withoutChannel && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("No connected onion-message peer has an open channel with us: the offer's paths are "
                             + "introduced by {Count} peer(s) without one, and they stop working for good once those "
                             + "peers disconnect (we do not reconnect to peers without channels)",
                               introductionNodes.Count);

        var pathId = _pathIds.Compute(metadata);
        return introductionNodes.Select(peer => WireBlindedPath.FromBlindedPath(
                                            _pathBuilder.CreateMessagePath([peer, ourNodeId], pathId)))
                                .ToList();
    }

    /// <summary>
    /// The introduction nodes of an offer's paths: up to <paramref name="max"/> of the <paramref name="peers"/> with an
    /// open channel with us, in order; only when none has one, up to <paramref name="max"/> of the others
    /// (<paramref name="withoutChannel"/> true).
    /// </summary>
    /// <remarks>
    /// <c>offer_paths</c> are fixed in the offer, and we reconnect only to peers we have channels with, so a path
    /// through a peer without a channel dies with its connection.
    /// </remarks>
    internal static IReadOnlyList<CompactPubKey> SelectIntroductionNodes(IReadOnlyList<CompactPubKey> peers,
                                                                        Func<CompactPubKey, bool> hasOpenChannel,
                                                                        int max, out bool withoutChannel)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(hasOpenChannel);
        var channelPeers = peers.Where(hasOpenChannel).Take(max).ToList();
        withoutChannel = channelPeers.Count == 0 && peers.Count > 0 && max > 0;
        return withoutChannel ? peers.Take(max).ToList() : channelPeers;
    }
}