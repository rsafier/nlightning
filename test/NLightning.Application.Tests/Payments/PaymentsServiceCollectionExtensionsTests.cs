using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;
using Application.Payments.FinalHop;
using Application.Payments.Invoices;
using Application.Payments.Onion;
using Application.Payments.Policy;
using Application.Payments.Routing;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure;
using Infrastructure.Bitcoin;
using Infrastructure.Serialization;

public class PaymentsServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_DaemonLayerRegistrations_When_AddPaymentsServices_Then_EveryServiceResolvesAsSingleton()
    {
        // Arrange: the layers the daemon composes, plus the host pieces
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton<ISecureKeyManager>(new TestNodeKeyManager(0x51));
        services.AddScoped(_ => new Mock<IInvoiceDbRepository>().Object);
        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);
        services.AddInfrastructureServices();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();

        // Act
        services.AddPaymentsServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Assert
        Assert.Same(provider.GetRequiredService<IncomingOnionProcessor>(),
                    provider.GetRequiredService<IncomingOnionProcessor>());
        Assert.Same(provider.GetRequiredService<FinalHopProcessor>(), provider.GetRequiredService<FinalHopProcessor>());
        Assert.IsType<HtlcForwardingPolicy>(provider.GetRequiredService<IForwardingPolicy>());
        Assert.Same(provider.GetRequiredService<HintRouteBuilder>(), provider.GetRequiredService<HintRouteBuilder>());
        Assert.Same(provider.GetRequiredService<PaymentOnionFactory>(),
                    provider.GetRequiredService<PaymentOnionFactory>());
        Assert.IsType<InvoiceService>(provider.GetRequiredService<IInvoiceService>());
        Assert.Same(provider.GetRequiredService<IInvoiceService>(), provider.GetRequiredService<IInvoiceService>());
    }
}