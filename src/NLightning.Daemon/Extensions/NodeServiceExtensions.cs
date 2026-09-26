using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Extensions;

using Application;
using Application.Payments.Send;
using Contracts.Utilities;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
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

        // Invoice and payment handlers (ClientCommand 9-12). They need IInvoiceService/IPaymentService from the
        // Application payment services; until those are registered, resolving a handler throws a ClientException
        // (invalid_operation "not available"), which the IPC command returns as is. Any other resolution failure
        // stays a server_error. Factories (not type registrations) keep ValidateOnBuild (Development hosts) from
        // failing the whole node while the services are missing.
        services.AddScoped<IClientCommandHandler<CreateInvoiceClientRequest, CreateInvoiceClientResponse>>(sp =>
            new CreateInvoiceClientHandler(GetPaymentLayerService<IInvoiceService>(sp),
                                           sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>>(sp =>
            new PayInvoiceClientHandler(GetPaymentLayerService<IPaymentService>(sp)));
        services.AddScoped<IClientCommandHandler<ListInvoicesClientRequest, ListInvoicesClientResponse>>(sp =>
            new ListInvoicesClientHandler(GetPaymentLayerService<IInvoiceService>(sp),
                                          sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>>(sp =>
            new ListPaymentsClientHandler(GetPaymentLayerService<IPaymentService>(sp)));
        services.TryAddSingleton(TimeProvider.System);

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
        services.AddSingleton<IIpcCommandHandler, CreateInvoiceIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, PayInvoiceIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListInvoicesIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListPaymentsIpcHandler>();

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
                     // BOLT 9: every advertised feature must have its dependencies set; BOLT 7 routing policy and
                     // reconnect delays must be sane (e.g. cltv_expiry_delta >= 34)
                     var errors = options.Features.GetValidationErrors().Concat(options.GetValidationErrors()).ToList();
                     if (errors.Count > 0)
                         throw new OptionsValidationException("Node", typeof(NodeOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // Fee limit of our outgoing payments (optional section; PaymentSendOptions has defaults)
        services.Configure<PaymentSendOptions>(configuration.GetSection("Node:Payments"));

        // Node:Routing is bound as part of NodeOptions (and validated with it); expose the same instance on its own
        services.AddSingleton<IOptions<RoutingOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<NodeOptions>>().Value.Routing));

        return services;
    }

    /// <summary>
    /// Resolves a payment-layer service for a client handler factory. A service that is not registered (a node built
    /// without the payment services) is a <see cref="ClientException"/> with <see cref="ErrorCodes.InvalidOperation"/>
    /// ("not available"); a failure while building a registered one propagates unchanged, so it is reported as a
    /// server error instead of being hidden.
    /// </summary>
    internal static T GetPaymentLayerService<T>(IServiceProvider serviceProvider) where T : class =>
        serviceProvider.GetService<T>()
     ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                  $"Not available on this node: {typeof(T).Name} is not registered.");
}