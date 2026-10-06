using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc;

using Domain.Protocol.ValueObjects;
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
        return services;
    }

    /// <summary>Registers <see cref="LndGrpcHost"/>, the hosted gRPC server (a no-op while disabled), with its files in
    /// <c>LndGrpc:DataDirectory</c> resolved against <paramref name="configPath"/>.</summary>
    public static IServiceCollection AddLndGrpcHost(this IServiceCollection services, string configPath)
    {
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

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}