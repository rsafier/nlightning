using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-216 through the production <see cref="Application.Payments.Switch.HtlcSwitch"/> on
/// <see cref="ThreeNodeHarness"/>: while a node's chain processing is halted it takes no new HTLC risk. As final hop
/// it fails a new payment back (<c>temporary_node_failure</c>) instead of revealing the preimage; as forwarder its
/// offer is refused and the upstream HTLC fails back with <c>temporary_channel_failure</c>.
/// </summary>
public class ChainHaltSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(30_000_000);

    [Fact]
    public async Task Given_FinalHopHalted_When_PaymentArrives_Then_FailedBackAndPreimageKept()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Carol.ConfigureServices =
                                                                         services => Halt(services));
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "halted", null,
                                                                     TestContext.Current.CancellationToken);

        // Act
        var onion = await PayAsync(harness, invoice);
        await harness.PumpAsync();

        // Assert: failed by Carol (hop 1), nothing fulfilled, the invoice still open
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.DoesNotContain(harness.Sent, s => s.From == "Carol" && s.Message is UpdateFulfillHtlcMessage);
        var stored = await harness.Carol.InScopeAsync(async u => await u.InvoiceDbRepository
                                                                        .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, stored!.Status);
    }

    [Fact]
    public async Task Given_ForwarderHalted_When_PaymentArrives_Then_NotForwardedAndFailedBackUpstream()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices =
                                                                         services => Halt(services));
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "halted forwarder", null,
                                                                     TestContext.Current.CancellationToken);

        // Act
        var onion = await PayAsync(harness, invoice);
        await harness.PumpAsync();

        // Assert: Bob (hop 0) never offered to Carol and failed the HTLC back
        Assert.DoesNotContain(harness.Sent, s => s is { From: "Bob", To: "Carol" }
                                              && s.Message is UpdateAddHtlcMessage);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
    }

    private static void Halt(IServiceCollection services)
    {
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
        monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        services.Replace(ServiceDescriptor.Singleton(monitor.Object));
    }

    private static async Task<PaymentOnion> PayAsync(ThreeNodeHarness harness, InvoiceModel invoice)
    {
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var onion = await harness.Alice.Services.GetRequiredService<PaymentOnionFactory>().CreateAsync(route);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash));
        return onion;
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }
}