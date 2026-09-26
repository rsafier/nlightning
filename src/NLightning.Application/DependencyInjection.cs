using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application;

using Channels.Close;
using Channels.Fees;
using Channels.Handlers;
using Channels.Handlers.Interfaces;
using Channels.Interfaces;
using Channels.Managers;
using Channels.Reestablish;
using Channels.Safety;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Validators;
using Domain.Crypto.Hashes;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Gossip;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Node.Managers;
using Onchain;
using Payments;
using Payments.Send;
using Payments.Switch;
using Protocol.Factories;

/// <summary>
/// Extension methods for setting up application services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds application layer services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    /// <remarks>
    /// Also registers the Domain channel and transaction factories and the open-channel validator, because Domain has
    /// no DI of its own. They need <see cref="IFeeService"/> (registered by the host) and <see cref="ILightningSigner"/>
    /// (registered by <c>AddBitcoinInfrastructure</c>).
    /// </remarks>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Domain services that have no DI of their own
        services.AddSingleton<IChannelOpenValidator>(sp =>
        {
            var nodeOptions = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            return new ChannelOpenValidator(nodeOptions);
        });
        services.AddSingleton<IChannelFactory>(sp =>
        {
            var channelIdFactory = sp.GetRequiredService<IChannelIdFactory>();
            var channelOpenValidator = sp.GetRequiredService<IChannelOpenValidator>();
            var feeService = sp.GetRequiredService<IFeeService>();
            var lightningSigner = sp.GetRequiredService<ILightningSigner>();
            var nodeOptions = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            var sha256 = sp.GetRequiredService<ISha256>();
            return new ChannelFactory(channelIdFactory, channelOpenValidator, feeService, lightningSigner, nodeOptions,
                                      sha256);
        });
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton<IFundingTransactionModelFactory, FundingTransactionModelFactory>();

        // Singleton services (one instance throughout the application)
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(sp =>
        {
            var blockchainMonitor = sp.GetRequiredService<IBlockchainMonitor>();
            var channelLockProvider = sp.GetRequiredService<IChannelLockProvider>();
            var channelMemoryRepository = sp.GetRequiredService<IChannelMemoryRepository>();
            var lightningSigner = sp.GetRequiredService<ILightningSigner>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new ChannelManager(blockchainMonitor, channelLockProvider, channelMemoryRepository,
                                      loggerFactory.CreateLogger<ChannelManager>(), lightningSigner, sp);
        });
        services.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddSingleton<IChannelMessagePublisher>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddReestablishServices();
        services.AddChannelOperationsServices();
        services.AddChannelCloseServices();
        services.AddGossipServices();
        services.AddPaymentsServices();
        services.AddHtlcSwitchServices();
        services.AddPaymentSendServices();
        services.AddChannelSafetyServices();
        services.AddOnchainServices();
        services.AddSingleton<IPeerManager, PeerManager>();

        // Automatically register all channel message handlers
        services.AddChannelMessageHandlers();

        // Add scoped services
        services.AddScoped<FundingConfirmedMessageHandler>();

        // Last: decorates the IHtlcSwitch registered above with the dust exposure check (N9-T3)
        services.AddChannelFeeServices();

        return services;
    }

    /// <summary>
    /// Registers all classes that implement IChannelMessageHandler&lt;T&gt; from the current assembly
    /// </summary>
    private static void AddChannelMessageHandlers(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Find all types that implement IChannelMessageHandler<>
        var handlerTypes = assembly
                          .GetTypes()
                          .Where(type => type is { IsClass: true, IsAbstract: false })
                          .Where(type => type.GetInterfaces()
                                             .Any(i => i.IsGenericType
                                                    && i.GetGenericTypeDefinition() ==
                                                       typeof(IChannelMessageHandler<>)))
                          .ToArray();

        foreach (var handlerType in handlerTypes)
        {
            // Get the interface this handler implements
            var handlerInterface = handlerType
                                  .GetInterfaces()
                                  .First(i => i.IsGenericType
                                           && i.GetGenericTypeDefinition() == typeof(IChannelMessageHandler<>));

            services.AddScoped(handlerInterface, handlerType);
        }
    }
}