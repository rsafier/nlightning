using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Channels.Interfaces;
using Application.Channels.Reestablish;
using Application.Gossip.Interfaces;
using Application.Gossip.Services;
using Application.Payments.Invoices;
using Application.Payments.Send;
using Application.Payments.Send.Interfaces;
using Application.Payments.Switch;
using Daemon.Extensions;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Reestablish;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
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
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();
        Assert.Contains(ClientCommand.ListChannels, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
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
        Assert.IsType<HtlcSwitch>(htlcSwitch);
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
    [InlineData("regtest", null, true)]
    [InlineData("regtest", "false", false)]
    [InlineData("mainnet", null, false)]
    [InlineData("mainnet", "true", true)]
    [InlineData("testnet", null, false)]
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
    [InlineData("mainnet", false)]
    [InlineData("testnet", false)]
    public void Given_DefaultConfigJson_When_Bound_Then_RoutingDefaultsAndEnableHtlcsAreExplicitAndValid(
        string network, bool expectedHtlcs)
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
        Assert.Equal(expectedHtlcs, configuration.GetValue<bool?>("Node:EnableHtlcs"));
        Assert.Equal(expectedHtlcs, options.EnableHtlcs);
        Assert.Equal(expectedHtlcs, options.HtlcsEnabled);
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