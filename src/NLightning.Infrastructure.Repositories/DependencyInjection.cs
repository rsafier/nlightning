using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Repositories;

using Database.Channel;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Memory;

/// <summary>
/// Extension methods for setting up Persistence infrastructure services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds Bitcoin infrastructure services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddRepositoriesInfrastructureServices(this IServiceCollection services)
    {
        // Register UnitOfWork
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Payment repositories: the scope's unit of work instances, so they share its database context and one
        // IUnitOfWork.SaveChangesAsync commits what they staged
        services.AddScoped<IInvoiceDbRepository>(sp => sp.GetRequiredService<IUnitOfWork>().InvoiceDbRepository);
        services.AddScoped<IPaymentDbRepository>(sp => sp.GetRequiredService<IUnitOfWork>().PaymentDbRepository);
        services.AddScoped<IForwardCircuitDbRepository>(sp =>
            sp.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository);

        // The signer loads a channel it has not registered from the database (NL-067)
        services.AddSingleton<IChannelSigningInfoSource, ChannelSigningInfoSource>();

        // Register memory repositories
        services.AddSingleton<IChannelMemoryRepository, ChannelMemoryRepository>();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();

        return services;
    }
}