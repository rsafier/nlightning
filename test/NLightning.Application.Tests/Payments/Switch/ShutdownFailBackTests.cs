using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Close;
using Application.Channels.Close.Handlers;
using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Payments.Routing;
using Channels.Harness;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-279 (BOLT 2 B2-SHUT-S08, "SHOULD fail to route any HTLC added after it has sent shutdown"): on
/// <see cref="ThreeNodeHarness"/> (production channel managers, <c>HtlcSwitch</c> and close coordinator, real onions,
/// SQLite), Bob sends <c>shutdown</c> on the Alice-Bob channel while Alice, who has not received it yet, adds an HTLC:
/// Bob accepts the add into the commitments (Alice broke no rule) and then fails it back with
/// <c>temporary_node_failure</c> instead of forwarding it to Carol or accepting it as final hop, and the close goes on.
/// An HTLC Alice added before Bob's <c>shutdown</c> is still routed.
/// </summary>
public class ShutdownFailBackTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);
    private static readonly BitcoinScript s_aliceScript = P2Wpkh(0xA1);
    private static readonly BitcoinScript s_bobScript = P2Wpkh(0xB0);
    private static readonly BitcoinScript s_carolScript = P2Wpkh(0xC0);

    [Fact]
    public async Task Given_BobSentShutdown_When_AliceAddsAForwardBeforeSeeingIt_Then_BobFailsItBackAndNeverForwards()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync(WithCloseServices);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "after shutdown", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        await BobClosesAliceBobAsync(harness);

        // Act: Alice's add crosses Bob's shutdown on the wire
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Bob (hop 0) failed it back; nothing went to Carol
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" }
                                               && m.Message is UpdateAddHtlcMessage);
        Assert.Contains(harness.Sent, m => m is { From: "Bob", To: "Alice" } && m.Message is UpdateFailHtlcMessage);
        Assert.Null(await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                                       ThreeNodeHarness.AliceBobChannelId, 0)));
        Assert.Equal(InvoiceStatus.Open,
                     (await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash)))!.Status);

        // The boundary was persisted with Bob's shutdown, and the close went on once the HTLC was gone
        var bobChannel = harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId);
        Assert.Equal(0UL, bobChannel.FirstRemoteHtlcIdAfterLocalShutdown);
        Assert.Equal(0UL, (await StoredChannelAsync(harness.Bob))!.FirstRemoteHtlcIdAfterLocalShutdown);
        Assert.True(bobChannel.State >= ChannelState.Negotiating, $"Bob's channel is {bobChannel.State}");
        Assert.True(harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).State >= ChannelState.Negotiating);
        Assert.Empty(bobChannel.Commitments!.Htlcs);
    }

    [Fact]
    public async Task Given_BobSentShutdown_When_AlicePaysBobsInvoiceBeforeSeeingIt_Then_BobFailsItBackAndKeepsTheInvoiceOpen()
    {
        // Arrange: a shutdown means no new HTLC is accepted either (BOLT 2 rationale)
        await using var harness = await ThreeNodeHarness.CreateAsync(WithCloseServices);
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "to bob", null,
                                                                    TestContext.Current.CancellationToken);
        var finalCltv = ThreeNodeHarness.BlockHeight + 43;
        var route = new PaymentRoute([new RouteHop(harness.Bob.NodeId, s_amount, finalCltv, null)], s_amount,
                                     finalCltv, invoice.PaymentHash, invoice.PaymentSecret);
        await BobClosesAliceBobAsync(harness);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(InvoiceStatus.Open,
                     (await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(invoice.PaymentHash)))!.Status);
        Assert.True(harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).State >= ChannelState.Negotiating);
    }

    [Fact]
    public async Task Given_AnHtlcAddedBeforeBobsShutdown_When_ItIsHandledAfterIt_Then_BobStillForwardsIt()
    {
        // Arrange: Alice's HTLC 0 is locked in at Bob, whose switch has not acted on it yet, when Bob sends shutdown
        await using var harness = await ThreeNodeHarness.CreateAsync(WithCloseServices);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "before shutdown", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        harness.Bob.SwitchSuspended = true;
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();
        Assert.Contains(harness.Bob.Events, e => e is IncomingHtlcLockedIn);
        await BobClosesAliceBobAsync(harness);
        await harness.PumpAsync();

        // Act
        harness.Bob.SwitchSuspended = false;
        await harness.Bob.ReplayPendingEventsAsync();
        await harness.PumpAsync();

        // Assert: added before the shutdown (id 0 < boundary 1), so routed and paid; then the close went on
        Assert.Equal(1UL, harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).FirstRemoteHtlcIdAfterLocalShutdown);
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage!.Value, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Contains(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        Assert.True(harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).State >= ChannelState.Negotiating);
    }

    [Fact]
    public async Task Given_BobRestartedAfterHisShutdown_When_AliceAddsAnHtlc_Then_ThePersistedBoundaryStillFailsItBack()
    {
        // Arrange: Bob's shutdown is persisted, then Bob restarts (the queued shutdown is lost with the link; the
        // harness has no reestablish to send it again, so Alice still has not seen it) and the boundary comes back
        // from his database
        await using var harness = await ThreeNodeHarness.CreateAsync(WithCloseServices);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "after restart", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        await BobClosesAliceBobAsync(harness);
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        Assert.Equal(0UL, harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).FirstRemoteHtlcIdAfterLocalShutdown);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" }
                                               && m.Message is UpdateAddHtlcMessage);
        Assert.Empty(harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.Htlcs);
    }

    [Theory]
    [InlineData(ChannelState.Failed)]
    [InlineData(ChannelState.OnchainResolving)]
    public async Task Given_AFinalHopHtlcAddedAfterBobsShutdown_When_TheChannelGoesOnChainBeforeTheSwitchActs_Then_ClaimedOnChainNotFailedBack(
        ChannelState closedState)
    {
        // Arrange: Alice's payment to Bob crosses his shutdown, and the channel goes on chain before Bob's switch acts
        await using var harness = await ThreeNodeHarness.CreateAsync(WithCloseServices);
        var invoice = await harness.Bob.Invoices.CreateInvoiceAsync(s_amount, "to bob on chain", null,
                                                                    TestContext.Current.CancellationToken);
        var finalCltv = ThreeNodeHarness.BlockHeight + 43;
        var route = new PaymentRoute([new RouteHop(harness.Bob.NodeId, s_amount, finalCltv, null)], s_amount,
                                     finalCltv, invoice.PaymentHash, invoice.PaymentSecret);
        await BobClosesAliceBobAsync(harness);
        harness.Bob.SwitchSuspended = true;
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();
        harness.Bob.SwitchSuspended = false;
        var bobChannel = harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId);
        var incoming = Assert.Single(bobChannel.Commitments!.Htlcs.Values, h => h.Direction == HtlcDirection.Incoming);
        Assert.True(bobChannel.IsRemoteHtlcAddedAfterLocalShutdown(incoming.Id));
        bobChannel.UpdateState(closedState);
        var sentBefore = harness.Sent.Count;

        // Act: the resolver hands it to the switch (twice, as it does every round while the invoice is Open)
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, incoming),
                                             CancellationToken.None);
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, incoming),
                                             CancellationToken.None);
        await harness.PumpAsync();

        // Assert: no failure attempted (the channel cannot carry one), the preimage committed for the on-chain claim
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(invoice.Preimage!.Value,
                     bobChannel.Commitments!.GetHtlc(HtlcDirection.Incoming, incoming.Id)!.KnownPreimage);
        Assert.Equal(InvoiceStatus.Settled,
                     (await harness.Bob.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(invoice.PaymentHash)))!.Status);
    }

    /// <summary>Bob closes the Alice-Bob channel: his <c>shutdown</c> is persisted and queued, not delivered.</summary>
    private static async Task BobClosesAliceBobAsync(ThreeNodeHarness harness)
    {
        var result = await harness.Bob.Services.GetRequiredService<IChannelCloseService>()
                                  .CloseChannelAsync(ThreeNodeHarness.AliceBobChannelId, new ChannelCloseRequest(),
                                                     TestContext.Current.CancellationToken);
        Assert.Equal(ChannelState.ShuttingDown, result.State);
    }

    private static Task<ChannelModel?> StoredChannelAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(ThreeNodeHarness.AliceBobChannelId));

    /// <summary>The mutual close on every node: a fixed shutdown script, a fixed fee estimate and the handlers.</summary>
    private static void WithCloseServices(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
        {
            var script = node.Name switch
            {
                "Alice" => s_aliceScript,
                "Bob" => s_bobScript,
                _ => s_carolScript
            };
            node.ConfigureServices = services =>
            {
                var feeService = new Mock<IFeeService>();
                feeService.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Satoshis(2_500));
                feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(LightningMoney.Satoshis(2_500));
                services.Replace(ServiceDescriptor.Singleton(feeService.Object));
                services.AddChannelCloseServices();
                services.Replace(ServiceDescriptor.Scoped<ShutdownScriptProvider>(
                                     _ => new FixedShutdownScriptProvider(script)));
                services.AddScoped<IChannelMessageHandler<ShutdownMessage>, ShutdownMessageHandler>();
                services.AddScoped<IChannelMessageHandler<ClosingSignedMessage>, ClosingSignedMessageHandler>();
                services.AddScoped<IChannelMessageHandler<ClosingCompleteMessage>, ClosingCompleteMessageHandler>();
                services.AddScoped<IChannelMessageHandler<ClosingSigMessage>, ClosingSigMessageHandler>();
            };
        }
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        var decrypted = service.DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static BitcoinScript P2Wpkh(byte fill) => new([0x00, 0x14, .. Enumerable.Repeat(fill, 20)]);

    /// <summary>A fixed shutdown script instead of a wallet address.</summary>
    private sealed class FixedShutdownScriptProvider(BitcoinScript script)
        : ShutdownScriptProvider(Options.Create(new NodeOptions()), new Mock<IBitcoinWalletService>().Object)
    {
        public override Task<BitcoinScript> GetLocalScriptAsync(ChannelModel channel) => Task.FromResult(script);
    }
}