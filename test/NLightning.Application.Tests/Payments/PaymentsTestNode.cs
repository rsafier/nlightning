using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;
using Application.Payments.FinalHop;
using Application.Payments.Onion;
using Application.Payments.Routing;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.ValueObjects;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Protocol.Onion;
using Infrastructure.Serialization;

/// <summary>
/// One node of an in-process payment test: the production payment services (<see cref="AddPaymentsServices"/>) over
/// the real Sphinx (<c>AddBitcoinInfrastructure</c>), hop payload and failure serializers
/// (<c>AddSerializationInfrastructureServices</c>) and replay cache, with its own node key, routing options and an
/// in-memory invoice store.
/// </summary>
internal sealed class PaymentsTestNode : IDisposable
{
    private readonly ServiceProvider _provider;

    public string Name { get; }
    public TestNodeKeyManager KeyManager { get; }
    public CompactPubKey NodeId => KeyManager.NodeId;
    public NodeOptions Options { get; }
    public InMemoryInvoiceDbRepository Invoices { get; } = new();
    public Mock<IUnitOfWork> UnitOfWork { get; } = new();

    public IncomingOnionProcessor OnionProcessor => _provider.GetRequiredService<IncomingOnionProcessor>();
    public FinalHopProcessor FinalHopProcessor => _provider.GetRequiredService<FinalHopProcessor>();
    public IForwardingPolicy ForwardingPolicy => _provider.GetRequiredService<IForwardingPolicy>();
    public HintRouteBuilder RouteBuilder => _provider.GetRequiredService<HintRouteBuilder>();
    public PaymentOnionFactory OnionFactory => _provider.GetRequiredService<PaymentOnionFactory>();
    public IInvoiceService InvoiceService => _provider.GetRequiredService<IInvoiceService>();
    public ISphinxService Sphinx => _provider.GetRequiredService<ISphinxService>();
    public IFailureOnionService FailureOnion => _provider.GetRequiredService<IFailureOnionService>();
    public IHopPayloadSerializer HopPayloadSerializer => _provider.GetRequiredService<IHopPayloadSerializer>();

    public PaymentsTestNode(string name, byte seed, RoutingOptions? routing = null)
    {
        Name = name;
        KeyManager = new TestNodeKeyManager(seed);
        Options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest, Routing = routing ?? new RoutingOptions() };
        UnitOfWork.Setup(x => x.SaveChangesAsync()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(KeyManager);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<IOnionReplayCache>(new OnionReplayCache());
        services.AddScoped<IInvoiceDbRepository>(_ => Invoices);
        services.AddScoped(_ => UnitOfWork.Object);
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPaymentsServices();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        });
    }

    public void Dispose() => _provider.Dispose();
}