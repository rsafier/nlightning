using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Invoices;

using Bolt11.Models;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
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
/// <para>Singleton; thread-safe.</para>
/// </remarks>
public sealed class InvoiceService : IInvoiceService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                          IOptions<NodeOptions> nodeOptions, ILogger<InvoiceService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _nodeOptions = nodeOptions;
        _logger = logger;
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