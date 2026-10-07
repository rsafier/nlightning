using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Application.Tests.Channels.Harness;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Money;
using NLightning.Domain.Payments.Enums;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Real production payment, channel, onion and SQLite stack; peer messages use the harness FIFO links.</summary>
public sealed class RemotePaymentFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlicePaysCarolThroughBobWithAllKeysInIndependentSignerProcesses(bool taproot)
    {
        await using var aliceSigner = new SignerDaemonFixture(new string('a', 64));
        await using var bobSigner = new SignerDaemonFixture(new string('b', 64));
        await using var carolSigner = new SignerDaemonFixture(new string('c', 64));
        await aliceSigner.InitializeAsync();
        await bobSigner.InitializeAsync();
        await carolSigner.InitializeAsync();
        using var alice = new RemoteSignerConnection(aliceSigner.Options());
        using var bob = new RemoteSignerConnection(bobSigner.Options());
        using var carol = new RemoteSignerConnection(carolSigner.Options());
        var connections = new[] { alice, bob, carol };
        await using var harness = await ThreeNodeHarness.CreateAsync(network =>
        {
            for (var i = 0; i < network.Nodes.Count; i++)
            {
                var connection = connections[i];
                network.Nodes[i].ExternalKeyManager = new RemoteSecureKeyManager(connection);
                network.Nodes[i].ConfigureServices = services =>
                    services.Replace(ServiceDescriptor.Singleton<ILightningSigner>(provider =>
                        new RemoteLightningSigner(connection, provider.GetService<IChannelSigningInfoSource>(),
                                                 provider.GetRequiredService<IUtxoMemoryRepository>())));
            }
        }, simpleTaproot: taproot);
        foreach (var node in harness.Nodes)
        {
            Assert.IsType<RemoteLightningSigner>(node.Signer);
            Assert.Throws<NotSupportedException>(() => node.Services.GetRequiredService<ISecureKeyManager>().GetNodeKeyPair());
        }
        var amount = LightningMoney.MilliSatoshis(50_000_123);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "remote signer payment", null,
                                                                    TestContext.Current.CancellationToken);
        var aliceBefore = harness.Alice.LocalBalanceMsat;
        var bobBefore = harness.Bob.LocalBalanceMsat;
        var carolBefore = harness.Carol.LocalBalanceMsat;
        var fee = ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, amount);
        await harness.AlicePaysAsync(harness.RouteToCarol(amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage!.Value, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(aliceBefore - (long)(amount + fee).MilliSatoshi, harness.Alice.LocalBalanceMsat);
        Assert.Equal(bobBefore + (long)fee.MilliSatoshi, harness.Bob.LocalBalanceMsat);
        Assert.Equal(carolBefore + (long)amount.MilliSatoshi, harness.Carol.LocalBalanceMsat);
        var settled = await harness.Carol.InScopeAsync(unit => unit.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, settled!.Status);
        Assert.Equal(amount, settled.AmountReceived);
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}