using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments;
using Application.Payments.Routing;
using Channels.Harness;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Events;

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
        await AssertNoSettledRowsAsync(harness);
        AssertNeverTwoLocks(harness);

        // NL-602 A2 (the books): Carol's and Bob's channels account moved exactly as their live balances. Alice offered
        // her HTLC through the harness, not her payment service, so she has no payment event to book.
        var carolBooks = BooksSimulator.Of(await AccountingEventsAsync(harness.Carol));
        Assert.Equal((long)after.CarolBc - (long)before.CarolBc, carolBooks[AccountRole.Channels]);
        Assert.Equal(-(long)s_amount.MilliSatoshi, carolBooks[AccountRole.Received]);
        var bobBooks = BooksSimulator.Of(await AccountingEventsAsync(harness.Bob));
        Assert.Equal((long)after.BobAb - (long)before.BobAb + ((long)after.BobBc - (long)before.BobBc),
                     bobBooks[AccountRole.Channels]);
        Assert.Equal(-(long)fee.MilliSatoshi, bobBooks[AccountRole.Routing]);
    }

    [Fact]
    public async Task Given_ASubscriberAtCarol_When_AlicePaysThroughBob_Then_CarolPublishesTheSettleOnce()
    {
        // Arrange - Cashu plan C0 (NL-901): the settle is published after its save, once per invoice
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "coffee", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        using var carolEvents = harness.Carol.Services.GetRequiredService<IPaymentEventSource>().Subscribe();
        using var bobEvents = harness.Bob.Services.GetRequiredService<IPaymentEventSource>().Subscribe();

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Carol's invoice is Settled when the event arrives; Bob only forwarded, so he publishes nothing
        var settled = Assert.IsType<InvoiceSettledEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents));
        Assert.Equal(invoice.PaymentHash, settled.PaymentHash);
        Assert.Equal(s_amount, settled.Amount);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                            .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
        harness.Carol.Services.GetRequiredService<IPaymentEventPublisher>()
               .Publish(new PaymentFailedEvent(invoice.PaymentHash, "marker", DateTimeOffset.UnixEpoch));
        Assert.IsType<PaymentFailedEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents));
        Assert.False(bobEvents.Overflowed);
    }

    [Fact]
    public async Task Given_InvoiceAtCarol_When_AlicePaysThroughBob_Then_CarolRecordsTheSettleAndBobTheForwardFee()
    {
        // Arrange - NL-602: the settle and the circuit's fulfill each stage one accounting event in their own save
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "coffee", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var fee = ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, s_amount);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Carol's invoice, settled for what the HTLC carried, from Bob over the Bob-Carol channel
        var settled = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal(AccountingEventKind.InvoiceSettled, settled.Kind);
        Assert.Equal(AccountingEventKeys.InvoiceSettled(invoice.PaymentHash), settled.EventKey);
        Assert.Equal((long)s_amount.MilliSatoshi, settled.AmountMsat);
        Assert.Equal(0, settled.FeeMsat);
        Assert.Equal(AccountingFinality.Final, settled.Finality);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, settled.ChannelId);
        Assert.Equal(ThreeNodeHarness.BobCarolScid, settled.ShortChannelId);
        Assert.Equal(harness.Bob.NodeId, settled.Counterparty);
        Assert.Equal(invoice.PaymentHash, settled.PaymentHash);
        Assert.Equal(ThreeNodeHarness.BlockHeight, settled.BlockHeight);
        Assert.Equal("bolt11", settled.Details["kind"]);
        Assert.Equal("coffee", settled.Details["description"]);
        Assert.Equal("1", settled.Details["parts"]);
        Assert.Equal("fulfill", settled.Details["settledBy"]);
        Assert.False(settled.Details.ContainsKey("requestedMsat"));
        var storedInvoice = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                                   .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(storedInvoice!.SettledAt, settled.OccurredAt);

        // Bob earned the fee on the Alice-Bob channel; the amounts in and out are in the details
        var forward = Assert.Single(await AccountingEventsAsync(harness.Bob));
        Assert.Equal(AccountingEventKind.ForwardSettled, forward.Kind);
        Assert.Equal(AccountingEventKeys.ForwardSettled(ThreeNodeHarness.AliceBobChannelId, 0), forward.EventKey);
        Assert.Equal((long)fee.MilliSatoshi, forward.AmountMsat);
        Assert.Equal(0, forward.FeeMsat);
        Assert.Equal(ThreeNodeHarness.AliceBobChannelId, forward.ChannelId);
        Assert.Equal(ThreeNodeHarness.AliceBobScid, forward.ShortChannelId);
        Assert.Equal(harness.Alice.NodeId, forward.Counterparty);
        Assert.Equal(invoice.PaymentHash, forward.PaymentHash);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId.ToString(), forward.Details["outgoingChannelId"]);
        Assert.Equal(ThreeNodeHarness.BobCarolScid.ToString(), forward.Details["outgoingScid"]);
        Assert.Equal((s_amount + fee).MilliSatoshi.ToString(), forward.Details["incomingAmountMsat"]);
        Assert.Equal(s_amount.MilliSatoshi.ToString(), forward.Details["outgoingAmountMsat"]);
        Assert.Equal("0", forward.Details["outgoingHtlcId"]);

        // Alice's payment goes through her payment service (a recorder here): nothing at the switch
        Assert.Empty(await AccountingEventsAsync(harness.Alice));
    }

    [Theory]
    [InlineData(FeatureSupport.No, false)]
    [InlineData(FeatureSupport.Optional, false)]
    [InlineData(FeatureSupport.Optional, true)]
    [InlineData(FeatureSupport.Compulsory, true)]
    public async Task Given_ScidAliasUse_When_TheOnionNamesAnAcceptedScid_Then_BobForwardsAndCarolIsPaid(
        FeatureSupport scidAlias, bool byAlias)
    {
        // Arrange: NL-348, a public channel that negotiated option_scid_alias without it in its channel type
        // (Optional) is announced by its real scid, so Bob must forward by it as well as by his alias
        await using var harness = await ThreeNodeHarness.CreateAsync(bobCarolScidAlias: scidAlias);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "coffee", null,
                                                                      TestContext.Current.CancellationToken);
        var scid = byAlias ? ThreeNodeHarness.BobCarolBobAlias : ThreeNodeHarness.BobCarolScid;
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret, bobCarolScid: scid);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness, 0))!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ScidAliasInTheChannelType_When_TheOnionNamesTheRealScid_Then_BobFailsWithUnknownNextPeer()
    {
        // Arrange: BOLT 2 forbids routing by the real scid of a channel with option_scid_alias in its channel type
        await using var harness = await ThreeNodeHarness.CreateAsync(bobCarolScidAlias: FeatureSupport.Compulsory);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "coffee", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: ThreeNodeHarness.BobCarolScid);
        var before = Balances(harness);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Bob (hop 0) refused it; nothing reached Carol
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.UnknownNextPeer, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" }
                                               && m.Message is UpdateAddHtlcMessage);
        Assert.Equal(before, Balances(harness));
        AssertNoHtlcs(harness);
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
        await AssertNoSettledRowsAsync(harness);
        AssertNeverTwoLocks(harness);

        // NL-602: a failed forward and a refused HTLC move no money: nothing recorded
        Assert.Empty(await AccountingEventsAsync(harness.Bob));
        Assert.Empty(await AccountingEventsAsync(harness.Carol));
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

        // The circuit keeps why the forward failed: the fail_malformed code and the channel it is about (NL-457)
        var circuit = await GetCircuitAsync(harness, 0);
        Assert.Equal(ForwardCircuitStatus.Failed, circuit!.Status);
        Assert.Equal((ushort)FailureCode.InvalidOnionHmac, circuit.FailureCode);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, circuit.FailureSource);
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
    public async Task Given_AForwardThatOnlyFitsWithoutItsCommitmentFee_When_BobForwards_Then_HeRefusesItBeforeTheCircuit()
    {
        // Arrange: Bob funds the Bob-Carol channel; at a raised feerate what is left of his balance above Carol's
        // reserve no longer covers the commitment fee of one more HTLC (B2-ADD-S01), so the engine would refuse the
        // offer only after the circuit was saved: the liquidity pre-check must refuse it before that (NL-268)
        await using var harness = await ThreeNodeHarness.CreateAsync();
        await harness.Bob.Operations.UpdateFeeAsync(ThreeNodeHarness.BobCarolChannelId, 25_000,
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        var amount = LightningMoney.MilliSatoshis(1_170_000_000);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "tight", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(amount, invoice.PaymentHash, invoice.PaymentSecret);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: temporary_channel_failure from hop 0, with no circuit and no downstream HTLC: refused by the
        // pre-check, not by the engine at offer time
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Null(await GetCircuitAsync(harness, 0));
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AForwardAboveTheFeeEstimate_When_BobForwards_Then_ItStillForwards()
    {
        // Arrange: the same raised feerate, and an amount the channel can send including the fee estimate: the
        // pre-check must not refuse forwards the engine accepts (NL-268)
        await using var harness = await ThreeNodeHarness.CreateAsync();
        await harness.Bob.Operations.UpdateFeeAsync(ThreeNodeHarness.BobCarolChannelId, 25_000,
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        var amount = LightningMoney.MilliSatoshis(1_120_000_000);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(amount, "fits", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(amount, invoice.PaymentHash, invoice.PaymentSecret);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness, 0))!.Status);
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

        // NL-602: the replays and restarts recorded the forward and the settle once
        var forward = Assert.Single(await AccountingEventsAsync(harness.Bob));
        Assert.Equal(AccountingEventKind.ForwardSettled, forward.Kind);
        Assert.Equal(AccountingEventKind.InvoiceSettled, Assert.Single(await AccountingEventsAsync(harness.Carol)).Kind);
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
    public async Task Given_BobCrashesAfterRecordingTheOnionHmac_When_Restarted_Then_HeForwardsItInsteadOfFailingAReplay()
    {
        // Arrange: Bob's switch records the onion HMAC (its own save), then "crashes" on the shared secret's save
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "crash after hmac", null,
                                                                      TestContext.Current.CancellationToken);
        var crashes = 0;
        harness.Bob.BeforeSetOnionSharedSecret = (_, _) =>
        {
            crashes++;
            throw new InvalidOperationException("Simulated crash before the shared secret's save");
        };
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                           invoice.PaymentSecret));
        await harness.PumpAsync();
        harness.Bob.BeforeSetOnionSharedSecret = null;

        var incoming = harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!
                              .GetHtlc(HtlcDirection.Incoming, 0)!;
        var hmac = onion.Packet.Hmac;
        var recorded = await harness.Bob.InScopeAsync(u => u.OnionReplayDbRepository.GetByHmacAsync(hmac));
        var storedSecret = await harness.Bob.InScopeAsync(u => u.ChannelStateDbRepository.GetOnionSharedSecretAsync(
                                                              ThreeNodeHarness.AliceBobChannelId,
                                                              new HtlcKey(HtlcDirection.Incoming, 0)));
        Assert.Equal(1, crashes);
        Assert.Null(storedSecret);
        Assert.Empty(harness.Carol.Received.OfType<UpdateAddHtlcMessage>());

        // The HMAC is owned by the incoming HTLC (channel, id) and kept until its cltv_expiry
        Assert.NotNull(recorded);
        Assert.True(recorded.IsOwnedBy(ThreeNodeHarness.AliceBobChannelId, 0));
        Assert.Equal(incoming.CltvExpiry, recorded.ExpiryHeight);

        // Act: the restarted Bob processes the same HTLC again (startup / link-up replay)
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();

        // Assert: not a replay of itself; forwarded once and paid
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
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
        // Arrange: two payments of the same invoice locked in at Carol before her switch acts (NL-253). Without
        // basic_mpp: with it, an HTLC with the right secret for a settled invoice may be a held part of its set and is
        // fulfilled (MppReceiveTests)
        await using var harness = await ThreeNodeHarness.CreateAsync(
                                      h => h.Carol.Options.Features.BasicMpp = FeatureSupport.No);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "once", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var (firstId, firstOnion) = await harness.AlicePaysAsync(route);
        var (_, secondOnion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();
        var lockIns = harness.Carol.Events.OfType<IncomingHtlcLockedIn>().ToList();
        Assert.Equal(2, lockIns.Count);

        // The first handler to read the invoice waits there until another handler has read it too (or 500 ms): without
        // the payment hash lock both would read it Open, with it the second read only comes after the first settled
        var reads = 0;
        var overlapped = false;
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Carol.AfterInvoiceRead = async _ =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read > 1)
            {
                secondRead.TrySetResult();
                return;
            }

            try
            {
                await secondRead.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
                overlapped = true;
            }
            catch (TimeoutException)
            {
                // The second handler waited for the lock
            }
        };

        // Act
        harness.Carol.SwitchSuspended = false;
        await Task.WhenAll(lockIns.Select(e => Task.Run(() => harness.Carol.Switch.HandleAsync(
                                                            e, TestContext.Current.CancellationToken),
                                                        TestContext.Current.CancellationToken)));
        harness.Carol.AfterInvoiceRead = null;
        await harness.PumpAsync();

        // Assert: the reads never overlapped (NL-253 check-and-mark under the payment hash lock)
        Assert.False(overlapped);
        Assert.True(reads >= 2);

        // One fulfilled, the other failed from Carol with incorrect_or_unknown_payment_details
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
    public async Task Given_InvoiceAtCarol_When_HerFulfillLeaves_Then_TheInvoiceWasSettledInTheSameSave()
    {
        // Arrange - NL-253: no crash window between the fulfill's save and the invoice's
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "atomic", null,
                                                                      TestContext.Current.CancellationToken);
        InvoiceStatus? statusWhenSent = null;
        harness.Carol.OnPublish = message =>
        {
            // Under the channel lock, right after the fulfill's save: read what a crash would leave on disk
            if (message is UpdateFulfillHtlcMessage)
                statusWhenSent = harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                                   .GetByPaymentHashAsync(invoice.PaymentHash))
                                        .GetAwaiter().GetResult()!.Status;
        };

        // Act
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert
        Assert.Equal(InvoiceStatus.Settled, statusWhenSent);
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
    }

    [Fact]
    public async Task Given_CarolsLinkDownWhenSheWouldFulfill_When_TheLinkComesBack_Then_TheCommittedHtlcIsFulfilledOnce()
    {
        // Arrange - the HTLC is locked in at Carol, whose link to Bob drops before her switch acts
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "refused", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        harness.Disconnect(harness.Bob, harness.Carol);
        harness.Carol.SwitchSuspended = false;

        // Act 1: the fulfill is refused (peer away): the set is committed instead (NL-322/NL-323), the preimage on
        // the HTLC's record and the invoice settled in that save
        await harness.Carol.ReplayPendingEventsAsync();
        var whileAway = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                               .GetByPaymentHashAsync(invoice.PaymentHash));
        var committed = Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs
                                                     .Values, h => h.Direction == HtlcDirection.Incoming);
        var carolChannel = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId);
        var stored = await harness.Carol.InScopeAsync(async u => (await u.ChannelStateDbRepository.LoadAsync(
                                                                      carolChannel.ChannelId,
                                                                      carolChannel.Commitments!.Params))!
                                                                .Commitments.GetHtlc(HtlcDirection.Incoming,
                                                                                     committed.Id));

        // Act 2: the link is reestablished (N7 marks it up; nothing else replays)
        await harness.ReconnectLinkAsync(harness.Bob, harness.Carol);
        await harness.PumpAsync();

        // Assert: committed while away (nothing sent), then one fulfill
        Assert.Equal(InvoiceStatus.Settled, whileAway!.Status);
        Assert.Equal(invoice.Preimage, committed.KnownPreimage);
        Assert.Equal(invoice.Preimage, stored!.KnownPreimage);
        Assert.Null(committed.Removal);
        Assert.DoesNotContain(harness.Carol.Dropped, m => m is UpdateFulfillHtlcMessage);
        Assert.Single(harness.Sent, m => m is { From: "Carol", To: "Bob" } && m.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        var settled = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                            .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, settled!.Status);
        AssertNoHtlcs(harness);
        await AssertNoSettledRowsAsync(harness);

        // NL-602: the settle was recorded in the mark's save while away, and the fulfill's replay added nothing
        var settledEvent = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal(AccountingEventKind.InvoiceSettled, settledEvent.Kind);
        Assert.Equal((long)s_amount.MilliSatoshi, settledEvent.AmountMsat);
        Assert.Equal(whileAway.SettledAt, settledEvent.OccurredAt);
    }

    [Fact]
    public async Task Given_AliceAwayWhenCarolRevealsThePreimage_When_TheLinkComesBack_Then_BobFulfillsUpstreamWithoutARestart()
    {
        // Arrange - the HTLC reaches Carol; Alice's link to Bob drops before Carol settles
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "away", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        harness.Disconnect(harness.Alice, harness.Bob);

        // Act 1: Carol fulfills; Bob learns the preimage but his upstream fulfill is refused
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        Assert.Contains(harness.Bob.Events, e => e is OutgoingHtlcFulfilled);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Alice" }
                                              && m.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness, 0))!.Status);

        // Act 2: the link comes back (N7 marks it up); no restart, no harness replay
        await harness.ReconnectLinkAsync(harness.Alice, harness.Bob);
        await harness.PumpAsync();

        // Assert: Bob claimed from Alice exactly once
        Assert.Single(harness.Sent, m => m is { From: "Bob", To: "Alice" } && m.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(invoice.Preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        AssertNoHtlcs(harness);
        await AssertNoSettledRowsAsync(harness);
        AssertNeverTwoLocks(harness);

        // NL-602: the forward was recorded when its circuit was fulfilled (Act 1); the upstream fulfill's replay
        // added nothing
        Assert.Equal(AccountingEventKind.ForwardSettled, Assert.Single(await AccountingEventsAsync(harness.Bob)).Kind);
    }

    [Fact]
    public async Task Given_OfferThrowsBeforeItsSave_When_Forwarding_Then_TheCircuitFailsAndAliceGetsTemporaryChannelFailure()
    {
        // Arrange - staging the origin with Bob's add throws something other than a refusal
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "throws", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Bob.BeforeSetHtlcOrigin = _ => throw new InvalidOperationException("origin row missing");

        // Act
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                           invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert: nothing offered to Carol, the circuit failed, Alice gets Bob's temporary_channel_failure
        Assert.DoesNotContain(harness.Carol.Received, m => m is UpdateAddHtlcMessage);
        Assert.Equal(ForwardCircuitStatus.Failed, (await GetCircuitAsync(harness, 0))!.Status);
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_OfferThrowsAfterItsSave_When_Forwarding_Then_TheCircuitRecordsTheOutgoingHtlc()
    {
        // Arrange - Bob's add is saved, then publishing it throws (the message is lost with the "connection")
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "saved", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Bob.OnPublish = message =>
        {
            if (message is UpdateAddHtlcMessage)
                throw new InvalidOperationException("outbox closed");
        };

        // Act
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert: the forward is not failed upstream, since a live HTLC carries it
        var circuit = await GetCircuitAsync(harness, 0);
        Assert.Equal(ForwardCircuitStatus.Offered, circuit!.Status);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, circuit.OutgoingChannelId);
        Assert.NotNull(harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!
                              .GetHtlc(HtlcDirection.Outgoing, circuit.OutgoingHtlcId!.Value));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Alice" } && m.Message is UpdateFailHtlcMessage);
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

    [Fact]
    public async Task Given_TwoSiblingFlowsForkedFromOneContext_When_EachTakesOneLock_Then_NoViolationIsRecorded()
    {
        // Arrange - W6 integration: the replay of one channel and a channel_update of another run as tasks forked
        // from the same context; each holds one lock at a time, which is not a nested lock
        var audit = new LockAudit();
        LockAudit.BeginFlow();
        using (await audit.AcquireAsync(ThreeNodeHarness.AliceBobChannelId, TestContext.Current.CancellationToken))
        {
        }

        var firstHeld = new TaskCompletionSource();
        var secondHeld = new TaskCompletionSource();

        // Act - both hold their lock at the same time
        var first = Task.Run(async () =>
        {
            using (await audit.AcquireAsync(ThreeNodeHarness.AliceBobChannelId))
            {
                firstHeld.SetResult();
                await secondHeld.Task;
            }
        }, TestContext.Current.CancellationToken);
        var second = Task.Run(async () =>
        {
            await firstHeld.Task;
            using (await audit.AcquireAsync(ThreeNodeHarness.BobCarolChannelId))
                secondHeld.SetResult();
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);

        // Assert
        Assert.Empty(audit.Violations);
        Assert.Equal(1, audit.MaxHeldInOneFlow);
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        var decrypted = service.DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    /// <summary>The accounting events <paramref name="node"/> saved (NL-602; none is sealed in these tests).</summary>
    private static Task<IReadOnlyList<AccountingEventModel>> AccountingEventsAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000));

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

    /// <summary>NL-243: every archived row (incoming and outgoing) of every channel was pruned.</summary>
    private static async Task AssertNoSettledRowsAsync(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(await SettledRowsAsync(node, channel.ChannelId));
    }

    private static BalanceSheet Balances(ThreeNodeHarness harness) =>
        new(harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat,
         harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat,
         harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.LocalBalanceMsat,
         harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.LocalBalanceMsat);

    [Fact]
    public async Task Given_AForwardThroughBob_When_Resolved_Then_ListForwardsShowsItWithItsFeeAndTheRefusalsAreCounted()
    {
        // Arrange - NL-597/NL-598: a real forward lands in ForwardCircuits and the summary's counters see the HTLC
        // that never got a circuit
        await using var harness = await ThreeNodeHarness.CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "forwarded", null,
                                                                      TestContext.Current.CancellationToken);
        var fee = ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, s_amount);

        // Act 1: the refused one — Bob's onion names a channel he does not know
        var refusedRoute = harness.RouteToCarol(s_amount, ThreeNodeHarness.Sha256Of("refused"u8), invoice.PaymentSecret,
                                                bobCarolScid: new ShortChannelId(999_999, 9, 9));
        await harness.AlicePaysAsync(refusedRoute);
        await harness.PumpAsync();

        // Act 2: the real one — paid end to end through Bob
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: the counter counted the refusal once, and listforwards shows the fulfilled forward with its fee
        var counter = harness.Bob.Services.GetRequiredService<IRefusedHtlcCounter>();
        var snapshot = counter.Snapshot();
        Assert.Equal(1, snapshot.GetValueOrDefault(RefusedHtlcReason.UnknownNextChannel));
        Assert.Equal(1, counter.Total());

        var (forwards, totals) = await harness.Bob.InScopeAsync(async u =>
        {
            var forwards = await u.ForwardCircuitDbRepository.ListAsync(
                new ForwardCircuitListQuery(0, 100), TestContext.Current.CancellationToken);
            var totals = await u.ForwardCircuitDbRepository.SummarizeAsync(
                new ForwardCircuitListQuery(0, 100), TestContext.Current.CancellationToken);
            return (forwards, totals);
        });
        var forward = Assert.Single(forwards);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, forward.Status);
        Assert.Equal(fee.MilliSatoshi, forward.Fee.MilliSatoshi);
        Assert.Equal(1, totals.Fulfilled);
        Assert.Equal((long)fee.MilliSatoshi, totals.FulfilledFeesMsat);
        AssertNoHtlcs(harness);
    }

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
            Assert.True(node.LockAudit.Violations.Count == 0, node.Name + ": " + string.Join(" | ", node.LockAudit.Violations));
            Assert.Equal(1, node.LockAudit.MaxHeldInOneFlow);
        }
    }

    private readonly record struct BalanceSheet(ulong AliceAb, ulong BobAb, ulong BobBc, ulong CarolBc);
}