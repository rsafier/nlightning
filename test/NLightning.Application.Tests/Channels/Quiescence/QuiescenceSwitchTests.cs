using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Quiescence;

using Application.Channels.Quiescence;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// Splicing plan Q1-T4 with the production <c>HtlcSwitch</c> (<see cref="ThreeNodeHarness"/>, SQLite): a fulfill the
/// switch can't send because the channel is quiescent is never lost: the final hop commits the set (the preimage on
/// the HTLC's record, the invoice settled) and a forward keeps its outgoing record, and the replay the quiescence
/// service runs when the quiescence ends sends the fulfill once.
/// </summary>
/// <remarks>
/// Only one end is made quiescent (the peer's <c>stfu</c> handed to its service directly): the other end's
/// <c>ChannelManager</c> has no <c>stfu</c> case before lane Q-A, and the switch only sees its own node's state.
/// </remarks>
public class QuiescenceSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    [Fact]
    public async Task Given_CarolQuiescentWhenSheWouldFulfill_When_TheQuiescenceEnds_Then_TheFulfillIsSentOnce()
    {
        // Arrange: the HTLC is locked in at Carol, whose channel to Bob is quiescent before her switch acts
        await using var harness = await ThreeNodeHarness.CreateAsync(WithQuiescence);
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "quiescent", null, ct);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        await MakeQuiescentAsync(harness.Carol, ThreeNodeHarness.BobCarolChannelId);

        // Act 1: the switch acts while quiescent: the fulfill is refused, the set is committed instead
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        var whileQuiescent = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                                    .GetByPaymentHashAsync(invoice.PaymentHash));
        var committed = Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs
                                                     .Values, h => h.Direction == HtlcDirection.Incoming);

        // Act 2: the dependent protocol ends the quiescence
        await TerminateAsync(harness.Carol, ThreeNodeHarness.BobCarolChannelId);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(InvoiceStatus.Settled, whileQuiescent!.Status);
        Assert.Equal(invoice.Preimage!.Value, committed.KnownPreimage);
        Assert.Null(committed.Removal);
        Assert.Single(harness.Sent, m => m is { From: "Carol", To: "Bob" } && m.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_BobQuiescentUpstreamWhenCarolFulfills_When_TheQuiescenceEnds_Then_BobFulfillsUpstreamOnce()
    {
        // Arrange: the HTLC reaches Carol; then Bob's channel to Alice turns quiescent
        await using var harness = await ThreeNodeHarness.CreateAsync(WithQuiescence);
        var ct = TestContext.Current.CancellationToken;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "forward", null, ct);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        await MakeQuiescentAsync(harness.Bob, ThreeNodeHarness.AliceBobChannelId);

        // Act 1: Carol fulfills; Bob learns the preimage but can't fulfill upstream
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        var upstreamWhileQuiescent = harness.Sent.Count(m => m is { From: "Bob", To: "Alice" }
                                                          && m.Message is UpdateFulfillHtlcMessage);
        var paidWhileQuiescent = harness.Alice.PaymentHandler.Fulfilled.Count;

        // Act 2: the quiescence ends
        await TerminateAsync(harness.Bob, ThreeNodeHarness.AliceBobChannelId);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(0, upstreamWhileQuiescent);
        Assert.Equal(0, paidWhileQuiescent);
        Assert.Single(harness.Sent, m => m is { From: "Bob", To: "Alice" } && m.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        AssertNoHtlcs(harness);
    }

    private static void WithQuiescence(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
        {
            node.Options.Features.OptionQuiesce = FeatureSupport.Optional;
            node.ConfigureServices = services => services.AddQuiescenceServices();
        }
    }

    /// <summary>The peer's <c>stfu</c> handed to the node's service; its reply is not delivered (see remarks).</summary>
    private static async Task MakeQuiescentAsync(SwitchNode node, ChannelId channelId)
    {
        var lockProvider = node.Services.GetRequiredService<IChannelLockProvider>();
        using (await lockProvider.AcquireAsync(channelId, TestContext.Current.CancellationToken))
            Assert.NotNull(node.Services.GetRequiredService<IQuiescenceService>()
                               .OnStfuReceived(node.Channel(channelId), new StfuPayload(channelId, true),
                                               QuiescenceTestPair.QuiesceFeatures));

        Assert.True(node.Services.GetRequiredService<IQuiescenceService>().GetState(channelId).IsQuiescent);
    }

    private static async Task TerminateAsync(SwitchNode node, ChannelId channelId)
    {
        var lockProvider = node.Services.GetRequiredService<IChannelLockProvider>();
        using (await lockProvider.AcquireAsync(channelId, TestContext.Current.CancellationToken))
            node.Services.GetRequiredService<IQuiescenceService>().Terminate(channelId, QuiescenceEndReason.TxAbort);

        await node.Services.GetRequiredService<QuiescenceService>().WhenIdleAsync();
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}