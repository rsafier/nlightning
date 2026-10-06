using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Repositories;

using Database.Channel;
using Database.Payment;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Crypto.Hashes;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Memory;
using Persistence.Contexts;

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
        // The accounting feed's gate (NL-619): one per process, held by the backfill when the cutover fails
        services.TryAddSingleton<AccountingFeedGate>();

        // LND's dense invoice/payment indexes (NL-1165): one allocator per node, shared by every unit of work
        services.TryAddSingleton<LndIndexAllocator>();

        // Register UnitOfWork. Its optional NodeOptions read is the dust-limit backfill of NL-290: a commitment
        // snapshot stored without a MaxDustHtlcExposureMsat (the option is newer) runs under the configured one
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<NLightningDbContext>(),
                                                             sp.GetRequiredService<ILogger<UnitOfWork>>(),
                                                             sp.GetRequiredService<ISha256>(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                             sp.GetService<TimeProvider>(),
                                                             sp.GetService<IOptions<NodeOptions>>()
                                                                 ?.Value.MaxDustHtlcExposureMsat,
                                                             sp.GetService<AccountingFeedGate>(),
                                                             sp.GetService<LndIndexAllocator>()));

        // Payment repositories: the scope's unit of work instances, so they share its database context and one
        // IUnitOfWork.SaveChangesAsync commits what they staged
        services.AddScoped<IInvoiceDbRepository>(sp => sp.GetRequiredService<IUnitOfWork>().InvoiceDbRepository);
        services.AddScoped<IPaymentDbRepository>(sp => sp.GetRequiredService<IUnitOfWork>().PaymentDbRepository);
        services.AddScoped<IPaymentPartDbRepository>(sp =>
            sp.GetRequiredService<IUnitOfWork>().PaymentPartDbRepository);
        services.AddScoped<IForwardCircuitDbRepository>(sp =>
            sp.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository);
        services.AddScoped<ITrampolineRelayDbRepository>(sp =>
            sp.GetRequiredService<IUnitOfWork>().TrampolineRelayDbRepository);

        // The signer loads a channel it has not registered from the database (NL-067)
        services.AddSingleton<IChannelSigningInfoSource, ChannelSigningInfoSource>();

        // Register memory repositories
        services.AddSingleton<IChannelMemoryRepository, ChannelMemoryRepository>();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();

        return services;
    }
}