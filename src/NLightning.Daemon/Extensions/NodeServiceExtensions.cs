using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Extensions;

using Application;
using Application.Accounting;
using Application.Channels.Close;
using Application.Channels.DualFunding;
using Application.Channels.Safety;
using Application.Channels.Splicing;
using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Relay;
using Application.Gossip.Sync;
using Application.Node.PeerStorage;
using Application.Offers.Receive;
using Application.Onchain;
using Application.Onchain.Anchors;
using Application.Onchain.Mempool;
using Application.Onchain.Resolvers.Local;
using Application.Onchain.Resolvers.Remote;
using Application.Onchain.Resolvers.Revoked;
using Application.OnionMessages;
using Application.Payments;
using Application.Payments.Invoices;
using Application.Payments.Routing.Interfaces;
using Application.Payments.Send;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Cashu.PaymentProcessor;
using Contracts.Utilities;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Prices;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
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
using Transport.Ipc;

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

            // Static channel backups (wave rf1 R1): <configPath>/channel.backup and its monitor, started after the
            // daemon service loaded the channels and stopped before it
            services.AddChannelBackupFile(configPath);

            // Expired unpaid BOLT 12 invoice rows pruned on a timer (NL-448)
            services.AddExpiredBolt12InvoicePruning();

            // Cashu plan C1 (NL-992): the CDK payment processor's gRPC server, after the node started (off unless
            // Cashu:PaymentProcessor:Enabled)
            services.AddCashuPaymentProcessorHost();

            // IPC server pieces that need the config path
            services.AddSingleton<INamedPipeIpcService>(sp =>
            {
                var ipcAuthenticator = sp.GetRequiredService<IIpcAuthenticator>();
                var ipcFraming = sp.GetRequiredService<IIpcFraming>();
                var logger = sp.GetRequiredService<ILogger<NamedPipeIpcService>>();
                var ipcRequestRouter = sp.GetRequiredService<IIpcRequestRouter>();
                return new NamedPipeIpcService(ipcAuthenticator, configPath, ipcFraming, logger, ipcRequestRouter)
                {
                    ShutdownTrigger = sp.GetService<NodeShutdownTrigger>(),
                    ConnectionAccessor = sp.GetService<IpcClientConnectionAccessor>()
                };
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
            new ListPaymentsClientHandler(GetPaymentLayerService<IPaymentService>(sp),
                                          sp.GetService<IPaymentDbRepository>(), sp.GetService<IUnitOfWork>()));
        // Cashu plan C0 (NL-991): wait for an invoice to leave Open (ClientCommand 47)
        services.AddScoped<IClientCommandHandler<WaitInvoiceClientRequest, WaitInvoiceClientResponse>>(sp =>
            new WaitInvoiceClientHandler(GetPaymentLayerService<IInvoiceService>(sp),
                                         sp.GetService<IPaymentEventSource>(),
                                         sp.GetRequiredService<TimeProvider>(),
                                         sp.GetService<IpcClientConnectionAccessor>()));
        services.AddScoped<IClientCommandHandler<ListForwardsClientRequest, ListForwardsClientResponse>>(sp =>
            new ListForwardsClientHandler(GetPaymentLayerService<IForwardCircuitDbRepository>(sp),
                                          sp.GetRequiredService<ILogger<ListForwardsClientHandler>>(),
                                          sp.GetService<IChannelMemoryRepository>(),
                                          sp.GetService<IRefusedHtlcCounter>(),
                                          sp.GetService<ITrampolineRelayDbRepository>()!));
        // Pay over caller-supplied routes, never re-planned (NL-1082, ClientCommand 48)
        services.AddScoped<IClientCommandHandler<PayRouteClientRequest, PayRouteClientResponse>>(sp =>
            new PayRouteClientHandler(GetPaymentLayerService<IPaymentService>(sp),
                                      sp.GetService<IBlockchainMonitor>(),
                                      sp.GetService<IChannelMemoryRepository>()));
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

        // BOLT 7 G5-T4: the graph's counts, memory, queues and sync state, with paged listings (ClientCommand 20)
        services.TryAddSingleton<GossipGraphDescriber>(sp =>
            new GossipGraphDescriber(sp.GetRequiredService<IGraphStore>(), sp.GetService<GossipIngress>(),
                                     sp.GetService<GossipSyncManager>()));
        services.AddScoped<IClientCommandHandler<DescribeGraphClientRequest, DescribeGraphClientResponse>,
            DescribeGraphClientHandler>();

        // Register IPC routing and command handlers
        services.AddSingleton<IIpcFraming, IpcFraming>();
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
        services.AddSingleton<IIpcCommandHandler, WaitInvoiceIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListForwardsIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, CloseChannelIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ForceCloseChannelIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, PendingSweepsIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ChainStatusIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListNodesIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, ListGraphChannelsIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, GetRouteIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, DescribeGraphIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, PayRouteIpcHandler>();

        // Static channel backups and restore (wave rf1 R1, ClientCommand 21-23) and the operator commands (wave rf1
        // R4, disconnect = ClientCommand 24); each registers its client and IPC handlers once
        services.AddChannelBackupNodeServices(configuration);
        services.AddOperatorIpcServices();
        // On-chain withdraw (wave m6 W1, ClientCommand 25)
        services.AddWithdrawIpcServices();

        // Cashu plan C1 (NL-992): the CDK payment processor (Cashu:PaymentProcessor, off by default)
        services.AddCashuPaymentProcessor(configuration);
        // BOLT 12 offers (wave B12): createoffer/listoffers/disableoffer (ClientCommand 26-28) and payoffer/
        // fetchinvoice (29-30); the Application registers the offer services themselves (AddApplicationServices)
        services.AddOfferIpcServices();
        services.AddOfferSendIpcServices();
        // Keysend (wave lh1 L3, ClientCommand 31)
        services.AddKeysendIpcServices();
        services.Configure<OfferOptions>(configuration.GetSection(OfferOptions.SectionName));

        // BOLT 4 onion messages (wave M6): the service, rate limiter and outbox cap read this section
        services.Configure<OnionMessageOptions>(configuration.GetSection(OnionMessageOptions.SectionName));

        // BOLT 1 peer storage (wave rf1 R2, NL-010): after the backup services, so a static-channel-backup blob
        // provider registered there would win over the default channel list (TryAdd keeps the first)
        services.Configure<PeerStorageOptions>(configuration.GetSection(PeerStorageOptions.SectionName));
        services.AddPeerStorageServices();
        // listpeerstorage (wave lh1 L4, ClientCommand 32, NL-432)
        services.AddPeerStorageIpcServices();

        // BOLT 2 splicing (wave sp1): splicein/spliceout (ClientCommand 33/34) over the Application's splice service;
        // option_splice and option_quiesce are Optional by default since splicing plan D13 (wave d13)
        services.Configure<SpliceOptions>(configuration.GetSection(SpliceOptions.SectionName));
        services.AddSpliceIpcServices();
        // BOLT 2 dual-funded opens (wave sp1 lane SP1-F, Node:DualFund); option_dual_fund is Optional by default since
        // D13 (a peer's open_channel2 is accepted; openchannel stays v1 unless --dual-fund)
        services.Configure<DualFundingOptions>(configuration.GetSection(DualFundingOptions.SectionName));
        // bumpopen (ClientCommand 38, lane dfrbf): RBF of our unconfirmed dual-funded open (Node:DualFund:AllowRbf)
        services.AddDualFundIpcServices();
        // Liquidity ads (NL-850): liquidityads rates|sellers|purchases (ClientCommand 47)
        services.AddLiquidityAdsIpcServices();
        // Per-channel routing policies (wave sp1 lane SP1-G): setchannelpolicy/getchannelpolicy (ClientCommand 35/36)
        services.AddChannelPolicyIpcServices();
        // The accounting feed (NL-602): listaccountingevents/accountingsnapshot (ClientCommand 41/42); the sealer and
        // snapshot source come from AddApplicationServices and read the Accounting section
        services.Configure<AccountingOptions>(configuration.GetSection(AccountingOptions.SectionName));
        services.AddAccountingIpcServices();
        // The financial books' prices (NL-602 A3-T2, Accounting:Prices): the price file and mempool.space's historical
        // price, asked only by the back-valuation job (PriceValuationService, from AddApplicationServices). Other invalid
        // price options only keep the job off (logged); a ThroughTor that contradicts Node:Tor:Mode refuses the start
        // (NL-868: true with Tor Off, false in TorOnly), never a silent change of route
        services.AddOptions<AccountingPriceOptions>()
                .Bind(configuration.GetSection(AccountingPriceOptions.SectionName))
                .Validate<IOptions<NodeOptions>>((options, nodeOptions) =>
                 {
                     var errors = options.GetTorRoutingErrors(nodeOptions.Value.Tor);
                     if (errors.Count > 0)
                         throw new OptionsValidationException(AccountingPriceOptions.SectionName,
                                                              typeof(AccountingPriceOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // One started fee service shared by every consumer (DustService, the close coordinator, ChannelFactory,
        // FeeUpdateScheduler); a transient typed HttpClient left all but the started instance without an estimate
        services.AddFeeServices();
        // The price sources of the financial books, their HTTP client built like the fee service's (Tor in TorOnly)
        services.AddAccountingPriceSources();

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
        // The members are checked here, not marked required: the binding source generator cannot build a type with
        // required members (NL-338)
        services.AddOptions<BitcoinOptions>()
                .BindConfiguration(BitcoinOptions.SectionName)
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(BitcoinOptions.SectionName, typeof(BitcoinOptions),
                                                              errors);

                     return true;
                 })
                .ValidateOnStart();
        services.AddOptions<FeeEstimationOptions>().BindConfiguration("FeeEstimation").ValidateOnStart();
        services.AddOptions<ChannelCloseOptions>().BindConfiguration(ChannelCloseOptions.SectionName);
        services.Configure<ChannelSafetyOptions>(configuration.GetSection(ChannelSafetyOptions.SectionName));
        services.Configure<OnchainOptions>(configuration.GetSection(OnchainOptions.SectionName));
        // The BOLT 5 resolvers read the same Node:Onchain depths (ReasonableDepth, IrrevocableDepth; FeePolicy for the
        // penalty resolver) as the executor
        services.Configure<LocalCommitResolverOptions>(configuration.GetSection(OnchainOptions.SectionName));
        services.Configure<RemoteResolutionOptions>(configuration.GetSection(OnchainOptions.SectionName));
        services.Configure<RevokedCommitResolverOptions>(configuration.GetSection(OnchainOptions.SectionName));
        // O7-T2: the anchor CPFP knobs (Node:Onchain:Anchors)
        services.Configure<AnchorCpfpOptions>(configuration.GetSection(AnchorCpfpOptions.SectionName));
        // NodeOptions' LightningMoney members (DustLimitAmount, HtlcMinimumAmount, MinimumChannelSize) are not
        // configuration keys: LightningMoney has no settable members, so the binding generator leaves them as they are,
        // and the validation below refuses a file that sets one instead of ignoring it (NL-338)
        services.AddOptions<NodeOptions>()
                .BindConfiguration("Node")
                .PostConfigure(options =>
                 {
                     var configuredAddresses = configuration.GetSection("Node:ListenAddresses").Get<string[]?>();
                     if (configuredAddresses is { Length: > 0 })
                     {
                         options.ListenAddresses = configuredAddresses.ToList();
                     }

                     // Replace the lists rather than let the binder append to them (NL-113)
                     var bootstrapSeeds = configuration.GetSection("Node:Bootstrap:Seeds").Get<string[]?>();
                     if (bootstrapSeeds is not null)
                         options.Bootstrap.Seeds = bootstrapSeeds.ToList();

                     // The obsolete Node:DnsSeedServers key is never used: older templates wrote mainnet seeds into it
                     // on every network. Only a list the operator edited earns a warning (when bootstrap is on)
                     var obsoleteSeeds = configuration.GetSection("Node:DnsSeedServers").Get<string[]?>();
                     options.Bootstrap.ObsoleteSeedsIgnored = BootstrapOptions.IsEditedObsoleteSeedList(obsoleteSeeds);

                     var nameServers = configuration.GetSection("Node:Bootstrap:NameServers").Get<string[]?>();
                     if (nameServers is not null)
                         options.Bootstrap.NameServers = nameServers.ToList();

                     var fallbackNameServers =
                         configuration.GetSection("Node:Bootstrap:FallbackNameServers").Get<string[]?>();
                     if (fallbackNameServers is not null)
                         options.Bootstrap.FallbackNameServers = fallbackNameServers.ToList();

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
                     errors.AddRange(NodeOptions.UnboundMoneyKeys
                                                .Where(key => configuration.GetSection($"Node:{key}").Exists())
                                                .Select(key => $"Node:{key} cannot be set in the configuration "
                                                             + "(NL-338)."));
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

        // Our trampoline relay policy and limits (optional Node:Trampoline section; used only while trampoline_routing
        // is advertised, NL-875 TR3); an invalid section fails the start
        services.AddOptions<TrampolineOptions>()
                .Bind(configuration.GetSection(TrampolineOptions.SectionName))
                .Validate(options =>
                 {
                     var errors = options.GetValidationErrors();
                     if (errors.Count > 0)
                         throw new OptionsValidationException(TrampolineOptions.SectionName,
                                                              typeof(TrampolineOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // BOLT 7 funding output lookups of channel announcements (optional Gossip section; defaults apply, G2-T2)
        services.Configure<FundingOutputLookupOptions>(configuration.GetSection("Gossip"));

        // Where the funding txid of a channel announcement comes from (optional; Gossip:FundingTxIdSource Bitcoind
        // (default) or Esplora with Gossip:EsploraUrl, for pruned nodes; D12 lane Z4)
        services.Configure<FundingTxIdSourceOptions>(configuration.GetSection("Gossip"));

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
                // Gossip:AssumeChannelValid never on mainnet together with HTLCs or public channels (refused at start)
                .Validate<IOptions<NodeOptions>, IOptions<GossipOptions>>((options, nodeOptions, gossipOptions) =>
                 {
                     var network = nodeOptions.Value.BitcoinNetwork;
                     var errors = options.GetAssumeChannelValidErrors(
                         network, nodeOptions.Value.HtlcsEnabled, gossipOptions.Value.ArePublicChannelsAllowed(network));
                     if (errors.Count > 0)
                         throw new OptionsValidationException(GossipGraphOptions.SectionName,
                                                              typeof(GossipGraphOptions), errors);

                     return true;
                 })
                .ValidateOnStart();

        // BOLT 7 gossip queries and sync (G3-T1/G3-T2): answers peers' queries from the graph, syncs the graph from
        // up to Gossip:SyncPeers peers, sends the gossip_timestamp_filters and re-queries what the ingress dropped
        // (NL-353). Gossip:SyncEnabled unset means on everywhere, mainnet included (plan D12, wave d12); the peer services hand it
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
        // everywhere, mainnet included since the NL-417 proof. Own and relayed gossip use the peer's outbox when the peer manager
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