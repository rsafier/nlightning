using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers;

using Domain.Node.Options;
using Domain.Offers.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Receive;

/// <summary>
/// Registers BOLT 12 offers (wave B12; the receive side of lane B12-D).
/// </summary>
public static class OffersServiceCollectionExtensions
{
    private static int s_marginUpgradeLogged;

    /// <summary>
    /// Registers <see cref="OfferService"/> (as itself and <see cref="IOfferService"/>), the invoice_request handler
    /// <see cref="InvoiceRequestHandler"/> (an <see cref="IOnionMessageHandler"/> for type 64, picked up by the
    /// onion-message service), <see cref="OfferPathIds"/>, <see cref="InvoiceRequestRateLimiter"/> and
    /// <see cref="ExpiredBolt12InvoicePruner"/> (the host starts it, NL-448),
    /// <see cref="IBlindedPaymentPathSource"/> (<see cref="BlindedPaymentPathFactory"/> over the payments'
    /// <c>BlindedPathBuilder</c>, so call it after <c>AddPaymentsServices()</c>). Binds nothing: the host configures
    /// <see cref="OfferOptions"/> from <see cref="OfferOptions.SectionName"/>; the defaults apply otherwise, and invalid
    /// options fall back to them with an error log. The <see cref="IBolt12Signer"/> is taken when registered (lane
    /// B12-B, <c>AddBitcoinInfrastructure</c>); without it offers are unavailable and invoice_requests are ignored.
    /// Idempotent (TryAdd).
    /// </summary>
    public static IServiceCollection AddOffersServices(this IServiceCollection services)
    {
        services.AddOptions<OfferOptions>().PostConfigure(options => options.UpgradeFormerDefaults());
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<OfferPathIds>(sp));
        services.TryAddSingleton(sp =>
        {
            var options = GetOptions(sp);
            return new InvoiceRequestRateLimiter(options.InvoiceRequestsPerSecondPerOffer,
                                                 options.InvoiceRequestsPerSecond, sp.GetService<TimeProvider>());
        });
        services.TryAddSingleton<IBlindedPaymentPathSource>(sp => ActivatorUtilities
                                                                .CreateInstance<BlindedPaymentPathFactory>(
                                                                     sp, Options.Create(GetOptions(sp))));
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<OfferService>(
                                     sp, Options.Create(GetOptions(sp))));
        services.TryAddSingleton<IOfferService>(sp => sp.GetRequiredService<OfferService>());
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<ExpiredBolt12InvoicePruner>(
                                     sp, Options.Create(GetOptions(sp))));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOnionMessageHandler, InvoiceRequestHandler>(sp =>
            new InvoiceRequestHandler(sp.GetRequiredService<IServiceScopeFactory>(), sp,
                                      sp.GetRequiredService<ISecureKeyManager>(), sp.GetService<IBolt12Signer>(),
                                      sp.GetRequiredService<IBlindedPaymentPathSource>(),
                                      sp.GetRequiredService<OfferPathIds>(),
                                      sp.GetRequiredService<IOptions<NodeOptions>>(),
                                      sp.GetRequiredService<InvoiceRequestRateLimiter>(),
                                      sp.GetRequiredService<ILogger<InvoiceRequestHandler>>(),
                                      Options.Create(GetOptions(sp)), sp.GetService<TimeProvider>())));
        return services;
    }

    /// <summary>
    /// The configured <see cref="OfferOptions"/>, or the defaults when they are invalid (logged as an error); a margin
    /// raised from the former template default is logged once as a warning (NL-743).
    /// </summary>
    private static OfferOptions GetOptions(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetService<IOptions<OfferOptions>>()?.Value ?? new OfferOptions();
        if (options.PathLifetimeMarginRaisedFromFormerDefault && Interlocked.Exchange(ref s_marginUpgradeLogged, 1) == 0)
        {
            serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(OffersServiceCollectionExtensions))
                           .LogWarning("Offers:PathLifetimeMarginBlocks is {Former}, the default this node's "
                                     + "appsettings.json was written with: using {Default} instead, since payers add "
                                     + "a random delta to the final expiry (Eclair up to 350 blocks, LDK up to 432) "
                                     + "and were refused (NL-719, NL-723, NL-743). Set it to {Default} in the file, or "
                                     + "to another value to pin one", OfferOptions.FormerDefaultPathLifetimeMarginBlocks,
                                       OfferOptions.DefaultPathLifetimeMarginBlocks,
                                       OfferOptions.DefaultPathLifetimeMarginBlocks);
        }

        var errors = options.GetValidationErrors();
        if (errors.Count == 0)
            return options;

        serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(OffersServiceCollectionExtensions))
                       .LogError("Invalid Offers options, using the defaults: {Errors}", string.Join(" ", errors));
        return new OfferOptions();
    }
}