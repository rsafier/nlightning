using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Onchain.Resolvers;
using Application.Payments.Interception;
using Channels.Harness;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;

public partial class HtlcInterceptionParitySwitchTests
{
    [Fact]
    public async Task Given_AClaimedFailedCircuit_When_TheSwitchCannotStageIncome_Then_ItThrowsBeforeSaveAndReplayRetries()
    {
        // Arrange: exercise the actual switch writer without any live incoming channel or HTLC row.
        await using var harness = await CreateAsync();
        var preimage = new Secret(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var circuit = new ForwardCircuitModel(ThreeNodeHarness.AliceBobChannelId, 500,
            LightningMoney.MilliSatoshis(53_000), 900,
            new Hash(System.Security.Cryptography.SHA256.HashData((byte[])preimage)), preimage,
            ThreeNodeHarness.BobCarolScid, LightningMoney.MilliSatoshis(50_000), 850, DateTimeOffset.UtcNow);
        circuit.SetActualIncomingAmount(LightningMoney.MilliSatoshis(52_000));
        circuit.MarkIncomingClaimed(preimage);
        circuit.MarkFailed(DateTimeOffset.UtcNow);
        var staged = new List<AccountingEventModel>();
        var fail = true;
        var events = new Mock<IAccountingEventDbRepository>();
        events.Setup(e => e.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string key, CancellationToken _) => staged.Any(fact => fact.EventKey == key));
        events.Setup(e => e.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(fact =>
        {
            if (fail) throw new InvalidOperationException("accounting staging failed");
            staged.Add(fact);
        });
        var work = new Mock<IUnitOfWork>();
        work.SetupGet(u => u.AccountingEventDbRepository).Returns(events.Object);
        var writer = typeof(Application.Payments.Switch.HtlcSwitch).GetMethod("StageClaimedFailedForwardAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Task<bool> StageAsync() => (Task<bool>)writer.Invoke(harness.Bob.Services.GetRequiredService<Application.Payments.Switch.HtlcSwitch>(), [work.Object, circuit])!;

        // Act / Assert: failures propagate; the enclosing circuit save cannot run.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await StageAsync());
        work.Verify(u => u.SaveChangesAsync(), Times.Never);
        Assert.Empty(staged);
        fail = false;
        Assert.True(await StageAsync());
        Assert.False(await StageAsync());
        Assert.Equal(52_000, Assert.Single(staged).AmountMsat);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public async Task Given_AnOutgoingForwardInFlight_When_OnchainClaimRacesDownstream_Then_EachRealLegIsBookedOnce(
        bool claimFirst, bool downstreamFulfills, bool unloadIncoming)
    {
        // Arrange: the outgoing HTLC is committed, while Carol has not resolved it.
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "claim race", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var original = Assert.Single(client.Offered);
        await hub.ResolveAsync(original.IncomingShortChannelId, original.IncomingHtlcId, ForwardInterceptResolution.Resume);
        await harness.PumpAsync();
        var incoming = BobIncoming(harness);
        var incomingChannel = harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId);
        var incomingParams = incomingChannel.Commitments!.Params;
        harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).UpdateState(ChannelState.OnchainResolving);
        var decision = await harness.Bob.InScopeAsync(u => FinalHopClaims.GetFinalHopDecisionAsync(
            u, ThreeNodeHarness.AliceBobChannelId, incoming, 0));
        Assert.NotNull(decision);
        await harness.Bob.Switch.HandleAsync(decision!,
                                             TestContext.Current.CancellationToken);
        var claimOffer = client.Offered.Last();
        Assert.True(claimOffer.IsOnChain);
        Assert.Equal(2, client.Offered.Count);

        async Task ClaimAsync()
        {
            Assert.Equal(InterceptResolveResult.Resolved,
                await hub.ResolveAsync(claimOffer.IncomingShortChannelId, claimOffer.IncomingHtlcId,
                    new ForwardInterceptResolution(ForwardInterceptAction.Settle, invoice.Preimage!.Value)));
        }

        // Act: resolve through Carol's real channel engine in either order.
        if (claimFirst)
        {
            await ClaimAsync();
            Assert.Empty(await harness.Bob.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000)));
        }
        if (unloadIncoming)
        {
            // The claim has been made; closed channel memory no longer provides the incoming HTLC.
            incomingChannel.UpdateState(ChannelState.Closed);
            await harness.Bob.InScopeAsync(async u =>
            {
                await u.ChannelDbRepository.UpdateAsync(incomingChannel);
                // Model the incoming claim's completed row lifetime before the still-pending outgoing failure.
                var claimed = incomingChannel.Commitments!.Htlcs.Values.Single(h => h.Direction == HtlcDirection.Incoming);
                await u.ChannelStateDbRepository.ApplyAsync(incomingChannel.Commitments,
                    new ChannelTransition([], [claimed with { State = HtlcState.SentRemoveAckRevocation, Removal = HtlcRemoval.Fulfill(invoice.Preimage!.Value) }], [],
                                          false, false, false, false));
                await u.SaveChangesAsync();
                await u.ChannelStateDbRepository.PruneSettledHtlcsAsync(ThreeNodeHarness.AliceBobChannelId,
                    [new HtlcKey(HtlcDirection.Incoming, incoming.Id)]);
                await u.SaveChangesAsync();
                var remaining = await u.ChannelStateDbRepository.LoadAsync(ThreeNodeHarness.AliceBobChannelId, incomingParams);
                Assert.DoesNotContain(remaining!.Commitments.Htlcs.Values, h => h.Direction == HtlcDirection.Incoming);
                Assert.DoesNotContain(remaining.SettledHtlcs, h => h.Direction == HtlcDirection.Incoming);
                return true;
            });
            Assert.True(harness.Bob.Services.GetRequiredService<IChannelMemoryRepository>()
                               .TryRemoveChannel(ThreeNodeHarness.AliceBobChannelId));
        }
        if (!downstreamFulfills)
            Assert.True(await harness.Carol.Invoices.CancelInvoiceAsync(invoice.PaymentHash,
                                                                         TestContext.Current.CancellationToken));
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        if (!claimFirst)
            await ClaimAsync();

        // Assert: forwarding earns only the actual fee; failed outgoing plus claimed incoming earns its full amount.
        var circuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
            ThreeNodeHarness.AliceBobChannelId, incoming.Id));
        Assert.Equal(downstreamFulfills ? ForwardCircuitStatus.Fulfilled : ForwardCircuitStatus.Failed, circuit!.Status);
        var fact = Assert.Single(await harness.Bob.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000)));
        Assert.Equal(downstreamFulfills ? AccountingEventKind.ForwardSettled : AccountingEventKind.InterceptedHtlcSettled,
                     fact.Kind);
        Assert.Equal((long)(downstreamFulfills ? circuit.ActualFee : circuit.ActualIncomingAmount).MilliSatoshi,
                     fact.AmountMsat);
        Assert.Equal(invoice.Preimage!.Value, circuit.IncomingClaimedPreimage);
        var stored = unloadIncoming
            ? incomingChannel.Commitments!.Htlcs.Values.Single(h => h.Direction == HtlcDirection.Incoming)
            : await harness.Bob.InScopeAsync(async u =>
                (await u.ChannelStateDbRepository.LoadAsync(ThreeNodeHarness.AliceBobChannelId, incomingParams))!
                .Commitments.Htlcs.Values.Single(h => h.Direction == HtlcDirection.Incoming));
        Assert.Equal(invoice.Preimage!.Value, stored.KnownPreimage);
        Assert.Equal((byte[])invoice.Preimage.Value, await harness.Bob.InScopeAsync(u =>
            InterceptorClaims.GetSettledPreimageAsync(u, ThreeNodeHarness.AliceBobChannelId, stored)));

        // Persist the closing state and restart against the same SQLite database before replay.
        await harness.Bob.InScopeAsync(async u =>
        {
            await u.ChannelDbRepository.UpdateAsync(incomingChannel);
            await u.SaveChangesAsync();
            return true;
        });
        connection.Dispose();
        await harness.RestartAsync(harness.Bob);
        var restoredCircuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
            ThreeNodeHarness.AliceBobChannelId, incoming.Id));
        Assert.Equal(invoice.Preimage.Value, restoredCircuit!.IncomingClaimedPreimage);
        var restored = unloadIncoming ? stored : await harness.Bob.InScopeAsync(async u =>
            (await u.ChannelStateDbRepository.LoadAsync(ThreeNodeHarness.AliceBobChannelId, incomingParams))!
            .Commitments.Htlcs.Values.Single(h => h.Direction == HtlcDirection.Incoming));
        Assert.Equal(invoice.Preimage.Value, restored.KnownPreimage);

        // Replay of the durable circuit and incoming preimage must not add a second accounting event.
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, restored),
                                             TestContext.Current.CancellationToken);
        Assert.Single(await harness.Bob.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000)));
    }

    [Fact]
    public async Task Given_AnAmountOverrideExceedingDustExposure_When_ResumedModified_Then_ItFailsBeforeForwarding()
    {
        // Arrange: the real incoming HTLC is not dust; its interpreted replacement would exceed the limit.
        await using var harness = await CreateAsync(maxDustMsat: 2_000);
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "dust override", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);

        // Act: outgoing 1,000 msat fits the channel limit, incoming interpretation 3,000 msat does not.
        await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
            ForwardInterceptResolution.Modified(LightningMoney.MilliSatoshis(3_000),
                                                  LightningMoney.MilliSatoshis(1_000), null));
        await harness.PumpAsync();

        // Assert
        Assert.Null(await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
            ThreeNodeHarness.AliceBobChannelId, offered.IncomingHtlcId)));
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        Assert.Single(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_AnUnsupportedMandatoryWireRecord_When_ResumedModified_Then_NoCircuitOrOutgoingAddIsPersisted()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "mandatory record", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);

        // Act: this peer has not negotiated an auxiliary channel protocol for this even wire TLV.
        await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
            ForwardInterceptResolution.Modified(null, null, [new CustomRecord(65_536, [0xCA, 0xFE])]));
        await harness.PumpAsync();

        // Assert: refusing before persistence prevents restart retransmission from closing the downstream channel.
        Assert.Null(await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
            ThreeNodeHarness.AliceBobChannelId, offered.IncomingHtlcId)));
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        Assert.Single(harness.Alice.PaymentHandler.Failed);
    }
}