using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.Cashu.PaymentProcessor;

using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Registers the Cashu CDK payment processor (Cashu plan C1, NL-902).
/// </summary>
public static class CashuPaymentProcessorServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="CashuPaymentProcessorOptions"/> from <c>Cashu:PaymentProcessor</c> (validated at start against
    /// the node's network) and registers <see cref="CdkPaymentProcessorService"/>. Idempotent. The server itself is
    /// <see cref="AddCashuPaymentProcessorHost"/>.
    /// </summary>
    /// <remarks>Needs the node's <c>IInvoiceService</c>, <c>IPaymentService</c>, <c>IPaymentEventSource</c> and
    /// <c>IOptions&lt;NodeOptions&gt;</c>, resolved only when the processor is enabled.</remarks>
    public static IServiceCollection AddCashuPaymentProcessor(this IServiceCollection services,
                                                              IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(CdkPaymentProcessorService)))
            return services;

        services.AddOptions<CashuPaymentProcessorOptions>()
                .Bind(configuration.GetSection(CashuPaymentProcessorOptions.SectionName))
                .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CashuPaymentProcessorOptions>,
                                      CashuPaymentProcessorOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CdkPaymentProcessorService>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="CashuPaymentProcessorHost"/>, the hosted gRPC server (a no-op while the processor is
    /// disabled). Add it after the node's own hosted service, so the server starts once the node runs.
    /// </summary>
    public static IServiceCollection AddCashuPaymentProcessorHost(this IServiceCollection services)
    {
        services.TryAddSingleton<CashuPaymentProcessorHost>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, CashuPaymentProcessorHost>(
                sp => sp.GetRequiredService<CashuPaymentProcessorHost>()));
        return services;
    }

    private sealed class CashuPaymentProcessorOptionsValidator(IOptions<NodeOptions>? nodeOptions)
        : IValidateOptions<CashuPaymentProcessorOptions>
    {
        public ValidateOptionsResult Validate(string? name, CashuPaymentProcessorOptions options)
        {
            var isMainnet = nodeOptions?.Value.BitcoinNetwork == BitcoinNetwork.Mainnet;
            var errors = options.GetValidationErrors(isMainnet);
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}