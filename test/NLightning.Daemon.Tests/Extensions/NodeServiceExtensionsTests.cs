using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Channels.Fees;
using Application.Channels.Interfaces;
using Application.Channels.Reestablish;
using Application.Channels.Safety.Interfaces;
using Application.Gossip.Announcements;
using Application.Gossip.Announcements.Interfaces;
using Application.Gossip.Graph;
using Application.Gossip.Interfaces;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Services;
using Application.Gossip.Sync;
using Application.Gossip.Sync.Interfaces;
using Application.Node.Managers;
using Application.OnionMessages;
using Application.Payments.Invoices;
using Application.Payments.Routing;
using Application.Payments.Routing.Interfaces;
using Application.Payments.Send;
using Application.Payments.Send.Interfaces;
using Application.Payments.Switch;
using Daemon.Extensions;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Reestablish;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Gossip;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class NodeServiceExtensionsTests
{
    [Fact]
    public void Given_NodeServices_When_Composed_Then_EveryNodeServiceResolves()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);

        // BitcoinChainService connects to bitcoind in its constructor; keep the test hermetic
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act / Assert: the services the Docker tests and the daemon both need come from the shared composition
        Assert.NotNull(provider.GetRequiredService<IChannelOpenValidator>());
        Assert.NotNull(provider.GetRequiredService<IChannelFactory>());
        Assert.NotNull(provider.GetRequiredService<ICommitmentTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<IFundingTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<ILightningSigner>());
        Assert.NotNull(provider.GetRequiredService<IFeeService>());
        Assert.Same(provider.GetRequiredService<IFeeService>(), provider.GetRequiredService<IFeeService>());
        Assert.NotNull(provider.GetRequiredService<IChannelManager>());
        Assert.NotNull(provider.GetRequiredService<IPeerManager>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                 OpenChannelClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                 OpenChannelClientSubscriptionResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<ListChannelsClientRequest,
                                 ListChannelsClientResponse>>());
        // W3: the N9 safety services, the update_fee scheduler and the dust exposure decorator on the switch
        Assert.NotNull(provider.GetRequiredService<IChannelFailureService>());
        Assert.NotNull(provider.GetRequiredService<IHtlcExpiryMonitor>());
        Assert.NotNull(provider.GetRequiredService<IFeeUpdateScheduler>());
        Assert.IsType<DustExposureHtlcSwitch>(provider.GetRequiredService<IHtlcSwitch>());
        Assert.Same(provider.GetRequiredService<OnionReplayBlockPruner>(),
                    provider.GetRequiredService<OnionReplayBlockPruner>());
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();
        Assert.Contains(ClientCommand.ListChannels, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());

        // BOLT 7 G2-T5/G2-T6: the graph pruner and the graph listings
        Assert.Same(provider.GetRequiredService<GraphPruner>(), provider.GetRequiredService<GraphPruner>());
        // Our own 256/258/257 reach the graph, never a no-op default sink (whatever registered one first)
        Assert.Same(provider.GetRequiredService<GossipIngress>(), provider.GetRequiredService<IOwnGossipSink>());
        Assert.Contains(ClientCommand.ListNodes, commands);
        Assert.Contains(ClientCommand.ListGraphChannels, commands);
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<ListNodesClientRequest,
                                 ListNodesClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<ListGraphChannelsClientRequest,
                                 ListGraphChannelsClientResponse>>());

        // BOLT 7 G4-T4: getroute answered by the payment service; the graph routing pieces are shared singletons
        Assert.Contains(ClientCommand.GetRoute, commands);
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<GetRouteClientRequest,
                                 GetRouteClientResponse>>());
        Assert.Same(provider.GetRequiredService<PaymentService>(), provider.GetRequiredService<IRouteQueryService>());

        // BOLT 7 G5-T4: describegraph reads the node's own store, ingress and sync manager (it reports their parts)
        Assert.Contains(ClientCommand.DescribeGraph, commands);
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<DescribeGraphClientRequest,
                                 DescribeGraphClientResponse>>());
        var description = provider.GetRequiredService<GossipGraphDescriber>().Describe();
        Assert.NotNull(description.Ingress);
        Assert.NotNull(description.Sync);
        Assert.True(provider.GetRequiredService<GraphPathSource>().IsAvailable);
        Assert.Same(provider.GetRequiredService<MissionControl>(),
                    provider.GetRequiredService<GraphPathSource>().MissionControl);
        // G3-T5: a payment's gossip refresh goes to the sync manager, not the no-op default
        Assert.IsType<GossipSyncScidRefresher>(provider.GetRequiredService<IGossipScidRefresher>());
        // NL-351: own and relayed gossip goes through the peer manager's per-connection outbox
        Assert.Same(provider.GetRequiredService<IPeerManager>(), provider.GetRequiredService<IPeerGossipOutbox>());
        Assert.True(Assert.IsType<PeerGossipSender>(provider.GetRequiredService<IGossipPeerSender>()).UsesOutbox);
        // NL-373: Gossip:MaxMemoryMb (1 GiB by default) is one shared budget, checked by the ingress (a private
        // constructor argument, read by reflection since Daemon.Tests has no internals access) and read by describegraph
        var budget = provider.GetRequiredService<GossipMemoryBudget>();
        Assert.Equal(1_024L << 20, budget.BudgetBytes);
        Assert.Same(budget, typeof(GossipIngress)
                           .GetField("_memoryBudget", System.Reflection.BindingFlags.Instance
                                                    | System.Reflection.BindingFlags.NonPublic)!
                           .GetValue(provider.GetRequiredService<GossipIngress>()));
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_PeerManagerGetsTheChannelUpdateService()
    {
        // Arrange: AddApplicationServices registers the W1-E gossip services, so the daemon needs nothing else
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var peerManager = provider.GetRequiredService<IPeerManager>();
        var channelUpdateService = provider.GetRequiredService<IChannelUpdateService>();

        // Assert
        Assert.NotNull(peerManager);
        Assert.IsType<ChannelUpdateService>(channelUpdateService);
        Assert.Single(services, d => d.ServiceType == typeof(IChannelUpdateService));
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_InvoiceServiceAndScopedPaymentRepositoriesResolve()
    {
        // Arrange: AddApplicationServices registers the W1-B payment core; the repositories come from the unit of work
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var invoiceService = provider.GetRequiredService<IInvoiceService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Assert
        Assert.IsType<InvoiceService>(invoiceService);
        Assert.Same(unitOfWork.InvoiceDbRepository, scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>());
        Assert.Same(unitOfWork.PaymentDbRepository, scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>());
        Assert.Same(unitOfWork.ForwardCircuitDbRepository,
                    scope.ServiceProvider.GetRequiredService<IForwardCircuitDbRepository>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<CreateInvoiceClientRequest,
                                 CreateInvoiceClientResponse>>());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_HtlcSwitchSendPathAndReestablishAreWired()
    {
        // Arrange: AddApplicationServices registers the W2-A reestablish, W2-B switch and W2-C send services
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Payments:MaxFeeFloorMsat", "7000")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // Act
        var htlcSwitch = provider.GetRequiredService<IHtlcSwitch>();
        var paymentService = provider.GetRequiredService<IPaymentService>();
        var outcomeHandler = provider.GetRequiredService<IPaymentOutcomeHandler>();
        var localHandlers = provider.GetServices<ILocalPaymentHtlcHandler>().ToList();
        var probe = provider.GetRequiredService<IPeerLivenessProbe>();
        var tracker = provider.GetRequiredService<IReestablishTracker>();
        var sendOptions = provider.GetRequiredService<IOptions<PaymentSendOptions>>().Value;

        // Assert
        // The switch is decorated with the dust exposure check (AddChannelFeeServices, N9-T3)
        Assert.IsType<HtlcSwitch>(Assert.IsType<DustExposureHtlcSwitch>(htlcSwitch).Inner);
        Assert.IsType<PaymentService>(paymentService);
        Assert.Same(paymentService, outcomeHandler);
        Assert.IsType<PaymentOutcomeSwitchHandler>(Assert.Single(localHandlers));
        Assert.IsType<LinkUpReplayingPeerLivenessProbe>(probe);
        Assert.IsType<ReestablishTracker>(tracker);
        Assert.Equal(7_000UL, sendOptions.MaxFeeFloorMsat);
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();
        Assert.Contains(ClientCommand.PayInvoice, commands);
        Assert.Contains(ClientCommand.ListPayments, commands);
    }

    [Fact]
    public void Given_NodeServicesWithPaymentServices_When_Composed_Then_InvoiceAndPaymentCommandsResolve()
    {
        // Arrange: mocks stand in for the Application invoice and payment services
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddSingleton(new Mock<IInvoiceService>().Object);
        services.AddSingleton(new Mock<IPaymentService>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert
        Assert.Contains(ClientCommand.CreateInvoice, commands);
        Assert.Contains(ClientCommand.PayInvoice, commands);
        Assert.Contains(ClientCommand.ListInvoices, commands);
        Assert.Contains(ClientCommand.ListPayments, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<CreateInvoiceClientRequest,
                                 CreateInvoiceClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<PayInvoiceClientRequest,
                                 PayInvoiceClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<ListInvoicesClientRequest,
                                 ListInvoicesClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<ListPaymentsClientRequest,
                                 ListPaymentsClientResponse>>());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheWaveRf1CommandsAndPeerStorageAreWiredOnce()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert: backups (R1), disconnect (R4) and the peer storage service (R2) come from the shared composition
        Assert.Contains(ClientCommand.ExportChanBackup, commands);
        Assert.Contains(ClientCommand.VerifyChanBackup, commands);
        Assert.Contains(ClientCommand.RestoreChanBackup, commands);
        Assert.Contains(ClientCommand.DisconnectPeer, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.NotNull(provider.GetRequiredService<IIpcRequestRouter>());
        Assert.NotNull(provider.GetRequiredService<IPeerStorageService>());
        Assert.Equal(FeatureSupport.Optional,
                     provider.GetRequiredService<IOptions<NodeOptions>>().Value.Features.OptionProvideStorage);
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheWaveM6OnionMessagesAndWithdrawAreWired()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("OnionMessages:MaxOutboxPerPeer", "7"),
                                                        ("OnionMessages:PeerMessagesPerSecond", "3"),
                                                        ("OnionMessages:PeerBurstMessages", "4")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var peerManager = provider.GetRequiredService<IPeerManager>();
        var outbox = provider.GetRequiredService<IPeerOnionMessageOutbox>();
        var limiter = provider.GetRequiredService<IOnionMessageRateLimiter>();
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert: the outbox is the peer manager, its cap and the limiter follow the OnionMessages section
        Assert.Same(peerManager, outbox);
        Assert.Equal(7, Assert.IsType<PeerManager>(peerManager).MaxOutboxOnionMessagesPerPeer);
        var limits = Assert.IsType<OnionMessageRateLimiter>(limiter).Limits;
        Assert.Equal(3, limits.PeerMessagesPerSecond);
        Assert.Equal(4, limits.PeerBurstMessages);
        Assert.NotNull(provider.GetRequiredService<IOnionMessageService>());
        Assert.NotNull(provider.GetRequiredService<IWalletSpendService>());
        Assert.Single(commands, c => c == ClientCommand.Withdraw);
    }

    [Fact]
    public void Given_NodeServicesWithoutPaymentServices_When_BuiltWithValidateOnBuild_Then_TheGraphStillBuilds()
    {
        // Arrange: a Development host validates every registration at build
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);

        // Act
        var exception = Record.Exception(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
        });

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_RoutingConfigured_When_OptionsResolved_Then_EveryValueIsBoundAndShared()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Routing:FeeBaseMsat", "2000"),
                                                        ("Node:Routing:FeeProportionalMillionths", "500"),
                                                        ("Node:Routing:CltvExpiryDelta", "80"),
                                                        ("Node:Routing:MaxCltvExpiryDistance", "1008"),
                                                        ("Node:Routing:ExpiryTooSoonBlocks", "20"),
                                                        ("Node:Routing:InvoiceMinFinalCltvExpiry", "36"),
                                                        ("Node:Routing:InvoiceExpirySeconds", "600"),
                                                        ("Node:Routing:HtlcMinimumMsat", "1"),
                                                        ("Node:Routing:HtlcMaximumMsat", "990000000")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var routing = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

        // Assert
        Assert.Same(provider.GetRequiredService<IOptions<NodeOptions>>().Value.Routing, routing);
        Assert.Equal(2_000U, routing.FeeBaseMsat);
        Assert.Equal(500U, routing.FeeProportionalMillionths);
        Assert.Equal((ushort)80, routing.CltvExpiryDelta);
        Assert.Equal(1_008U, routing.MaxCltvExpiryDistance);
        Assert.Equal((ushort)20, routing.ExpiryTooSoonBlocks);
        Assert.Equal((ushort)36, routing.InvoiceMinFinalCltvExpiry);
        Assert.Equal(600U, routing.InvoiceExpirySeconds);
        Assert.Equal(1UL, routing.HtlcMinimumMsat);
        Assert.Equal(990_000_000UL, routing.HtlcMaximumMsat);
    }

    [Fact]
    public void Given_InvalidRouting_When_RoutingOptionsResolved_Then_ValidationFails()
    {
        // Arrange: IOptions<RoutingOptions> is the validated NodeOptions.Routing, never an unchecked copy
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Routing:InvoiceExpirySeconds", "0")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        var exception = Assert.Throws<OptionsValidationException>(() =>
                                                                       provider
                                                                          .GetRequiredService<
                                                                               IOptions<RoutingOptions>>()
                                                                          .Value);
        Assert.Contains(exception.Failures, f => f.Contains("InvoiceExpirySeconds"));
    }

    [Theory]
    [InlineData("mainnet", null, null, "HTLCs are enabled")] // HTLCs on by default
    [InlineData("mainnet", "true", null, "HTLCs are enabled")]
    [InlineData("mainnet", "false", "true", "AllowPublicChannelsOnMainnet")]
    [InlineData("mainnet", "false", null, null)] // the gossip probe's settings
    [InlineData("regtest", null, "true", null)] // any other network: allowed
    [InlineData("signet", null, null, null)]
    public void Given_AssumeChannelValid_When_GossipGraphOptionsResolved_Then_MainnetRefusesItWithHtlcsOrPublicChannels(
        string network, string? enableHtlcs, string? allowPublicOnMainnet, string? expectedFailure)
    {
        // Arrange: ValidateOnStart runs this check when the daemon's host starts
        var extra = new List<(string, string)> { ("Node:Network", network), ("Gossip:AssumeChannelValid", "true") };
        if (enableHtlcs is not null)
            extra.Add(("Node:EnableHtlcs", enableHtlcs));
        if (allowPublicOnMainnet is not null)
            extra.Add(("Gossip:AllowPublicChannelsOnMainnet", allowPublicOnMainnet));
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(extra.ToArray()), new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        GossipGraphOptions Resolve() => provider.GetRequiredService<IOptions<GossipGraphOptions>>().Value;

        // Assert
        if (expectedFailure is null)
        {
            Assert.True(Resolve().AssumeChannelValid);
            return;
        }

        var exception = Assert.Throws<OptionsValidationException>(Resolve);
        Assert.Contains(exception.Failures, f => f.Contains(expectedFailure) && f.Contains("AssumeChannelValid"));
    }

    [Fact]
    public void Given_AssumeChannelValidUnset_When_GossipGraphOptionsResolvedOnMainnet_Then_ItIsOffAndValid()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Network", "mainnet")), new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<GossipGraphOptions>>().Value;

        // Assert
        Assert.False(options.AssumeChannelValid);
    }

    [Theory]
    [InlineData("regtest", null, true)]
    [InlineData("regtest", "false", false)]
    [InlineData("mainnet", null, true)]
    [InlineData("mainnet", "false", false)]
    [InlineData("mainnet", "true", true)]
    [InlineData("testnet", null, true)]
    [InlineData("signet", "false", false)]
    public void Given_EnableHtlcsConfig_When_NodeOptionsResolved_Then_HtlcsEnabledFollowsIt(
        string network, string? enableHtlcs, bool expected)
    {
        // Arrange
        (string, string)[] extra = enableHtlcs is null
                                       ? [("Node:Network", network)]
                                       : [("Node:Network", network), ("Node:EnableHtlcs", enableHtlcs)];
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(extra), new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<NodeOptions>>().Value;

        // Assert
        Assert.Equal(enableHtlcs is null ? null : bool.Parse(enableHtlcs), options.EnableHtlcs);
        Assert.Equal(expected, options.HtlcsEnabled);
    }

    [Theory]
    [InlineData("regtest", true)]
    [InlineData("mainnet", null)]
    [InlineData("testnet", null)]
    [InlineData("signet", true)]
    [InlineData("mutinynet", true)]
    public void Given_DefaultConfigJson_When_Bound_Then_RoutingDefaultsAndEnableHtlcsAreExplicitAndValid(
        string network, bool? expectedEnableHtlcs)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();
        var defaults = new RoutingOptions();

        // Act
        var options = provider.GetRequiredService<IOptions<NodeOptions>>().Value;

        // Assert
        // Mainnet and testnet keep the key (the operator's switch) with null, which binds as unset: the code default
        // of the network applies, whatever the BOLT 5 O6-T4 gate makes it
        Assert.Contains(configuration.GetSection("Node").GetChildren(), c => c.Key == "EnableHtlcs");
        Assert.Equal(expectedEnableHtlcs, configuration.GetValue<bool?>("Node:EnableHtlcs"));
        Assert.Equal(expectedEnableHtlcs, options.EnableHtlcs);
        var codeDefault = new NodeOptions { BitcoinNetwork = options.BitcoinNetwork }.HtlcsEnabled;
        Assert.Equal(expectedEnableHtlcs ?? codeDefault, options.HtlcsEnabled);
        Assert.Equal(defaults.FeeBaseMsat, options.Routing.FeeBaseMsat);
        Assert.Equal(defaults.FeeProportionalMillionths, options.Routing.FeeProportionalMillionths);
        Assert.Equal(defaults.CltvExpiryDelta, options.Routing.CltvExpiryDelta);
        Assert.Equal(defaults.MaxCltvExpiryDistance, options.Routing.MaxCltvExpiryDistance);
        Assert.Equal(defaults.ExpiryTooSoonBlocks, options.Routing.ExpiryTooSoonBlocks);
        Assert.Equal(defaults.InvoiceMinFinalCltvExpiry, options.Routing.InvoiceMinFinalCltvExpiry);
        Assert.Equal(defaults.InvoiceExpirySeconds, options.Routing.InvoiceExpirySeconds);
        Assert.Equal(defaults.HtlcMinimumMsat, options.Routing.HtlcMinimumMsat);
        Assert.Null(options.Routing.HtlcMaximumMsat);
        // Every routing key in the template is a real RoutingOptions property (a typo would bind nothing)
        var routingKeys = configuration.GetSection("Node:Routing").GetChildren().Select(c => c.Key).ToList();
        Assert.Equal(8, routingKeys.Count);
        Assert.All(routingKeys, key => Assert.NotNull(typeof(RoutingOptions).GetProperty(key)));
    }

    [Theory]
    [InlineData("mainnet", false)]
    [InlineData("testnet", true)]
    [InlineData("regtest", true)]
    [InlineData("signet", true)]
    [InlineData("mutinynet", true)]
    public void Given_DefaultConfigJson_When_Bound_Then_GossipMainnetGateIsExplicit(string network, bool expectedOn)
    {
        // Arrange: BOLT 7 plan D12 / G5-T5 (wave d12: the graph and the sync are on everywhere, mainnet included; the
        // relay of others' gossip and public channels stay off on mainnet)
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var nodeOptions = provider.GetRequiredService<IOptions<NodeOptions>>().Value;
        var graph = provider.GetRequiredService<IOptions<GossipGraphOptions>>().Value;
        var sync = provider.GetRequiredService<IOptions<GossipSyncOptions>>().Value;
        var relay = provider.GetRequiredService<IOptions<GossipRelayOptions>>().Value;
        var gossip = provider.GetRequiredService<IOptions<GossipOptions>>().Value;
        var chain = nodeOptions.BitcoinNetwork;

        // Assert: written explicitly (visible in the file) and equal to the code default of the network
        Assert.True(graph.Enabled);
        Assert.True(sync.SyncEnabled);
        Assert.True(graph.IsEnabledFor(chain));
        Assert.True(sync.IsSyncEnabledFor(chain));
        Assert.Equal(expectedOn, relay.RelayEnabled);
        Assert.Equal(expectedOn, relay.IsRelayEnabledFor(chain));
        // AcceptPublicChannels keeps its code default everywhere: on mainnet AllowPublicChannelsOnMainnet alone gates
        // public channels, ours and a peer's
        Assert.Equal(new GossipOptions().AcceptPublicChannels, gossip.AcceptPublicChannels);
        Assert.True(gossip.AcceptPublicChannels);
        Assert.False(gossip.AllowPublicChannelsOnMainnet);
        Assert.Equal(new GossipGraphOptions().IsEnabledFor(chain), graph.IsEnabledFor(chain));
        Assert.Equal(new GossipSyncOptions().IsSyncEnabledFor(chain), sync.IsSyncEnabledFor(chain));
        Assert.Equal(new GossipRelayOptions().IsRelayEnabledFor(chain), relay.IsRelayEnabledFor(chain));
        Assert.Equal(expectedOn, gossip.ArePublicChannelsAllowed(chain));
        // Every key of the section binds to a real option (a typo would bind nothing)
        var gossipKeys = configuration.GetSection(GossipOptions.SectionName).GetChildren().Select(c => c.Key).ToList();
        Assert.Equal(6, gossipKeys.Count);
        // NL-373: the memory budget is written on every network, 1 GiB
        Assert.Equal(GossipGraphOptions.DefaultMaxMemoryMb, graph.MaxMemoryMb);
        Assert.Equal("1024", configuration["Gossip:MaxMemoryMb"]);
        Assert.All(gossipKeys, key => Assert.True(typeof(GossipOptions).GetProperty(key) is not null
                                               || typeof(GossipGraphOptions).GetProperty(key) is not null
                                               || typeof(GossipSyncOptions).GetProperty(key) is not null
                                               || typeof(GossipRelayOptions).GetProperty(key) is not null,
                                                  key));
    }

    [Fact]
    public void Given_MainnetDefaultConfigJson_When_OnlyAllowPublicChannelsOnMainnetIsFlipped_Then_PublicChannelsAreAccepted()
    {
        // Arrange: the documented D12 switch is one key; the template must not leave a second one (AcceptPublicChannels)
        // off, or a peer's public open_channel would still be refused
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson("mainnet");
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Gossip:AllowPublicChannelsOnMainnet"] = "true"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var nodeOptions = provider.GetRequiredService<IOptions<NodeOptions>>().Value;
        var gossip = provider.GetRequiredService<IOptions<GossipOptions>>().Value;

        // Assert
        Assert.Equal(BitcoinNetwork.Mainnet, nodeOptions.BitcoinNetwork);
        Assert.True(gossip.ArePublicChannelsAllowed(nodeOptions.BitcoinNetwork));
        Assert.True(gossip.AcceptPublicChannels);
    }

    [Theory]
    [InlineData("mutinynet", "mutinynet", "https://mutinynet.com/api/v1/fees/recommended")]
    [InlineData("signet", "", "https://mempool.space/signet/api/v1/fees/recommended")]
    public void Given_SignetDefaultConfigJson_When_Bound_Then_SignetWithCustomSignetSectionAndFeeSource(
        string network, string customSignetName, string feeUrl)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<NodeOptions>>().Value;
        var fees = provider.GetRequiredService<IOptions<FeeEstimationOptions>>().Value;
        var bitcoin = provider.GetRequiredService<IOptions<BitcoinOptions>>().Value;

        // Assert: a custom signet is signet for everything but its name (chain hash, invoices, addresses)
        Assert.Equal("signet", configuration["Node:Network"]);
        Assert.Equal(BitcoinNetwork.Signet, options.BitcoinNetwork);
        Assert.Equal([ChainConstants.Signet], options.Features.ChainHashes);
        Assert.Equal(customSignetName, options.CustomSignet?.Name ?? string.Empty);
        Assert.Empty(options.GetValidationErrors());
        Assert.Empty(configuration.GetSection("Node:DnsSeedServers").GetChildren());
        Assert.Equal(FeeEstimationOptions.SourceHttp, fees.Source);
        Assert.Equal(feeUrl, fees.Url);
        Assert.Equal("sat/vB", fees.RateUnit);
        Assert.Null(fees.RateMultiplier);
        Assert.Empty(fees.GetValidationErrors());
        Assert.Equal("http://localhost:38332", bitcoin.RpcEndpoint);
        Assert.Equal(28332, bitcoin.ZmqBlockPort);
        Assert.Equal(28333, bitcoin.ZmqTxPort);
    }

    [Theory]
    [InlineData("regtest", "Fixed", "http://localhost:18443")]
    [InlineData("testnet", "Http", "http://localhost:18332")]
    [InlineData("mainnet", "Http", "http://localhost:8332")]
    public void Given_DefaultConfigJson_When_Bound_Then_FeeSourceAndRpcPortFitTheNetwork(string network,
        string feeSource, string rpcEndpoint)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                                                               NodeConfigurationExtensions
                                                                  .CreateDefaultConfigJson(network))))
                           .Build();

        // Act
        var fees = configuration.GetSection("FeeEstimation").Get<FeeEstimationOptions>()!;

        // Assert: no RateMultiplier any more (NL-288)
        Assert.Equal(feeSource, fees.Source);
        Assert.Null(fees.RateMultiplier);
        Assert.Empty(fees.GetValidationErrors());
        Assert.Equal(rpcEndpoint, configuration["Bitcoin:RpcEndpoint"]);
        Assert.Null(configuration["Node:CustomSignet:Name"]);
    }

    [Fact]
    public void Given_UnknownNetwork_When_CreatingDefaultConfigJson_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => NodeConfigurationExtensions.CreateDefaultConfigJson("unknown-net"));
    }

    [Fact]
    public void Given_LayerServicesOnly_When_Composed_Then_ChannelFactoriesAndSignerResolve()
    {
        // Arrange: the layers' own DI, without anything the daemon registers by hand (NL-156)
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddLogging();
        services.AddOptions<NodeOptions>().BindConfiguration("Node");
        Application.DependencyInjection.AddApplicationServices(services);
        Infrastructure.Bitcoin.DependencyInjection.AddBitcoinInfrastructure(services);
        Infrastructure.DependencyInjection.AddInfrastructureServices(services);
        Infrastructure.Persistence.DependencyInjection.AddPersistenceInfrastructureServices(services, configuration);
        Infrastructure.Repositories.DependencyInjection.AddRepositoriesInfrastructureServices(services);
        Infrastructure.Serialization.DependencyInjection.AddSerializationInfrastructureServices(services);
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        Assert.NotNull(provider.GetRequiredService<IChannelOpenValidator>());
        Assert.NotNull(provider.GetRequiredService<IChannelFactory>());
        Assert.NotNull(provider.GetRequiredService<ICommitmentTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<IFundingTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<ILightningSigner>());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheSecureKeyManagerGivenIsUsed()
    {
        // Arrange
        var secureKeyManager = new Mock<ISecureKeyManager>().Object;
        var services = new ServiceCollection();

        // Act
        services.AddNltgNodeServices(BuildConfiguration(), secureKeyManager);
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Same(secureKeyManager, provider.GetRequiredService<ISecureKeyManager>());
    }

    [Fact]
    public void Given_CltvExpiryDeltaBelow34_When_NodeOptionsResolved_Then_ValidationFails()
    {
        // Arrange: BOLT 7 recommends cltv_expiry_delta >= 34
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Routing:CltvExpiryDelta", "33")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<OptionsValidationException>(() =>
                                                                       provider
                                                                          .GetRequiredService<IOptions<NodeOptions>>()
                                                                          .Value);

        // Assert
        Assert.Contains(exception.Failures, f => f.Contains("CltvExpiryDelta"));
    }

    [Fact]
    public void Given_ReconnectDelaysConfigured_When_NodeOptionsResolved_Then_TheyAreBound()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:ReconnectInitialDelay", "00:00:01"),
                                                        ("Node:ReconnectMaxDelay", "00:00:30")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<NodeOptions>>().Value;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), options.ReconnectInitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ReconnectMaxDelay);
    }

    [Fact]
    public void Given_NodeSwitchSection_When_Composed_Then_MppTimeoutBoundAndAttributionServiceResolves()
    {
        // Arrange (ABCD W6 integration: Node:Switch binds HtlcSwitchOptions, AddBitcoinInfrastructure registers the
        // W6-D attribution service)
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Node:Switch:MppTimeout", "00:01:30")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var switchOptions = provider.GetRequiredService<IOptions<HtlcSwitchOptions>>().Value;
        var attribution = provider.GetRequiredService<IAttributionDataService>();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(90), switchOptions.MppTimeout);
        Assert.NotNull(attribution);
        Assert.Single(services, d => d.ServiceType == typeof(IAttributionDataService));
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheOwnGossipServicesResolve()
    {
        // Arrange (BOLT 7 plan G1-T4..T7: registered by AddApplicationServices, nothing to add in the daemon)
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act / Assert
        Assert.IsType<ChannelAnnouncementService>(provider.GetRequiredService<IChannelAnnouncementService>());
        Assert.IsType<NodeAnnouncementService>(provider.GetRequiredService<INodeAnnouncementService>());
        Assert.IsType<GossipRelayScheduler>(provider.GetRequiredService<IGossipRelayScheduler>());
        Assert.IsType<PeerManagerGossipPeerDirectory>(provider.GetRequiredService<IGossipPeerDirectory>());
        Assert.Empty(provider.GetRequiredService<IGossipPeerDirectory>().GetConnectedPeers());
        // The graph ingress replaces lane B1's no-op sink (AddGossipGraphServices), so our own gossip reaches the graph
        Assert.Same(provider.GetRequiredService<GossipIngress>(), provider.GetRequiredService<IOwnGossipSink>());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheGossipSyncResolvesAsOneInstanceWithBoundOptions()
    {
        // Arrange (BOLT 7 plan G3-T1/G3-T2: the peer services' IGossipSyncService is the sync manager)
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:SyncPeers", "5"),
                                                        ("Gossip:SyncReplyTimeout", "00:00:30"),
                                                        ("Gossip:SyncEnabled", "false")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var manager = provider.GetRequiredService<GossipSyncManager>();
        var options = provider.GetRequiredService<IOptions<GossipSyncOptions>>().Value;

        // Assert
        Assert.Same(manager, provider.GetRequiredService<IGossipSyncService>());
        Assert.Same(manager, provider.GetRequiredService<IGossipSyncManager>());
        Assert.Equal(5, options.SyncPeers);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SyncReplyTimeout);
        Assert.False(manager.IsSyncEnabled);
    }

    [Fact]
    public void Given_EsploraFundingTxIdSource_When_Composed_Then_TheFundingOutputLookupUsesTheIndex()
    {
        // Arrange (D12 lane Z4: Gossip:FundingTxIdSource=Esplora is bound from the Gossip section)
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:FundingTxIdSource", "Esplora"),
                                                        ("Gossip:EsploraUrl", "http://esplora.test/api"),
                                                        ("Gossip:EsploraMaxInlineWait", "00:00:03")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var options = provider.GetRequiredService<IOptions<FundingTxIdSourceOptions>>().Value;
        var lookup = provider.GetRequiredService<IFundingOutputLookup>();
        var source = typeof(FundingOutputLookup)
                    .GetField("_txIdSource",
                              System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(lookup);

        // Assert
        Assert.Equal(FundingTxIdSourceKind.Esplora, options.FundingTxIdSource);
        Assert.Equal("http://esplora.test/api", options.EsploraUrl);
        Assert.Equal(TimeSpan.FromSeconds(3), options.EsploraMaxInlineWait);
        Assert.IsType<FundingOutputLookup>(lookup);
        Assert.Same(provider.GetRequiredService<EsploraTxIdSource>(), source);
    }

    [Fact]
    public void Given_NoFundingTxIdSource_When_Composed_Then_TheFundingOutputLookupAsksBitcoind()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var lookup = provider.GetRequiredService<IFundingOutputLookup>();
        var source = typeof(FundingOutputLookup)
                    .GetField("_txIdSource",
                              System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(lookup);

        // Assert
        Assert.Equal(FundingTxIdSourceKind.Bitcoind,
                     provider.GetRequiredService<IOptions<FundingTxIdSourceOptions>>().Value.FundingTxIdSource);
        Assert.Null(source);
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheRelayOfOthersIsWiredWithOriginTracking()
    {
        // Arrange (BOLT 7 plan G3-T3: the peer services' ingress records origins; the relay reads graph and filters)
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:RelayFlushInterval", "00:00:30"),
                                                        ("Gossip:BacklogMessagesPerSecond", "500")),
                                     new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var ingress = provider.GetRequiredService<IGossipIngress>();
        var relay = Assert.IsType<GossipRelayScheduler>(provider.GetRequiredService<IGossipRelayScheduler>());
        var options = provider.GetRequiredService<IOptions<GossipRelayOptions>>().Value;

        // Assert
        Assert.IsType<OriginTrackingGossipIngress>(ingress);
        Assert.True(ingress.IsEnabled);
        Assert.True(relay.IsRelayingOthers);
        Assert.IsType<PeerGossipSender>(provider.GetRequiredService<IGossipPeerSender>());
        Assert.Equal(TimeSpan.FromSeconds(30), options.RelayFlushInterval);
        Assert.Equal(500, options.BacklogMessagesPerSecond);
        // The graph's own entry points are unchanged: the sink and the sync manager use the ingress itself
        Assert.Same(provider.GetRequiredService<GossipIngress>(), provider.GetRequiredService<IOwnGossipSink>());
    }

    [Fact]
    public void Given_ABadRelaySetting_When_OptionsResolved_Then_ValidationFails()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:BacklogMessagesPerSecond", "0")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<OptionsValidationException>(() =>
                                                                      provider
                                                                         .GetRequiredService<
                                                                              IOptions<GossipRelayOptions>>().Value);

        // Assert
        Assert.Contains(exception.Failures, f => f.Contains(nameof(GossipRelayOptions.BacklogMessagesPerSecond)));
    }

    [Fact]
    public void Given_ABadSyncSetting_When_OptionsResolved_Then_ValidationFails()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:MaxScidsPerReply", "0")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<OptionsValidationException>(() =>
                                                                      provider
                                                                         .GetRequiredService<
                                                                              IOptions<GossipSyncOptions>>().Value);

        // Assert
        Assert.Contains(exception.Failures, f => f.Contains(nameof(GossipSyncOptions.MaxScidsPerReply)));
    }

    [Fact]
    public void Given_GossipSection_When_Bound_Then_AddressesAndIntervalsAreRead()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(("Gossip:AnnounceAddresses:0", "203.0.113.5:9735"),
                                                        ("Gossip:OwnGossipFlushInterval", "00:00:05"),
                                                        ("Node:Alias", "nltg"), ("Node:Color", "#00ff00")),
                                     new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var gossip = provider.GetRequiredService<IOptions<GossipOptions>>().Value;
        var node = provider.GetRequiredService<IOptions<NodeOptions>>().Value;

        // Assert
        Assert.Equal(["203.0.113.5:9735"], gossip.AnnounceAddresses);
        Assert.Equal(TimeSpan.FromSeconds(5), gossip.OwnGossipFlushInterval);
        Assert.Equal("nltg", node.Alias);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, node.GetColorBytes());
    }

    [Theory]
    [InlineData("Gossip:AnnounceAddresses:0", "2001:db8::1:9735", "Gossip:AnnounceAddresses")]
    [InlineData("Node:Color", "blue", "Node:Color")]
    public void Given_ABadAnnouncementSetting_When_OptionsResolved_Then_ValidationFails(string key, string value,
                                                                                        string expected)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration((key, value)), new Mock<ISecureKeyManager>().Object);
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<GossipOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<NodeOptions>>().Value;
        });

        // Assert
        Assert.Contains(exception.Failures, f => f.Contains(expected));
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=:memory:"
        };

        // An extra value overrides a default one
        foreach (var (key, value) in extra)
            values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}