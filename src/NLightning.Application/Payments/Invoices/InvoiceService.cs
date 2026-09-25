using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Invoices;

using Bolt11.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Gossip.Interfaces;
using Routing;

/// <summary>
/// Issues and looks up our BOLT 11 invoices (<see cref="IInvoiceService"/>, BOLT2 plan N8-T2, ONION M4-T3).
/// </summary>
/// <remarks>
/// <para><see cref="CreateInvoiceAsync"/>: the preimage and the payment secret are 32 bytes each from the OS CSPRNG;
/// the payment hash is SHA256(preimage). The invoice is encoded with <c>NLightning.Bolt11</c>'s node path (W0-D:
/// <c>Invoice.Encode()</c> signs with <see cref="ISecureKeyManager"/>'s node key and validates the BOLT 11 writer
/// rules first; <c>var_onion_optin</c> and <c>payment_secret</c> compulsory, no <c>basic_mpp</c>), with <c>s</c>,
/// <c>c</c> = <see cref="RoutingOptions.InvoiceMinFinalCltvExpiry"/> and <c>x</c>. It is persisted before it is
/// returned, so a payment never arrives for an invoice we forgot.</para>
/// <para>Persistence goes through a fresh DI scope per call: <see cref="IInvoiceDbRepository"/> stages, the scope's
/// <see cref="IUnitOfWork"/> commits. Both must share the scope's database context (see the Payments
/// <c>CLAUDE.md</c> section for the registration).</para>
/// <para>Route hints (NL-245): our channels are never announced, so a payer that is not our peer can only reach us
/// through an <c>r</c> field. Each <c>Open</c> channel whose peer sent us its <c>channel_update</c>
/// (<see cref="IChannelUpdateService.TryGetRemoteChannelUpdate"/>, not disabled) gets a one-hop hint: the peer's node
/// id, the channel's short channel id (the peer's alias <c>RemoteAlias</c> for an <c>option_scid_alias</c> channel)
/// and the <b>peer's</b> fee and <c>cltv_expiry_delta</c>. BOLT 11 describes each entry as the channel from its
/// <c>pubkey</c> towards the payee, which the peer forwards over and charges for under its own policy; our policy
/// never applies to that direction. For an invoice with an amount, channels whose peer cannot send it (peer balance, or
/// the peer's <c>htlc_minimum_msat</c>/<c>htlc_maximum_msat</c>) are skipped. At most <see cref="MaxRouteHints"/>
/// hints, the peers with the largest balance first (as LND). A channel whose peer's update we do not have gets no hint
/// (LND does the same).</para>
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

    /// <param name="serviceScopeFactory">Scopes for persistence.</param>
    /// <param name="secureKeyManager">The node key that signs the invoices.</param>
    /// <param name="nodeOptions">The network and the routing options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="channelMemoryRepository">Our channels, for route hints; without it invoices carry none.</param>
    /// <param name="channelUpdateService">The peers' <c>channel_update</c>s, for route hints; without it invoices
    /// carry none.</param>
    public InvoiceService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                          IOptions<NodeOptions> nodeOptions, ILogger<InvoiceService> logger,
                          IChannelMemoryRepository? channelMemoryRepository = null,
                          IChannelUpdateService? channelUpdateService = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _nodeOptions = nodeOptions;
        _logger = logger;
        _channelMemoryRepository = channelMemoryRepository;
        _channelUpdateService = channelUpdateService;
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
        invoice.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp + expiry);
        foreach (var routeHint in BuildRouteHints(amount))
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
    internal IReadOnlyList<RoutingInfoCollection> BuildRouteHints(LightningMoney? amount)
    {
        if (_channelMemoryRepository is null || _channelUpdateService is null)
            return [];

        var candidates = new List<(ChannelModel Channel, RoutingInfo Hint)>();
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

            if (amount is not null
             && (channel.RemoteBalance < amount || amount.MilliSatoshi < update.HtlcMinimumMsat
              || amount.MilliSatoshi > update.HtlcMaximumMsat))
                continue;

            candidates.Add((channel, new RoutingInfo(channel.RemoteNodeId, shortChannelId, update.FeeBaseMsat,
                                                     update.FeeProportionalMillionths, update.CltvExpiryDelta)));
        }

        return candidates.OrderByDescending(c => c.Channel.RemoteBalance.MilliSatoshi)
                         .Take(MaxRouteHints)
                         .Select(c => new RoutingInfoCollection { c.Hint })
                         .ToList();
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