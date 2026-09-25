using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Extensions;

using Application;
using Contracts.Utilities;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Handlers;
using Infrastructure;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Infrastructure.Serialization;
using Interfaces;
using Services;
using Services.Ipc;

public static class NodeServiceExtensions
{
    /// <summary>
    /// Registers all NLTG application services for dependency injection
    /// </summary>
    public static IHostBuilder ConfigureNltgServices(this IHostBuilder hostBuilder, SecureKeyManager secureKeyManager,
                                                     string configPath)
    {
        return hostBuilder.ConfigureServices((hostContext, services) =>
        {
            // The whole node graph, shared with the Docker integration tests
            services.AddNltgNodeServices(hostContext.Configuration, secureKeyManager);

            // Register the main daemon service
            services.AddHostedService<NltgDaemonService>();

            // IPC server pieces that need the config path
            services.AddSingleton<INamedPipeIpcService>(sp =>
            {
                var ipcAuthenticator = sp.GetRequiredService<IIpcAuthenticator>();
                var ipcFraming = sp.GetRequiredService<IIpcFraming>();
                var logger = sp.GetRequiredService<ILogger<NamedPipeIpcService>>();
                var ipcRequestRouter = sp.GetRequiredService<IIpcRequestRouter>();
                return new NamedPipeIpcService(ipcAuthenticator, configPath, ipcFraming, logger, ipcRequestRouter);
            });
            services.AddSingleton<IIpcAuthenticator>(sp =>
            {
                var cookiePath = NodeUtils.GetCookieFilePath(configPath);
                var logger = sp.GetRequiredService<ILogger<CookieFileAuthenticator>>();
                return new CookieFileAuthenticator(cookiePath, logger);
            });
        });
    }

    /// <summary>
    /// Registers the node's whole service graph: every layer, the fee service, the options, the client command
    /// handlers and the IPC command handlers. It leaves out only the hosted service and the IPC server pieces that
    /// need the config path (<see cref="INamedPipeIpcService"/>, <see cref="IIpcAuthenticator"/>).
    /// </summary>
    /// <remarks>
    /// This is the single composition used by the daemon and by the Docker integration tests
    /// (<c>test/NLightning.Integration.Tests/Docker/Utils/NLightningTestNode.cs</c>), so they cannot drift apart.
    /// Register new layer services in the layer's own <c>DependencyInjection.cs</c>, not here.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The node configuration (sections <c>Node</c>, <c>Bitcoin</c>,
    /// <c>FeeEstimation</c>, <c>Database</c>).</param>
    /// <param name="secureKeyManager">The unlocked node key.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddNltgNodeServices(this IServiceCollection services,
                                                         IConfiguration configuration,
                                                         ISecureKeyManager secureKeyManager)
    {
        // Register configuration and the node key
        services.AddSingleton(configuration);
        services.AddSingleton(secureKeyManager);

        // Register Client Handlers
        services
           .AddScoped<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>,
                OpenChannelClientHandler>();
        services
           .AddScoped<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                    OpenChannelClientSubscriptionResponse>,
                OpenChannelClientSubscriptionHandler>();
        services.AddScoped<IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>,
            ListChannelsClientHandler>();

        // Register IPC routing and command handlers
        services.AddSingleton<IIpcFraming, LengthPrefixedIpcFraming>();
        services.AddSingleton<IIpcRequestRouter, IpcRequestRouter>();
        services.AddSingleton<INodeInfoQueryService, NodeInfoQueryService>();
        services.AddSingleton<IIpcCommandHandler, NodeInfoIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ConnectPeerIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListPeersIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, GetAddressIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, GetWalletBalanceIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, OpenChannelIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, OpenChannelSubscriptionIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListChannelsIpcHandler>();

        // Add HttpClient for FeeService with configuration
        services.AddHttpClient<IFeeService, FeeService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // Add the Application services (also the Domain channel factories and validator)
        services.AddApplicationServices();

        // Add the Infrastructure services (AddBitcoinInfrastructure also registers the signer)
        services.AddBitcoinInfrastructure();
        services.AddInfrastructureServices();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddSerializationInfrastructureServices();

        // Register options with values from configuration
        services.AddOptions<BitcoinOptions>().BindConfiguration("Bitcoin").ValidateOnStart();
        services.AddOptions<FeeEstimationOptions>().BindConfiguration("FeeEstimation").ValidateOnStart();
        services.AddOptions<NodeOptions>()
                .BindConfiguration("Node")
                .PostConfigure(options =>
                 {
                     var configuredAddresses = configuration.GetSection("Node:ListenAddresses").Get<string[]?>();
                     if (configuredAddresses is { Length: > 0 })
                     {
                         options.ListenAddresses = configuredAddresses.ToList();
                     }

                     var networkString = configuration.GetValue<string>("Node:Network");
                     if (!string.IsNullOrWhiteSpace(networkString))
                     {
                         options.BitcoinNetwork = new BitcoinNetwork(networkString);
                     }

                     options.Features.ChainHashes = [options.BitcoinNetwork.ChainHash];
                 })
                .Validate(options =>
                 {
                     // BOLT 9: every advertised feature must have its dependencies set
                     var errors = options.Features.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException("Node", typeof(NodeOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        return services;
    }
}