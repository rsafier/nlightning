using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.LnBackend;

using Domain.Protocol.ValueObjects;
using NodeOptions = Domain.Node.Options.NodeOptions;

/// <summary>Registers the hold-invoice and cln.Node gRPC backends (the Bark/ASP seam, NL-1148).</summary>
public static class LnBackendServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="LnBackendOptions"/> from <c>LnBackend</c> (refused on mainnet unless
    /// <c>AllowMainnet</c>) and registers <see cref="HoldBackendService"/> and
    /// <see cref="ClnNodeBackendService"/>. Idempotent. The server itself is <see cref="AddLnBackendHost"/>.
    /// </summary>
    public static IServiceCollection AddLnBackend(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(HoldBackendService)))
            return services;

        services.AddOptions<LnBackendOptions>()
                .Bind(configuration.GetSection(LnBackendOptions.SectionName))
                .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LnBackendOptions>,
                                      LnBackendOptionsValidator>());
        services.AddSingleton<HoldBackendService>();
        services.AddSingleton<ClnNodeBackendService>();
        return services;
    }

    /// <summary>Registers <see cref="LnBackendHost"/>, the hosted gRPC server (a no-op while the backend is
    /// disabled).</summary>
    public static IServiceCollection AddLnBackendHost(this IServiceCollection services)
    {
        services.AddHostedService<LnBackendHost>();
        return services;
    }
}

/// <summary>Refuses the backend on mainnet unless <c>LnBackend:AllowMainnet</c> (the Cashu host's NL-998 rule).</summary>
internal sealed class LnBackendOptionsValidator : IValidateOptions<LnBackendOptions>
{
    private readonly NodeOptions _nodeOptions;

    public LnBackendOptionsValidator(IOptions<NodeOptions> nodeOptions)
    {
        _nodeOptions = nodeOptions.Value;
    }

    public ValidateOptionsResult Validate(string? name, LnBackendOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        failures.AddRange(options.GetValidationErrors());
        if (_nodeOptions.BitcoinNetwork == BitcoinNetwork.Mainnet && !options.AllowMainnet)
            failures.Add($"{LnBackendOptions.SectionName} is refused on mainnet unless "
                       + $"{LnBackendOptions.SectionName}:AllowMainnet.");

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}