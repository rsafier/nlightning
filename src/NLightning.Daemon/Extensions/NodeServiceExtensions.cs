using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Extensions;

using Application;
using Application.Channels.Close;
using Application.Channels.Safety;
using Application.Gossip.Graph;
using Application.Gossip.Relay;
using Application.Gossip.Sync;
using Application.Onchain;
using Application.Onchain.Mempool;
using Application.Onchain.Resolvers.Local;
using Application.Onchain.Resolvers.Remote;
using Application.Onchain.Resolvers.Revoked;
using Application.Payments.Invoices;
using Application.Payments.Routing.Interfaces;
using Application.Payments.Send;
using Application.Payments.Switch;
using Contracts.Utilities;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
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
using Infrastructure.Bitcoin.Gossip;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Wallet.Interfaces;
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

            // The gossip graph (ingress + pruner, BOLT 7 G2-T4/G2-T5) is registered first: it starts before the daemon
            // service (the pruner follows the chain monitor's first block) and stops after it (the graph is written
            // once the peers and the monitor stopped)
            services.AddHostedService<GossipGraphHostedService>();

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
            new PayInvoiceClientHandler(GetPaymentLayerService<IPaymentService>(sp),
                                        sp.GetService<IBlockchainMonitor>()));
        services.AddScoped<IClientCommandHandler<ListInvoicesClientRequest, ListInvoicesClientResponse>>(sp =>
            new ListInvoicesClientHandler(GetPaymentLayerService<IInvoiceService>(sp),
                                          sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>>(sp =>
            new ListPaymentsClientHandler(GetPaymentLayerService<IPaymentService>(sp)));
        services.TryAddSingleton(TimeProvider.System);

        // Cooperative close (ClientCommand 13, BOLT2 plan N10); IChannelCloseService comes from AddApplicationServices
        services.AddScoped<IClientCommandHandler<CloseChannelClientRequest, CloseChannelClientResponse>,
            CloseChannelClientHandler>();

        // BOLT 5 (plan O2-T5, O3-T6): force close (ClientCommand 14) and the on-chain resolution list (15)
        services.AddScoped<IClientCommandHandler<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>,
            ForceCloseChannelClientHandler>();
        services.AddScoped<IClientCommandHandler<PendingSweepsClientRequest, PendingSweepsClientResponse>,
            PendingSweepsClientHandler>();

        // NL-216: whether chain processing is halted and what is refused meanwhile (ClientCommand 16)
        services.AddScoped<IClientCommandHandler<ChainStatusClientRequest, ChainStatusClientResponse>,
            ChainStatusClientHandler>();

        // BOLT 7 G2-T6: the gossip graph's nodes (ClientCommand 17) and channels (18)
        services.AddScoped<IClientCommandHandler<ListNodesClientRequest, ListNodesClientResponse>,
            ListNodesClientHandler>();
        services.AddScoped<IClientCommandHandler<ListGraphChannelsClientRequest, ListGraphChannelsClientResponse>,
            ListGraphChannelsClientHandler>();

        // BOLT 7 G4-T4: the route a payment would take (ClientCommand 19); the payment service answers it
        services.AddScoped<IClientCommandHandler<GetRouteClientRequest, GetRouteClientResponse>>(sp =>
            new GetRouteClientHandler(GetPaymentLayerService<IRouteQueryService>(sp)));

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
        services.AddSingleton<IIpcCommandHandler, CloseChannelIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ForceCloseChannelIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, PendingSweepsIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ChainStatusIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListNodesIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListGraphChannelsIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, GetRouteIpcHandler>();

        // One started fee service shared by every consumer (DustService, the close coordinator, ChannelFactory,
        // FeeUpdateScheduler); a transient typed HttpClient left all but the started instance without an estimate
        services.AddFeeServices();

        // The node's services take ILogger<T>; AddHttpClient used to register logging implicitly (hosts add providers)
        services.AddLogging();

        // Add the Application services (also the Domain channel factories and validator)
        services.AddApplicationServices();

        // BOLT 5 O8 (NL-098): preimages and revoked commitments seen in the mempool; the hosted service starts it
        services.AddOnchainMempoolServices();

        // Add the Infrastructure services (AddBitcoinInfrastructure also registers the signer)
        services.AddBitcoinInfrastructure();
        services.AddInfrastructureServices();

        // Prunes the persistent onion replay set on every block (NL-327); the hosted service starts and stops it
        services.AddOnionReplayBlockPruner();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddSerializationInfrastructureServices();

        // BOLT 5 on-chain building blocks (output mapper, sweep and penalty builders); they need the commitment model
        // factory from AddApplicationServices and the commitment builder from AddBitcoinInfrastructure
        services.AddOnchainBitcoinServices();

        // Register options with values from configuration
        services.AddOptions<BitcoinOptions>().BindConfiguration("Bitcoin").ValidateOnStart();
        services.AddOptions<FeeEstimationOptions>().BindConfiguration("FeeEstimation").ValidateOnStart();
        services.AddOptions<ChannelCloseOptions>().BindConfiguration(ChannelCloseOptions.SectionName);
        services.Configure<ChannelSafetyOptions>(configuration.GetSection(ChannelSafetyOptions.SectionName));
        services.Configure<OnchainOptions>(configuration.GetSection(OnchainOptions.SectionName));
        // The BOLT 5 resolvers read the same Node:Onchain depths (ReasonableDepth, IrrevocableDepth; FeePolicy for the
        // penalty resolver) as the executor
        services.Configure<LocalCommitResolverOptions>(configuration.GetSection(OnchainOptions.SectionName));
        services.Configure<RemoteResolutionOptions>(configuration.GetSection(OnchainOptions.SectionName));
        services.Configure<RevokedCommitResolverOptions>(configuration.GetSection(OnchainOptions.SectionName));
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
                         // Fail fast on an unknown network; a configured custom signet (Mutinynet) resolves to signet
                         options.CustomSignet?.Register();
                         options.BitcoinNetwork = BitcoinNetwork.Resolve(networkString);
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

        // Fee, part and retry limits of our outgoing payments (optional section; PaymentSendOptions has defaults; a
        // payinvoice call may set its own fee and part limits, NL-270)
        services.Configure<PaymentSendOptions>(configuration.GetSection("Node:Payments"));

        // When our invoices carry route hints (optional; Auto leaves them out once an announced channel can receive,
        // BOLT 7 G4)
        services.Configure<InvoiceOptions>(configuration.GetSection(InvoiceOptions.SectionName));

        // How long the final hop holds an incomplete basic_mpp HTLC set before mpp_timeout (optional; default 60 s)
        services.Configure<HtlcSwitchOptions>(configuration.GetSection("Node:Switch"));

        // BOLT 7 funding output lookups of channel announcements (optional Gossip section; defaults apply, G2-T2)
        services.Configure<FundingOutputLookupOptions>(configuration.GetSection("Gossip"));

        // BOLT 7 public channels (optional Gossip section: AcceptPublicChannels, AllowPublicChannelsOnMainnet,
        // AnnouncementDepth, AnnounceAddresses, OwnGossipFlushInterval, NodeAnnouncementRefreshInterval; G1-T1..T7,
        // NL-341); a bad announced address fails the start
        services.AddOptions<GossipOptions>()
                .Bind(configuration.GetSection(GossipOptions.SectionName))
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(GossipOptions.SectionName, typeof(GossipOptions),
                                                              errors);

                     return true;
                 })
                .ValidateOnStart();

        // BOLT 7 graph: ingress, store and write-behind (G2-T4). Gossip:Enabled unset means on everywhere but
        // mainnet (plan D12); the peer services hand graph gossip to the ingress and ask gossip peers for their graph
        services.AddGossipGraphServices();
        services.AddOptions<GossipGraphOptions>()
                .BindConfiguration(GossipGraphOptions.SectionName)
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(GossipGraphOptions.SectionName,
                                                              typeof(GossipGraphOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // BOLT 7 gossip queries and sync (G3-T1/G3-T2): answers peers' queries from the graph, syncs the graph from
        // up to Gossip:SyncPeers peers, sends the gossip_timestamp_filters and re-queries what the ingress dropped
        // (NL-353). Gossip:SyncEnabled unset means on everywhere but mainnet (plan D12); the peer services hand it
        // messages 261-265 and call it after init
        services.AddGossipSyncServices();
        services.AddOptions<GossipSyncOptions>()
                .BindConfiguration(GossipSyncOptions.SectionName)
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(GossipSyncOptions.SectionName,
                                                              typeof(GossipSyncOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // BOLT 7 relay of other nodes' gossip (G3-T3): per-peer filters, staggered flushes, origin suppression (the
        // peer services' ingress records who sent what), backlog on a new filter. Gossip:RelayEnabled unset means on
        // everywhere but mainnet (plan D12). Own and relayed gossip use the peer's outbox when the peer manager
        // offers one (IPeerGossipOutbox, NL-351)
        services.AddGossipRelayOriginTracking();
        services.AddOptions<GossipRelayOptions>()
                .BindConfiguration(GossipRelayOptions.SectionName)
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(GossipRelayOptions.SectionName,
                                                              typeof(GossipRelayOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

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