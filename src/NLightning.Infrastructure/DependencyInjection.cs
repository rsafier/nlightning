using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure;

using Crypto.Hashes;
using Domain.Crypto.Hashes;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Node.Factories;
using Protocol.Dns;
using Protocol.Factories;
using Protocol.Onion;
using Protocol.Services;
using Transport.Factories;
using Transport.Interfaces;
using Transport.Services;
using Transport.Tor;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Singleton services (one instance throughout the application)
        services.AddSingleton<IChannelIdFactory, ChannelIdFactory>();
        services.AddSingleton<IMessageServiceFactory, MessageServiceFactory>();
        services.AddSingleton<IPeerServiceFactory, PeerServiceFactory>();
        services.AddSingleton<ITorSocksDialer, TorSocksDialer>();
        services.AddSingleton<ITorOnionService, TorOnionService>();
        services.AddSingleton<IAnnouncedAddressSource>(sp => sp.GetRequiredService<ITorOnionService>());
        services.AddSingleton<ITcpService, TcpService>();
        // Shared by singletons (ChannelFactory) and scoped users alike: per-thread state, so concurrent callers never
        // mix their data (NL-247)
        services.AddSingleton<ISha256, ThreadLocalSha256>();
        services.AddSingleton<ITransportServiceFactory, TransportServiceFactory>();

        // TryAdd: AddSerializationInfrastructureServices also registers it so that it can be composed on its own

        // The onion replay set (NL-078): persisted, owned by the incoming HTLC and pruned by its cltv_expiry. Every
        // onion is processed through a scoped IUnitOfWork (AddRepositoriesInfrastructureServices)
        services.AddPersistentOnionReplayStore();

        // BOLT 10 bootstrap's raw DNS queries (NL-113); the resolver is built on the first query, never here
        services.TryAddSingleton<IDnsRecordLookup, DnsClientRecordLookup>();
        // The public resolvers asked when the system resolvers give a seed no candidate (D-B10-7)
        services.TryAddSingleton<IFallbackDnsRecordLookup, FallbackDnsRecordLookup>();
        // The resolver a Tor-only node asks the seeds through (NL-571), over the SOCKS5 port
        services.TryAddSingleton<ITorDnsRecordLookup, TorSocksDnsRecordLookup>();

        // Transient services (new instance each time requested)
        services.AddTransient<IPingPongService, PingPongService>();

        return services;
    }
}