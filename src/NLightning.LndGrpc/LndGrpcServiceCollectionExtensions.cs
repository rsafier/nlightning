using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc;

using Application.Payments.Interception;
using Domain.Protocol.ValueObjects;
using Macaroons;
using Services;
using NodeOptions = Domain.Node.Options.NodeOptions;

/// <summary>Registers the LND-compatible gRPC server (<c>LND_GRPC_PLAN.md</c>, NL-1161).</summary>
public static class LndGrpcServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="LndGrpcOptions"/> from <c>LndGrpc</c> (refused on mainnet unless <c>AllowMainnet</c>) and
    /// registers <see cref="LightningService"/>. Idempotent. The server itself is <see cref="AddLndGrpcHost"/>.
    /// </summary>
    public static IServiceCollection AddLndGrpc(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(LightningService)))
            return services;

        services.AddOptions<LndGrpcOptions>()
                .Bind(configuration.GetSection(LndGrpcOptions.SectionName))
                .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LndGrpcOptions>,
                                      LndGrpcOptionsValidator>());
        services.AddSingleton<LightningService>();
        // Wave 2 (NL-1164) and wave 3 (NL-1183, NL-1184): routerrpc.Router (payments, HtlcInterceptor),
        // invoicesrpc.Invoices and walletrpc.WalletKit
        services.AddSingleton<RouterService>();
        // NL-1182: the interceptor hub follows these options from the start (requireinterceptor holds replayed forwards
        // before any client connects)
        services.AddSingleton<IConfigureOptions<HtlcInterceptorSettings>>(sp =>
            new ConfigureOptions<HtlcInterceptorSettings>(settings =>
            {
                var configured = sp.GetRequiredService<IOptions<LndGrpcOptions>>().Value.ToInterceptorSettings();
                settings.CltvRejectDelta = configured.CltvRejectDelta;
                settings.CltvInterceptDelta = configured.CltvInterceptDelta;
                settings.MaxHeld = configured.MaxHeld;
                settings.RequireInterceptor = configured.RequireInterceptor;
            }));
        services.AddSingleton<InvoicesService>();
        services.AddSingleton<WalletKitService>();
        services.Configure<Infrastructure.Bitcoin.KeyRing.KeyRingOptions>(configuration.GetSection("LndGrpc:Signer"));
        services.AddSingleton<SignerService>();
        services.AddSingleton<StateService>();
        services.AddSingleton<VersionerService>();
        services.AddSingleton<ChainNotifierService>();
        return services;
    }

    /// <summary>Registers <see cref="LndGrpcHost"/>, the hosted gRPC server (a no-op while disabled), with its files in
    /// <c>LndGrpc:DataDirectory</c> resolved against <paramref name="configPath"/>.</summary>
    public static IServiceCollection AddLndGrpcHost(this IServiceCollection services, string configPath)
    {
        // The macaroon root keys (BakeMacaroon, NL-1169), shared by the host's verifier and LightningService
        services.AddSingleton(sp => new LndRootKeyStore(sp.GetRequiredService<IOptions<LndGrpcOptions>>().Value
                                                          .ResolveDataDirectory(configPath),
            sp.GetService<NLightning.Domain.Signing.NodeSigningContext>()));
        services.AddHostedService(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LndGrpcOptions>>();
            return new LndGrpcHost(sp, options, options.Value.ResolveDataDirectory(configPath),
                                   sp.GetRequiredService<ILogger<LndGrpcHost>>(), sp.GetService<TimeProvider>());
        });
        return services;
    }
}

/// <summary>Refuses the server on mainnet unless <c>LndGrpc:AllowMainnet</c> (the NL-998 rule).</summary>
internal sealed class LndGrpcOptionsValidator : IValidateOptions<LndGrpcOptions>
{
    private readonly NodeOptions _nodeOptions;

    public LndGrpcOptionsValidator(IOptions<NodeOptions> nodeOptions)
    {
        _nodeOptions = nodeOptions.Value;
    }

    public ValidateOptionsResult Validate(string? name, LndGrpcOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        failures.AddRange(options.GetValidationErrors());
        if (_nodeOptions.BitcoinNetwork == BitcoinNetwork.Mainnet && !options.AllowMainnet)
            failures.Add($"{LndGrpcOptions.SectionName} is refused on mainnet unless "
                       + $"{LndGrpcOptions.SectionName}:AllowMainnet.");

        if (options.EnableSigner && _nodeOptions.BitcoinNetwork == BitcoinNetwork.Mainnet && !options.AllowSignerOnMainnet)
            failures.Add("LndGrpc:EnableSigner is refused on mainnet unless LndGrpc:AllowSignerOnMainnet.");
        if (options.EnableSigner && System.Net.IPAddress.TryParse(options.ListenAddress, out var address)
                                 && !System.Net.IPAddress.IsLoopback(address)
                                 && string.IsNullOrWhiteSpace(options.ClientCaPath))
            failures.Add("Remote swap signing requires LndGrpc:ClientCaPath (mutual TLS).");

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}