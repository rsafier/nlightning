using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Invoices;

using Bolt11.Models;
using Channels.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Models;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Gossip.Announcements;
using Gossip.Graph.Interfaces;
using Gossip.Interfaces;
using Routing;

/// <summary>
/// Issues and looks up our BOLT 11 invoices (<see cref="IInvoiceService"/>, BOLT2 plan N8-T2, ONION M4-T3).
/// </summary>
/// <remarks>
/// <para><see cref="CreateInvoiceAsync"/>: the preimage and the payment secret are 32 bytes each from the OS CSPRNG;
/// the payment hash is SHA256(preimage). The invoice is encoded with <c>NLightning.Bolt11</c>'s node path (W0-D:
/// <c>Invoice.Encode()</c> signs with <see cref="ISecureKeyManager"/>'s node key and validates the BOLT 11 writer
/// rules first; <c>var_onion_optin</c> and <c>payment_secret</c> compulsory, <c>basic_mpp</c> optional unless
/// <c>Features:BasicMpp</c> is <c>No</c>), with <c>s</c>,
/// <c>c</c> = <see cref="RoutingOptions.InvoiceMinFinalCltvExpiry"/> and <c>x</c>. It is persisted before it is
/// returned, so a payment never arrives for an invoice we forgot.</para>
/// <para>Persistence goes through a fresh DI scope per call: <see cref="IInvoiceDbRepository"/> stages, the scope's
/// <see cref="IUnitOfWork"/> commits. Both must share the scope's database context (see the Payments
/// <c>CLAUDE.md</c> section for the registration).</para>
/// <para>Route hints (NL-245): a payer that is not our peer can reach a node without announced channels only through
/// an <c>r</c> field. Each <c>Open</c> channel whose link is up (<see cref="IPeerLivenessProbe"/>, as LND skips
/// inactive channels) and whose peer sent us its <c>channel_update</c>
/// (<see cref="IChannelUpdateService.TryGetRemoteChannelUpdate"/>, not disabled) gets a one-hop hint: the peer's node
/// id, the channel's short channel id (the peer's alias <c>RemoteAlias</c> for an <c>option_scid_alias</c> channel)
/// and the <b>peer's</b> fee and <c>cltv_expiry_delta</c>. BOLT 11 describes each entry as the channel from its
/// <c>pubkey</c> towards the payee, which the peer forwards over and charges for under its own policy; our policy
/// never applies to that direction. For an invoice with an amount, channels whose peer cannot send it (the peer's
/// spendable balance, <see cref="GetPeerSpendable"/>, or the peer's <c>htlc_minimum_msat</c>/<c>htlc_maximum_msat</c>)
/// are skipped. At most <see cref="MaxRouteHints"/> hints, the peers with the largest spendable balance first (as
/// LND). A channel whose peer's update we do not have gets no hint
/// (LND does the same).</para>
/// <para>Public channels (BOLT 7 plan G4, <see cref="InvoiceOptions.RouteHints"/>): with
/// <see cref="InvoiceRouteHintMode.Auto"/> (the default) an invoice carries no hint at all once one of our announced
/// channels (<see cref="ChannelAnnouncementService.IsAnnounced"/>) is <c>Open</c>, has its link up and its peer can send
/// us the amount (<see cref="GetPeerSpendable"/>; any inbound for an invoice without an amount): payers then find us
/// through the gossip graph, and hints would only reveal our private channels. The channel must also be in our own
/// gossip graph with both policies for <see cref="InvoiceOptions.PublicChannelGracePeriod"/> (so the announcement
/// reached the network first). A node with only private channels keeps its hints. <see cref="InvoiceRouteHintMode.Always"/> forces the hints, <see cref="InvoiceRouteHintMode.Never"/> drops
/// them.</para>
/// <para>Singleton; thread-safe.</para>
/// </remarks>
public sealed class InvoiceService : IInvoiceService
{
    /// <summary>
    /// The most route hints an invoice carries.
    /// </summary>
    public const int MaxRouteHints = 3;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly IPeerLivenessProbe? _peerLivenessProbe;
    private readonly InvoiceRouteHintMode _routeHintMode;
    private readonly TimeSpan _publicChannelGracePeriod;
    private readonly IGraphStore? _graphStore;
    private readonly TimeProvider _timeProvider;

    /// <param name="serviceScopeFactory">Scopes for persistence.</param>
    /// <param name="secureKeyManager">The node key that signs the invoices.</param>
    /// <param name="nodeOptions">The network and the routing options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="channelMemoryRepository">Our channels, for route hints; without it invoices carry none.</param>
    /// <param name="channelUpdateService">The peers' <c>channel_update</c>s, for route hints; without it invoices
    /// carry none.</param>
    /// <param name="peerLivenessProbe">Which channels have their link up; without it every <c>Open</c> channel counts
    /// as up.</param>
    /// <param name="invoiceOptions">When invoices carry hints; without it <see cref="InvoiceRouteHintMode.Auto"/>.
    /// </param>
    /// <param name="graphStore">Our gossip graph: an announced channel counts as public only once it is there with
    /// both policies for <see cref="InvoiceOptions.PublicChannelGracePeriod"/>; without it every invoice keeps its hints.
    /// </param>
    /// <param name="timeProvider">The clock of that grace period (the system clock by default).</param>
    public InvoiceService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                          IOptions<NodeOptions> nodeOptions, ILogger<InvoiceService> logger,
                          IChannelMemoryRepository? channelMemoryRepository = null,
                          IChannelUpdateService? channelUpdateService = null,
                          IPeerLivenessProbe? peerLivenessProbe = null,
                          IOptions<InvoiceOptions>? invoiceOptions = null, IGraphStore? graphStore = null,
                          TimeProvider? timeProvider = null)
    {
        _routeHintMode = invoiceOptions?.Value.RouteHints ?? InvoiceRouteHintMode.Auto;
        _publicChannelGracePeriod = invoiceOptions?.Value.PublicChannelGracePeriod
                                 ?? InvoiceOptions.DefaultPublicChannelGracePeriod;
        _graphStore = graphStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _nodeOptions = nodeOptions;
        _logger = logger;
        _channelMemoryRepository = channelMemoryRepository;
        _channelUpdateService = channelUpdateService;
        _peerLivenessProbe = peerLivenessProbe;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">If the amount or the expiry is zero.</exception>
    /// <exception cref="ArgumentException">If the description is longer than BOLT 11 allows (639 UTF-8
    /// bytes).</exception>
    /// <exception cref="Bolt11.Exceptions.InvoiceSerializationException">If the invoice fails the BOLT 11 writer
    /// rules. Nothing is persisted in any of these cases.</exception>
    public async Task<InvoiceModel> CreateInvoiceAsync(LightningMoney? amount, string description, uint? expirySeconds,
                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (amount is { IsZero: true })
            throw new ArgumentOutOfRangeException(nameof(amount), "An invoice amount must be positive; use null for "
                                                                + "any amount.");

        var nodeOptions = _nodeOptions.Value;
        var routing = nodeOptions.Routing;
        var expiry = expirySeconds ?? routing.InvoiceExpirySeconds;
        if (expiry == 0)
            throw new ArgumentOutOfRangeException(nameof(expirySeconds), "The expiry must be positive.");

        var preimage = RandomNumberGenerator.GetBytes(CryptoConstants.SecretLen);
        var paymentHash = SHA256.HashData(preimage);
        var paymentSecret = RandomNumberGenerator.GetBytes(CryptoConstants.SecretLen);

        var invoice = new Invoice(amount ?? LightningMoney.Zero, description, PaymentTarget.FromWireBytes(paymentHash),
                                  PaymentTarget.FromWireBytes(paymentSecret), nodeOptions.BitcoinNetwork,
                                  _secureKeyManager)
        {
            MinFinalCltvExpiry = routing.InvoiceMinFinalCltvExpiry
        };
        if (nodeOptions.Features.BasicMpp != FeatureSupport.No)
        {
            // var_onion_optin (8) and payment_secret (14) compulsory, as the encoder would add them, plus basic_mpp
            // (17) optional: the HTLC switch receives multi-part payments (ABCD W6-B)
            var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
            features.SetFeature(Feature.BasicMpp, false);
            invoice.Features = features;
        }

        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + expiry);
        foreach (var routeHint in await BuildRouteHintsAsync(amount, cancellationToken))
            invoice.AddRouteHint(routeHint);

        var bolt11 = invoice.Encode();

        var model = new InvoiceModel(new Hash(paymentHash), new Secret(preimage), new Secret(paymentSecret), amount,
                                     description, bolt11, DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp),
                                     expiry, routing.InvoiceMinFinalCltvExpiry);

        cancellationToken.ThrowIfCancellationRequested();

        using (var scope = _serviceScopeFactory.CreateScope())
        {
            var invoiceDbRepository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await invoiceDbRepository.AddAsync(model);
            await unitOfWork.SaveChangesAsync();
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Created invoice {PaymentHash} for {Amount}", model.PaymentHash,
                                   amount is null ? "any amount" : $"{amount.MilliSatoshi} msat");

        return model;
    }

    /// <summary>
    /// The route hints for a new invoice (see the class remarks): one single-hop hint per usable private channel.
    /// </summary>
    internal async Task<IReadOnlyList<RoutingInfoCollection>> BuildRouteHintsAsync(LightningMoney? amount,
                                                                                 CancellationToken cancellationToken)
    {
        if (_channelMemoryRepository is null || _channelUpdateService is null
                                            || _routeHintMode == InvoiceRouteHintMode.Never)
            return [];

        if (_routeHintMode == InvoiceRouteHintMode.Auto
         && await HasReachablePublicChannelAsync(amount, cancellationToken) is { } publicChannel)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("No route hints: our announced channel {ShortChannelId} can receive {Amount}",
                                 publicChannel.ShortChannelId,
                                 amount is null ? "payments" : $"{amount.MilliSatoshi} msat");
            return [];
        }

        var candidates = new List<(ulong Spendable, RoutingInfo Hint)>();
        foreach (var channel in _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open))
        {
            if (!_channelUpdateService.TryGetRemoteChannelUpdate(channel.ChannelId, out var update)
             || update is null || update.IsDisabled)
                continue;

            var shortChannelId = channel.ChannelParams.UseScidAlias > FeatureSupport.No
                                     ? channel.RemoteAlias ?? default
                                     : channel.ShortChannelId;
            if (shortChannelId == default)
                continue;

            var spendable = GetPeerSpendable(channel);
            if (amount is not null
             && (spendable < amount.MilliSatoshi || amount.MilliSatoshi < update.HtlcMinimumMsat
              || amount.MilliSatoshi > update.HtlcMaximumMsat))
                continue;

            if (_peerLivenessProbe is not null
             && !await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                continue;

            candidates.Add((spendable, new RoutingInfo(channel.RemoteNodeId, shortChannelId, update.FeeBaseMsat,
                                                       update.FeeProportionalMillionths, update.CltvExpiryDelta)));
        }

        return candidates.OrderByDescending(c => c.Spendable)
                         .Take(MaxRouteHints)
                         .Select(c => new RoutingInfoCollection { c.Hint })
                         .ToList();
    }

    /// <summary>
    /// One of our announced channels that is <c>Open</c>, has its link up and whose peer can send us
    /// <paramref name="amount"/> (any amount when null); null when there is none.
    /// </summary>
    private async Task<ChannelModel?> HasReachablePublicChannelAsync(LightningMoney? amount,
                                                                    CancellationToken cancellationToken)
    {
        var needed = amount?.MilliSatoshi ?? 1;
        foreach (var channel in _channelMemoryRepository!.FindChannels(c => c.State == ChannelState.Open))
        {
            if (!ChannelAnnouncementService.IsAnnounced(channel) || GetPeerSpendable(channel) < needed
             || !IsInOurGraph(channel))
                continue;

            if (_peerLivenessProbe is not null
             && !await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                continue;

            return channel;
        }

        return null;
    }

    /// <summary>
    /// Whether payers can plausibly route to us over <paramref name="channel"/>: our own graph holds it with both
    /// directions' policies (the peer's towards us not disabled) and has held it for
    /// <see cref="InvoiceOptions.PublicChannelGracePeriod"/>, so our announcement and both updates had time to reach
    /// the network. Without a graph (none registered, or gossip off) nothing counts.
    /// </summary>
    private bool IsInOurGraph(ChannelModel channel)
    {
        var scid = channel.ShortChannelId;
        if (_graphStore is null || scid == default)
            return false;

        if (!_graphStore.TryGetChannel(scid, out var graphChannel)
         || graphChannel.GetPolicy(0) is null || graphChannel.GetPolicy(1) is null)
            return false;

        var peerDirection = graphChannel.NodeId1 == channel.RemoteNodeId ? (byte)0 : (byte)1;
        if (graphChannel.GetPolicy(peerDirection)!.IsDisabled)
            return false;

        return _graphStore.TryGetChannelReceivedAt(scid, out var receivedAt)
            && _timeProvider.GetUtcNow() - receivedAt >= _publicChannelGracePeriod;
    }

    /// <summary>
    /// About what the peer can still send us on <paramref name="channel"/>, in msat: its balance (gross, NL-062) minus
    /// the HTLCs it offered that are not settled yet, minus the reserve we require of it, minus, when it funded the
    /// channel, the commitment fee with one more HTLC (every pending HTLC counted as untrimmed). Never below zero.
    /// </summary>
    internal static ulong GetPeerSpendable(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var balance = (UInt128)channel.RemoteBalance.MilliSatoshi;
        UInt128 cost = channel.ChannelParams.Local.ChannelReserveAmount.MilliSatoshi;
        var pendingHtlcs = 0;
        var feeratePerKw = (ulong)channel.ChannelParams.FeeRateAmountPerKw.Satoshi;
        if (channel.Commitments is { } commitments)
        {
            feeratePerKw = commitments.LatestFeeratePerKw;
            foreach (var htlc in commitments.Htlcs.Values)
            {
                if (HtlcStateTable.IsFinal(htlc.State))
                    continue;

                pendingHtlcs++;
                if (htlc.Direction == HtlcDirection.Incoming)
                    cost += htlc.AmountMsat;
            }
        }

        if (!channel.IsInitiator)
            cost += (UInt128)CommitmentFeeCalculator.FunderCostSatoshis(feeratePerKw,
                                                                        channel.ChannelParams.OptionAnchorOutputs,
                                                                        pendingHtlcs + 1) * 1_000;

        return balance > cost ? (ulong)(balance - cost) : 0;
    }

    /// <inheritdoc />
    public async Task<InvoiceModel?> GetInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceScopeFactory.CreateScope();
        var invoiceDbRepository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
        return await invoiceDbRepository.GetByPaymentHashAsync(paymentHash);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvoiceModel>> ListInvoicesAsync(int skip, int take,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceScopeFactory.CreateScope();
        var invoiceDbRepository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
        return await invoiceDbRepository.ListAsync(skip, take);
    }

    /// <inheritdoc />
    public async Task<bool> CancelInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _serviceScopeFactory.CreateScope();
        var invoiceDbRepository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
        var invoice = await invoiceDbRepository.GetByPaymentHashAsync(paymentHash);
        if (invoice is not { Status: InvoiceStatus.Open })
            return false;

        invoice.Cancel();
        await invoiceDbRepository.UpdateAsync(invoice);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Canceled invoice {PaymentHash}", paymentHash);

        return true;
    }
}