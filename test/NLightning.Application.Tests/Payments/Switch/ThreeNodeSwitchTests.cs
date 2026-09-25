using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Channels.Harness;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;

/// <summary>
/// ABCD W2-B lane proof: <c>HtlcSwitch</c> on three in-process nodes Alice → Bob → Carol with real Sphinx onions,
/// real error onions, real commitment signatures and SQLite persistence (<see cref="ThreeNodeHarness"/>).
/// </summary>
public class ThreeNodeSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    [Fact]
    public async Task Given_InvoiceAtCarol_When_AlicePaysThroughBob_Then_FulfillPropagatesUpstreamImmediately()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "coffee", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var fee = ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, s_amount);
        var before = Balances(harness);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Alice learnt Carol's preimage
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

        // Bob fulfilled upstream as soon as Carol revealed the preimage, before the downstream removal was committed
        var upstreamFulfill = Assert.Single(harness.Sent, s => s is { From: "Bob", To: "Alice" }
                                                            && s.Message is UpdateFulfillHtlcMessage);
        Assert.NotNull(upstreamFulfill.SenderStates[ThreeNodeHarness.BobCarolChannelId]
                                      .GetHtlc(HtlcDirection.Outgoing, 0));

        // Balances moved by the amount and Bob earned his fee; nothing is pending anywhere
        var after = Balances(harness);
        Assert.Equal(before.AliceAb - (s_amount + fee).MilliSatoshi, after.AliceAb);
        Assert.Equal(before.BobAb + (s_amount + fee).MilliSatoshi, after.BobAb);
        Assert.Equal(before.BobBc - s_amount.MilliSatoshi, after.BobBc);
        Assert.Equal(before.CarolBc + s_amount.MilliSatoshi, after.CarolBc);
        AssertNoHtlcs(harness);
        AssertCommitmentNumbersMirror(harness);

        // Carol's invoice is settled, Bob's circuit fulfilled, and every archived row pruned
        var storedInvoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                                   .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, storedInvoice!.Status);
        Assert.Equal(s_amount, storedInvoice.AmountReceived);
        var circuit = await GetCircuitAsync(harness, 0);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit!.Status);
        Assert.Equal(fee, circuit.Fee);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, circuit.OutgoingChannelId);
        Assert.Empty(await SettledRowsAsync(harness.Bob, ThreeNodeHarness.BobCarolChannelId));
        Assert.Empty(await SettledRowsAsync(harness.Alice, ThreeNodeHarness.AliceBobChannelId));
        AssertNeverTwoLocks(harness);
    }

    [Fact]
    public async Task Given_UnknownPaymentHash_When_CarolFails_Then_AliceDecodesItFromHopOneAfterTheIrrevocableRemoval()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var paymentHash = ThreeNodeHarness.Sha256Of(Enumerable.Repeat((byte)0x33, 32).ToArray());
        var route = harness.RouteToCarol(s_amount, paymentHash, new Secret(Enumerable.Repeat((byte)7, 32).ToArray()));
        var before = Balances(harness);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Carol's failure, wrapped by Bob, is read at Alice from hop index 1
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Equal(s_amount, decrypted.Message!.HtlcAmount);
        Assert.Equal(ThreeNodeHarness.BlockHeight, decrypted.Message.Height);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);

        // Bob failed upstream only once his downstream HTLC was irrevocably removed (gone from the commitments)
        var upstreamFail = Assert.Single(harness.Sent, s => s is { From: "Bob", To: "Alice" }
                                                         && s.Message is UpdateFailHtlcMessage);
        Assert.Null(upstreamFail.SenderStates[ThreeNodeHarness.BobCarolChannelId]
                                .GetHtlc(HtlcDirection.Outgoing, 0));
        var downstreamRevoke = harness.Sent.FindLastIndex(s => s is { From: "Carol", To: "Bob" }
                                                             && s.Message is RevokeAndAckMessage);
        Assert.True(harness.Sent.IndexOf(upstreamFail) > downstreamRevoke);

        Assert.Equal(before, Balances(harness));
        AssertNoHtlcs(harness);
        Assert.Equal(ForwardCircuitStatus.Failed, (await GetCircuitAsync(harness, 0))!.Status);
        Assert.Empty(await SettledRowsAsync(harness.Bob, ThreeNodeHarness.BobCarolChannelId));
        AssertNeverTwoLocks(harness);
    }

    [Fact]
    public async Task Given_OnionCarolCannotPeel_When_CarolFailsMalformed_Then_BobConvertsItToUpdateFailHtlc()
    {
        // Arrange: Carol's layer is encrypted to another key, so her HMAC check fails
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "malformed", null,
                                                                      TestContext.Current.CancellationToken);
        var stranger = new HarnessKeyManager(0x5E).NodeId;
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret, payee: stranger);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Carol answered update_fail_malformed_htlc, Bob sent Alice an update_fail_htlc instead
        var malformed = Assert.IsType<UpdateFailMalformedHtlcMessage>(
            Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                          && s.Message is UpdateFailMalformedHtlcMessage).Message);
        Assert.Equal((ushort)FailureCode.InvalidOnionHmac, malformed.Payload.FailureCode);
        Assert.DoesNotContain(harness.Sent, s => s is { From: "Bob", To: "Alice" }
                                              && s.Message is UpdateFailMalformedHtlcMessage);
        Assert.Single(harness.Sent, s => s is { From: "Bob", To: "Alice" } && s.Message is UpdateFailHtlcMessage);

        // Bob is the erring node for Alice; the data is the sha256 of the onion Bob sent to Carol
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionHmac, decrypted.Code);
        var forwardedAdd = Assert.IsType<UpdateAddHtlcMessage>(
            Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.Equal((byte[])ThreeNodeHarness.Sha256Of(forwardedAdd.Payload.OnionRoutingPacket.Span),
                     decrypted.Message!.Sha256OfOnion!.Value.ToArray());
        AssertNoHtlcs(harness);
        AssertNeverTwoLocks(harness);
    }

    [Fact]
    public async Task Given_FeeBelowBobsPolicy_When_Forwarding_Then_FeeInsufficientCarriesBobsSignedChannelUpdate()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "cheap", null,
                                                                      TestContext.Current.CancellationToken);
        var fair = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var cheap = new PaymentRoute(fair.Hops, fair.FirstHopAmount - LightningMoney.MilliSatoshis(1),
                                     fair.FirstHopCltvExpiry, fair.PaymentHash, fair.PaymentSecret);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(cheap);
        await harness.PumpAsync();

        // Assert: fee_insufficient from Bob with the incoming amount and his signed update of the Bob-Carol channel
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.FeeInsufficient, decrypted.Code);
        Assert.Equal(cheap.FirstHopAmount, decrypted.Message!.HtlcAmount);
        Assert.True(FailureChannelUpdateFactory.TryGetChannelUpdate(decrypted.Message.ChannelUpdate!.Value,
                                                                    out var update));
        Assert.Equal(ThreeNodeHarness.BobCarolScid, update.Payload.ShortChannelId);
        Assert.Equal(ThreeNodeHarness.BobRouting.FeeBaseMsat, update.Payload.FeeBaseMsat);
        var signer = harness.Alice.Services.GetRequiredService<ILightningSigner>();
        Assert.True(signer.VerifyNodeMessage(update.Payload.GetSignatureHash(), update.Payload.Signature,
                                             harness.Bob.NodeId));

        // Nothing was forwarded and no circuit was written
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Null(await GetCircuitAsync(harness, 0));
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_BobRestartsAfterDownstreamFulfill_When_Replayed_Then_UpstreamFulfilledExactlyOnce()
    {
        // Arrange: the HTLC reaches Carol, who does not settle yet
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "restart", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        Assert.Equal(ForwardCircuitStatus.Offered, (await GetCircuitAsync(harness, 0))!.Status);

        // Carol settles; Bob persists every downstream step but "crashes" before his switch acts on them
        harness.Bob.SwitchSuspended = true;
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        Assert.Contains(harness.Bob.Events, e => e is OutgoingHtlcFulfilled);
        Assert.Contains(harness.Bob.Events, e => e is OutgoingHtlcSettled);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Null(harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!
                           .GetHtlc(HtlcDirection.Outgoing, 0));

        // Act: Bob restarts on his SQLite database (startup replay while no link is up), then reconnects
        harness.Bob.SwitchSuspended = false;
        await harness.RestartAsync(harness.Bob);
        Assert.Single(await SettledRowsAsync(harness.Bob, ThreeNodeHarness.BobCarolChannelId));
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();

        // Assert: no lost fulfill
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        AssertNoHtlcs(harness);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness, 0))!.Status);
        Assert.Empty(await SettledRowsAsync(harness.Bob, ThreeNodeHarness.BobCarolChannelId));

        // No double fulfill: replay again, and restart again
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();
        Assert.Single(harness.Sent, s => s is { From: "Bob", To: "Alice" } && s.Message is UpdateFulfillHtlcMessage);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        AssertCommitmentNumbersMirror(harness);
        AssertNeverTwoLocks(harness);
    }

    [Fact]
    public async Task Given_BobRestartsBeforeActingOnTheLockIn_When_Replayed_Then_HeForwardsOnceAndThePaymentSucceeds()
    {
        // Arrange: Alice's HTLC is locked in at Bob, whose switch "crashes" before acting on it
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "late", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Bob.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        Assert.Contains(harness.Bob.Events, e => e is IncomingHtlcLockedIn);
        Assert.Null(await GetCircuitAsync(harness, 0));

        // Act
        harness.Bob.SwitchSuspended = false;
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness, 0))!.Status);
        AssertNoHtlcs(harness);
        AssertNeverTwoLocks(harness);
    }

    [Fact]
    public async Task Given_LockInReplayedDuringTheForward_When_Handled_Then_NothingIsOfferedTwice()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "twice", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();

        // Act: the pending events of Bob are delivered again (as after a reestablish)
        await harness.Bob.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_PendingCircuitWithoutOutgoingHtlc_When_Replayed_Then_UpstreamFailsWithTemporaryChannelFailure()
    {
        // Arrange: Bob "crashed" after saving the Pending circuit and before the offer was persisted
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "lost offer", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Bob.SwitchSuspended = true;
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();
        var incoming = harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!
                              .GetHtlc(HtlcDirection.Incoming, 0)!;
        await harness.Bob.InScopeAsync(async unitOfWork =>
        {
            await unitOfWork.ForwardCircuitDbRepository.AddAsync(
                new ForwardCircuitModel(ThreeNodeHarness.AliceBobChannelId, 0,
                                        LightningMoney.MilliSatoshis(incoming.AmountMsat), incoming.CltvExpiry,
                                        incoming.PaymentHash, onion.SharedSecrets[0], ThreeNodeHarness.BobCarolScid,
                                        s_amount, route.Hops[0].OutgoingCltvValue, DateTimeOffset.UtcNow));
            await unitOfWork.SaveChangesAsync();
            return true;
        });

        // Act
        harness.Bob.SwitchSuspended = false;
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();

        // Assert: failed back from Bob with his channel_update, never offered to Carol
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
        Assert.True(FailureChannelUpdateFactory.TryGetChannelUpdate(decrypted.Message!.ChannelUpdate!.Value,
                                                                    out var update));
        Assert.Equal(ThreeNodeHarness.BobCarolScid, update.Payload.ShortChannelId);
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Equal(ForwardCircuitStatus.Failed, (await GetCircuitAsync(harness, 0))!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_TwoHtlcsForOneInvoice_When_HandledConcurrently_Then_OnlyOneIsFulfilled()
    {
        // Arrange: two payments of the same invoice locked in at Carol before her switch acts (NL-253)
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "once", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var (firstId, firstOnion) = await harness.AlicePaysAsync(route);
        var (_, secondOnion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();
        var lockIns = harness.Carol.Events.OfType<IncomingHtlcLockedIn>().ToList();
        Assert.Equal(2, lockIns.Count);

        // Act
        harness.Carol.SwitchSuspended = false;
        await Task.WhenAll(lockIns.Select(e => Task.Run(() => harness.Carol.Switch.HandleAsync(
                                                            e, TestContext.Current.CancellationToken),
                                                        TestContext.Current.CancellationToken)));
        await harness.PumpAsync();

        // Assert: one fulfilled, the other failed from Carol with incorrect_or_unknown_payment_details
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var onion = failed.HtlcId == firstId ? firstOnion : secondOnion;
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_InvoiceAcceptedButFulfillNeverPersisted_When_Replayed_Then_TheHtlcIsFulfilled()
    {
        // Arrange: Carol "crashed" between saving Accepted and persisting the fulfill
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "crash window", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        await harness.Carol.InScopeAsync(async unitOfWork =>
        {
            var stored = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash);
            stored!.Accept(s_amount);
            await unitOfWork.InvoiceDbRepository.UpdateAsync(stored);
            await unitOfWork.SaveChangesAsync();
            return true;
        });

        // Act
        harness.Carol.SwitchSuspended = false;
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        var settled = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                            .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, settled!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_NestedChannelLocksInOneFlow_When_Audited_Then_TheViolationIsRecorded()
    {
        // Arrange - the audit behind AssertNeverTwoLocks must catch a second lock taken in the same flow
        var audit = new LockAudit();
        LockAudit.BeginFlow();

        // Act
        using (await audit.AcquireAsync(ThreeNodeHarness.AliceBobChannelId, TestContext.Current.CancellationToken))
        using (await audit.AcquireAsync(ThreeNodeHarness.BobCarolChannelId, TestContext.Current.CancellationToken))
        {
        }

        // Assert
        Assert.Single(audit.Violations);
        Assert.Equal(2, audit.MaxHeldInOneFlow);
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        var decrypted = service.DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static Task<ForwardCircuitModel?> GetCircuitAsync(ThreeNodeHarness harness, ulong incomingHtlcId) =>
        harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                     ThreeNodeHarness.AliceBobChannelId, incomingHtlcId));

    private static async Task<IReadOnlyList<HtlcRecord>> SettledRowsAsync(SwitchNode node, ChannelId channelId)
    {
        var channel = node.Channel(channelId);
        var persisted = await node.InScopeAsync(u => u.ChannelStateDbRepository.LoadAsync(
                                                    channelId, channel.Commitments!.Params));
        return persisted!.SettledHtlcs;
    }

    private static BalanceSheet Balances(ThreeNodeHarness harness) =>
        new(harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat,
         harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat,
         harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.LocalBalanceMsat,
         harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.LocalBalanceMsat);

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }

    private static void AssertCommitmentNumbersMirror(ThreeNodeHarness harness)
    {
        foreach (var (a, b, channelId) in new[]
                 {
                     (harness.Alice, harness.Bob, ThreeNodeHarness.AliceBobChannelId),
                     (harness.Bob, harness.Carol, ThreeNodeHarness.BobCarolChannelId)
                 })
        {
            var left = a.Channel(channelId).Commitments!;
            var right = b.Channel(channelId).Commitments!;
            Assert.Equal(left.LocalCommit.Number, right.RemoteCommit.Number);
            Assert.Equal(left.RemoteCommit.Number, right.LocalCommit.Number);
        }
    }

    private static void AssertNeverTwoLocks(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
        {
            Assert.Empty(node.LockAudit.Violations);
            Assert.Equal(1, node.LockAudit.MaxHeldInOneFlow);
        }
    }

    private readonly record struct BalanceSheet(ulong AliceAb, ulong BobAb, ulong BobBc, ulong CarolBc);
}