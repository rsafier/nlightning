using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure;

using Crypto.Hashes;
using Domain.Crypto.Hashes;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Node.Factories;
using Protocol.Factories;
using Protocol.Onion;
using Protocol.Services;
using Transport.Factories;
using Transport.Interfaces;
using Transport.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Singleton services (one instance throughout the application)
        services.AddSingleton<IChannelIdFactory, ChannelIdFactory>();
        services.AddSingleton<IMessageServiceFactory, MessageServiceFactory>();
        services.AddSingleton<IOnionReplayCache, OnionReplayCache>();
        services.AddSingleton<IPeerServiceFactory, PeerServiceFactory>();
        services.AddSingleton<ITcpService, TcpService>();
        services.AddSingleton<ISha256, Sha256>();
        services.AddSingleton<ITransportServiceFactory, TransportServiceFactory>();

        // TryAdd: AddSerializationInfrastructureServices also registers it so that it can be composed on its own
        services.TryAddSingleton<ITlvConverterFactory, TlvConverterFactory>();

        // Transient services (new instance each time requested)
        services.AddTransient<IPingPongService, PingPongService>();

        return services;
    }
}