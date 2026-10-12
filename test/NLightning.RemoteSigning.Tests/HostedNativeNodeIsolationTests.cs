using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Application.Payments.Routing;
using NLightning.Application.Tests.Channels.Harness;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Money;
using NLightning.Domain.Payments.Enums;
using NLightning.Domain.Payments.ValueObjects;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class HostedNativeNodeIsolationTests
{
    [Fact]
    public async Task Given_TwoHostedSigners_When_RequestsCollideAndOneStops_Then_AuthorityAndReceiptsStayIsolated()
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var first = await supervisor.AddAsync("first", new string('a', 64));
        var second = await supervisor.AddAsync("second", new string('b', 64));
        using var a = new RemoteSignerConnection(first.Options());
        using var b = new RemoteSignerConnection(second.Options());
        Assert.NotEqual(a.Identity.NodePublicKey, b.Identity.NodePublicKey);
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.StatePath, second.StatePath);
        var aKeys = new RemoteSecureKeyManager(a);
        var bKeys = new RemoteSecureKeyManager(b);
        Assert.NotEqual(aKeys.GetWalletPublicKey(0, false, AddressType.P2Wpkh),
                        bKeys.GetWalletPublicKey(0, false, AddressType.P2Wpkh));
        var wrongCredentials = second.Options();
        wrongCredentials.AuthToken = first.Token;
        Assert.Throws<RemoteSignerTransportException>(() => new RemoteSignerConnection(wrongCredentials));

        var original = a.PrepareForContext(SignerOperations.ReserveChannelKeyIndex);
        a.Execute(original);
        Assert.Equal(RequestOutcome.NotFound,
                     b.Reconcile(b.PrepareForContext(SignerOperations.ReserveChannelKeyIndex)).Outcome);
        Assert.Throws<ArgumentException>(() => b.Execute(original));
        Assert.Throws<ArgumentException>(() => b.Reconcile(original));
        var colliding = b.PrepareForContext(SignerOperations.ReserveChannelKeyIndex);
        colliding.RequestId = original.RequestId;
        b.Execute(colliding);
        var aReceipt = a.Reconcile(original).Response.Payload.ToByteArray();
        var bReceipt = b.Reconcile(colliding).Response.Payload.ToByteArray();
        Assert.Equal(RequestOutcome.Completed, a.Reconcile(original).Outcome);
        Assert.Equal(RequestOutcome.Completed, b.Reconcile(colliding).Outcome);

        await first.StopAsync();
        Assert.Throws<RemoteSignerTransportException>(() => aKeys.ReserveChannelKeyIndex());
        var bIndex = bKeys.ReserveChannelKeyIndex();
        await first.RestartAsync();
        using var recovered = new RemoteSignerConnection(first.Options());
        Assert.Equal(a.Identity.NodePublicKey, recovered.Identity.NodePublicKey);
        Assert.Equal(aReceipt, recovered.Reconcile(original).Response.Payload.ToByteArray());
        Assert.Equal(bReceipt, b.Reconcile(colliding).Response.Payload.ToByteArray());
        Assert.True(bKeys.ReserveChannelKeyIndex() > bIndex);
        Assert.Throws<NotSupportedException>(() => new RemoteSecureKeyManager(recovered).GetNodeKeyPair());
    }

    [Fact]
    public async Task Given_HostedNodesWithSeparateSigners_When_OneSignerStops_Then_OtherNodesStillSettleFractionalPayments()
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var processes = new[]
        {
            await supervisor.AddAsync("alice", new string('a', 64)),
            await supervisor.AddAsync("bob", new string('b', 64)),
            await supervisor.AddAsync("carol", new string('c', 64))
        };
        using var a = new RemoteSignerConnection(processes[0].Options());
        using var b = new RemoteSignerConnection(processes[1].Options());
        using var c = new RemoteSignerConnection(processes[2].Options());
        var connections = new[] { a, b, c };
        await using var harness = await ThreeNodeHarness.CreateAsync(network =>
        {
            for (var i = 0; i < network.Nodes.Count; i++)
            {
                var connection = connections[i];
                network.Nodes[i].ExternalKeyManager = new RemoteSecureKeyManager(connection);
                network.Nodes[i].ConfigureServices = services =>
                {
                    services.AddSingleton(connection.Context);
                    services.Replace(ServiceDescriptor.Singleton<ILightningSigner>(provider =>
                        new RemoteLightningSigner(connection, provider.GetService<IChannelSigningInfoSource>(),
                                                 provider.GetRequiredService<IUtxoMemoryRepository>())));
                };
            }
        });
        Assert.Equal(3, harness.Nodes.Select(node => node.DatabasePath).Distinct().Count());
        Assert.Equal(3, harness.Nodes.Select(node => node.NodeId).Distinct().Count());
        var amount = LightningMoney.MilliSatoshis(10_000_123);
        var forwarded = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "isolated forwarding", null,
                                                                       TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(amount, forwarded.PaymentHash, forwarded.PaymentSecret));
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Settled,
                     (await harness.Carol.InScopeAsync(unit =>
                         unit.InvoiceDbRepository.GetByPaymentHashAsync(forwarded.PaymentHash)))!.Status);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);

        await processes[0].StopAsync();
        Assert.Throws<RemoteSignerTransportException>(() => new RemoteSecureKeyManager(a).ReserveChannelKeyIndex());
        var bobBefore = harness.Bob.LocalBalanceMsat;
        var carolBefore = harness.Carol.LocalBalanceMsat;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "signer A unavailable", null,
                                                                     TestContext.Current.CancellationToken);
        var expiry = ThreeNodeHarness.BlockHeight + 43;
        var route = new PaymentRoute([new RouteHop(harness.Carol.NodeId, amount, expiry, null)],
                                     amount, expiry, invoice.PaymentHash, invoice.PaymentSecret);
        var onion = await harness.Bob.Services.GetRequiredService<PaymentOnionFactory>().CreateAsync(route);
        await harness.Bob.Operations.OfferHtlcAsync(ThreeNodeHarness.BobCarolChannelId, amount,
                                                  invoice.PaymentHash, expiry, onion.Packet, null,
                                                  HtlcOrigin.Local(invoice.PaymentHash), TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        Assert.Single(harness.Bob.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Bob.PaymentHandler.Failed);
        Assert.Equal(bobBefore - (long)amount.MilliSatoshi, harness.Bob.LocalBalanceMsat);
        Assert.Equal(carolBefore + (long)amount.MilliSatoshi, harness.Carol.LocalBalanceMsat);
        var settled = await harness.Carol.InScopeAsync(unit => unit.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, settled!.Status);
        Assert.Equal(amount, settled.AmountReceived);

        await processes[0].RestartAsync();
        await harness.RestartAsync(harness.Alice);
        await harness.ReconnectAsync(harness.Alice);
        var afterRestart = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "recovered hosted node", null,
                                                                          TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(amount, afterRestart.PaymentHash, afterRestart.PaymentSecret));
        await harness.PumpAsync();
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}