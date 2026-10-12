using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;

/// <summary>
/// The composition <see cref="TrampolineHarness"/> gives the phase 2 proofs (NL-875 TR5): the topology and features,
/// the graph a payment service sees, and plain payments with the production payment service over it (T to C through X,
/// as the relay's outgoing leg will route; A to C through T and X), with no trampoline involved.
/// </summary>
public class TrampolineHarnessTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_000);

    [Fact]
    public async Task Given_TheDefaultHarness_When_Built_Then_TheChannelsAreOpenAndTAndCAdvertiseTrampoline()
    {
        // Act
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            SecondAliceTrampolineChannel = true
        });

        // Assert: four channels, both ends loaded and Open
        Assert.Equal(4, harness.Channels.Count);
        foreach (var channel in harness.Channels)
        {
            Assert.Equal(Domain.Channels.Enums.ChannelState.Open, channel.Funder.Channel(channel.ChannelId).State);
            Assert.Equal(Domain.Channels.Enums.ChannelState.Open, channel.Fundee.Channel(channel.ChannelId).State);
        }

        // Assert: bit 57 in the graph's node announcements of T and C only, and in C's invoices
        var graph = harness.BuildGraph();
        Assert.Equal(4, graph.ChannelCount);
        foreach (var node in harness.Nodes)
        {
            var advertises = harness.Is(node, TrampolineHarnessNodes.T | TrampolineHarnessNodes.C);
            Assert.True(graph.TryGetNode(node.NodeId, out var announced));
            Assert.Equal(advertises, FeatureBitSet(announced.Features, (int)Feature.OptionTrampolineRouting));
        }

        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        Assert.True(TrampolineHarness.Decode(invoice).Features!.IsFeatureSet(Feature.OptionTrampolineRouting, false));
    }

    [Fact]
    public async Task Given_TWithThePaymentService_When_ItPaysCsInvoice_Then_TheGraphRoutesItThroughX()
    {
        // Arrange: what the relay's outgoing leg will need on T
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            PaymentSenders = TrampolineHarnessNodes.T
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.T, invoice));

        // Assert: paid with X's fee only
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(invoice.Preimage!.Value, result.Payment.Preimage);
        var xFee = Channels.Harness.ThreeNodeHarness.ForwardingFeeOf(TrampolineHarness.XRouting, s_amount);
        Assert.Equal(xFee, result.Payment.Fee);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_AWithThePaymentService_When_ItPaysCsInvoice_Then_ThePlainRouteThroughTAndXSettles()
    {
        // Arrange
        await using var harness = await TrampolineHarness.CreateAsync(new TrampolineHarnessOptions
        {
            PaymentSenders = TrampolineHarnessNodes.A
        });
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);

        // Act
        var result = await harness.PumpUntilAsync(PayAsync(harness.A, invoice));

        // Assert: T's and X's fees, as BuildRoute computes them
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        var route = harness.BuildRoute([harness.T, harness.X, harness.C], s_amount,
                                       TrampolineHarness.FinalCltvOf(invoice), invoice.PaymentHash,
                                       invoice.PaymentSecret);
        Assert.Equal(route.Fee, result.Payment.Fee);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    [Fact]
    public async Task Given_ARestartedForwarder_When_APlainPaymentFollows_Then_ItIsForwarded()
    {
        // Arrange: X restarts at a quiescent point
        await using var harness = await TrampolineHarness.CreateAsync();
        var invoice = await TrampolineHarness.CreateInvoiceAsync(harness.C, s_amount);
        await harness.RestartAsync(harness.X);
        await harness.ReconnectAsync(harness.X);
        var route = harness.BuildRoute([harness.T, harness.X, harness.C], s_amount,
                                       TrampolineHarness.FinalCltvOf(invoice), invoice.PaymentHash,
                                       invoice.PaymentSecret);

        // Act
        await harness.OfferAsync(harness.A, TrampolineHarness.AliceTrampolineChannelId, route);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.A.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Equal(InvoiceStatus.Settled,
                     (await TrampolineHarness.GetInvoiceAsync(harness.C, invoice.PaymentHash))!.Status);
        harness.AssertQuiescent();
    }

    private static Task<PayInvoiceResult> PayAsync(Channels.Harness.SwitchNode payer, InvoiceModel invoice) =>
        payer.Services.GetRequiredService<IPaymentService>()
             .PayInvoiceAsync(invoice.Bolt11!, null, new PayInvoiceOptions { Timeout = TimeSpan.FromMinutes(5) },
                              TestContext.Current.CancellationToken);

    /// <summary>Whether bit <paramref name="bit"/> is set in big-endian wire feature bytes.</summary>
    private static bool FeatureBitSet(ReadOnlyMemory<byte> wire, int bit)
    {
        var span = wire.Span;
        var index = span.Length - 1 - bit / 8;
        return index >= 0 && (span[index] & (1 << (bit % 8))) != 0;
    }
}