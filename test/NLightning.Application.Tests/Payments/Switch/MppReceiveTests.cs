using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Application.Payments.Switch;
using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// ABCD W6-B lane proof: Carol receives multi-part payments (BOLT 4 <c>basic_mpp</c>) through the production
/// <see cref="HtlcSwitch"/> on <see cref="ThreeNodeHarness"/> (real onions, signatures and SQLite persistence). Alice
/// sends each part as its own HTLC through Bob, with the final payload's <c>total_msat</c> set to the whole payment.
/// </summary>
public class MppReceiveTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.MilliSatoshis(35_000_000);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();
    private uint _carolHeight = ThreeNodeHarness.BlockHeight;

    public MppReceiveTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => _carolHeight);
    }

    [Fact]
    public async Task Given_TwoParts_When_TheSecondArrives_Then_BothFulfilledAndInvoiceSettledOnce()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);

        // Act: the first part is held
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert: nothing revealed, the invoice still open, the part locked in at Carol
        Assert.DoesNotContain(harness.Sent, s => s.From == "Carol" && s.Message is UpdateFulfillHtlcMessage);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(1, _clock.PendingTimers);

        // Act: the second part completes the set
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Assert: both parts fulfilled with the preimage, the invoice settled for the sum, nothing left anywhere
        Assert.Equal(2, harness.Sent.Count(s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFulfillHtlcMessage));
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.Alice.PaymentHandler.Fulfilled, f => Assert.Equal(invoice.Preimage, f.PaymentPreimage));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_IncompleteSet_When_60SecondsPass_Then_EveryPartFailedWithMppTimeout()
    {
        // Arrange: one part of two
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Act: 59 s is not enough
        await AdvanceAsync(harness, TimeSpan.FromSeconds(59));

        // Assert
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);

        // Act: the 60th second
        await AdvanceAsync(harness, TimeSpan.FromSeconds(1));

        // Assert: mpp_timeout from Carol (hop 1), the invoice still open for a new attempt
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);

        // And a new attempt with both parts still pays the invoice
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
    }

    [Fact]
    public async Task Given_TimeoutWhileBobIsAway_When_TheLinkComesBack_Then_ThePartIsFailedWithMppTimeout()
    {
        // Arrange: one part held, then the Bob-Carol link drops
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        harness.Disconnect(harness.Bob, harness.Carol);

        // Act: the timeout's failure is refused (link down), then the link comes back and replays the lock-in
        await AdvanceAsync(harness, TimeSpan.FromSeconds(60));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        await harness.ReconnectLinkAsync(harness.Bob, harness.Carol);
        await harness.PumpAsync();

        // Assert: the replayed part is failed with mpp_timeout, not held again with a fresh timeout
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.MppTimeout, Decrypt(harness, onion, failed).Code);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_PartWithAnotherTotalMsat_When_Received_Then_TheWholeSetIsFailed()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var first = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Act: a second part promising another total
        var second = await PayPartAsync(harness, invoice, s_secondPart, s_amount + LightningMoney.MilliSatoshis(1_000));
        await harness.PumpAsync();

        // Assert: both parts failed with incorrect_or_unknown_payment_details (BOLT 4 SHOULD), nothing fulfilled
        Assert.Equal(2, harness.Alice.PaymentHandler.Failed.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        var codes = harness.Alice.PaymentHandler.Failed
                           .Select(f => (Decrypt(harness, first, f, allowUnreadable: true)
                                      ?? Decrypt(harness, second, f, allowUnreadable: true))!.Code)
                           .ToList();
        Assert.All(codes, c => Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, c));
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_PartialSetHeld_When_CarolRestartsAndTheLastPartArrives_Then_TheRestoredSetIsFulfilled()
    {
        // Arrange: one part held when Carol restarts (the set lives only in memory)
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Act: the restart rebuilds the set from the persisted incoming HTLC (startup replay, then link-up replay)
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_PartialSetHeld_When_CarolRestartsAndNothingMoreArrives_Then_TheRestoredPartTimesOut()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        await AdvanceAsync(harness, TimeSpan.FromSeconds(30));

        // Act: after the restart the timeout starts again from the replay
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        await AdvanceAsync(harness, TimeSpan.FromSeconds(59));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(1));

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.MppTimeout, Decrypt(harness, onion, failed).Code);
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_CompleteSetNotActedOn_When_CarolRestarts_Then_BothPartsFulfilled()
    {
        // Arrange: the last part locks in but Carol's switch stops before acting on it
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);

        // Act
        harness.Carol.SwitchSuspended = false;
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_SetSettledButOneFulfillRefused_When_TheLinkComesBack_Then_TheHeldPartIsFulfilledToo()
    {
        // Arrange: the link drops right after the first fulfill (the one that settles the invoice) is published
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        harness.Carol.OnPublish = message =>
        {
            if (message is UpdateFulfillHtlcMessage)
                harness.Carol.Probe.MarkLinkDown(ThreeNodeHarness.BobCarolChannelId);
        };

        // Act
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Assert: one fulfill went out, the invoice is settled and the other part is still locked in at Carol
        harness.Carol.OnPublish = null;
        Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" } && s.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                        h => h is { Direction: HtlcDirection.Incoming, Removal: null });

        // Act: the link comes back; its replay finds the held part of a settled set
        await harness.ReconnectLinkAsync(harness.Carol, harness.Bob);
        await harness.PumpAsync();

        // Assert: the whole set is fulfilled (BOLT 4 MUST), the mpp timeout never fires on it
        Assert.Equal(2, harness.Sent.Count(s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFulfillHtlcMessage));
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        await AdvanceAsync(harness, TimeSpan.FromMinutes(2));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        AssertNoHtlcs(harness);
    }

    [Theory]
    [InlineData(ThreeNodeHarness.BlockHeight + 10)] // past cltv_expiry - min_final_cltv_expiry_delta (543 - 40)
    [InlineData(0u)] // the chain monitor has no height yet (a startup replay)
    public async Task Given_SetSettledButOneFulfillRefused_When_TheLinkComesBackAtAnotherHeight_Then_FulfilledToo(
        uint heightAtReplay)
    {
        // Arrange: the second fulfill is refused after the first settled the invoice
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        await PayPartRefusingAllButTheFirstFulfillAsync(harness, invoice, s_secondPart, s_amount);

        // Act: blocks pass (or the height is unknown) before the link comes back and replays the held part
        _carolHeight = heightAtReplay;
        await harness.ReconnectLinkAsync(harness.Carol, harness.Bob);
        await harness.PumpAsync();

        // Assert: the held part is fulfilled anyway, the preimage being out already (BOLT 4 MUST)
        Assert.Equal(2, harness.Sent.Count(s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFulfillHtlcMessage));
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_SettledSetWithAPartCoveringTotalMsatAlone_When_ItsRefusedFulfillIsReplayed_Then_Fulfilled()
    {
        // Arrange: the payer overpays: 25,000 sat then 60,000 sat, both promising total_msat 60,000 sat. The second
        // part alone has amt_to_forward = total_msat but belongs to the set
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        await PayPartRefusingAllButTheFirstFulfillAsync(harness, invoice, s_amount, s_amount);

        // The invoice received what the set carried
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(s_firstPart + s_amount, stored.AmountReceived);

        // Act
        await harness.ReconnectLinkAsync(harness.Carol, harness.Bob);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ReplayOfAHeldPartWaitingForTheHashLock_When_TheSetIsFulfilledMeanwhile_Then_NotHandledTwice()
    {
        // Arrange: the first part is held
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        var held = Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                                 h => h.Direction == HtlcDirection.Incoming);
        var replay = new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, held);

        // While the last part's handler holds the payment hash lock (reading the invoice), a replay of the held part's
        // lock-in starts (a link-up) and waits for that lock
        var reads = 0;
        Task? replayTask = null;
        harness.Carol.AfterInvoiceRead = async _ =>
        {
            if (Interlocked.Increment(ref reads) > 1)
                return;

            using (ExecutionContext.SuppressFlow())
                replayTask = Task.Run(() => harness.Carol.Switch.HandleAsync(replay, CancellationToken.None),
                                      CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        };

        // Act
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        await replayTask!;
        harness.Carol.AfterInvoiceRead = null;
        await harness.PumpAsync();

        // Assert: the replay saw its HTLC resolved once it got the lock and stopped (no evaluation of its own, no
        // second fulfill, no new set): the only reads are the last part's check and its settle
        Assert.Equal(2, reads);
        Assert.Equal(2, harness.Sent.Count(s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFulfillHtlcMessage));
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_SingleHtlcForASettledInvoice_When_Received_Then_FailedAsNotAPartOfTheSet()
    {
        // Arrange: the invoice is paid in two parts
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);

        // Act: a single-part payment of the same hash with the right secret (NL-323: the committed parts of the set
        // carry the preimage in their records, this one does not)
        var onion = await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert: failed as LND does, the invoice untouched
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, Decrypt(harness, onion, failed).Code);
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_LatePartForASettledInvoice_When_Received_Then_FailedAsNotAPartOfTheSet()
    {
        // Arrange: the invoice is paid in two parts
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Act: a third part of the same total arrives after the set settled (NL-323)
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, Decrypt(harness, onion, failed).Code);
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
    }

    [Fact]
    public async Task Given_SingleHtlcWithAnotherSecretForASettledInvoice_When_Received_Then_FailedAsUnknown()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var invoice = await CreateInvoiceAsync(harness);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Act: whoever pays with another payment_secret never saw the invoice
        var onion = await PayPartAsync(harness, invoice, s_amount, s_amount,
                                       new Secret(Enumerable.Repeat((byte)7, 32).ToArray()));
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, Decrypt(harness, onion, failed).Code);
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
    }

    [Fact]
    public async Task Given_BasicMppTurnedOff_When_APartArrives_Then_FailedAtOnce()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync(h =>
        {
            h.Carol.Options.Features.BasicMpp = Domain.Enums.FeatureSupport.No;
            h.Carol.ConfigureServices = services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
        });
        var invoice = await CreateInvoiceAsync(harness);

        // Act
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert: BOLT 4, a node without basic_mpp MUST fail total_msat != amt_to_forward
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, Decrypt(harness, onion, failed).Code);
        Assert.Equal(0, _clock.PendingTimers);
    }

    private Task<ThreeNodeHarness> CreateHarnessAsync() =>
        ThreeNodeHarness.CreateAsync(h => h.Carol.ConfigureServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
        });

    private static Task<InvoiceModel> CreateInvoiceAsync(ThreeNodeHarness harness) =>
        harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "mpp", null, TestContext.Current.CancellationToken);

    private static HtlcSwitch CarolSwitch(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<HtlcSwitch>();

    private async Task AdvanceAsync(ThreeNodeHarness harness, TimeSpan by)
    {
        _clock.Advance(by);
        await CarolSwitch(harness).WhenIdleAsync();
        await harness.PumpAsync();
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC carrying <paramref name="part"/> to Carol, whose payload promises
    /// <paramref name="total"/> (<c>payment_data.total_msat</c>).
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, InvoiceModel invoice,
                                                         LightningMoney part, LightningMoney total,
                                                         Secret? paymentSecret = null)
    {
        var route = harness.RouteToCarol(part, invoice.PaymentHash, paymentSecret ?? invoice.PaymentSecret);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            var payload = hop.IsFinal
                              ? new HopPayload([
                                    new AmtToForwardTlv(hop.AmountToForward),
                                    new OutgoingCltvValueTlv(hop.OutgoingCltvValue),
                                    new PaymentDataTlv(route.PaymentSecret, total)
                                ])
                              : PaymentOnionFactory.CreatePayload(hop, route);
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var sessionKey = PaymentOnionFactory.CreateSessionKey();
        var constructed = harness.Alice.Services.GetRequiredService<ISphinxService>()
                                 .ConstructWithSharedSecrets(hops, new PrivKey(sessionKey), route.PaymentHash);
        var onion = new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash));
        return onion;
    }

    /// <summary>
    /// Pays the part that completes the set while the Bob-Carol link drops right after Carol publishes her first
    /// fulfill (the one that settles the invoice): the other fulfills are refused and their parts stay locked in.
    /// </summary>
    private static async Task PayPartRefusingAllButTheFirstFulfillAsync(ThreeNodeHarness harness, InvoiceModel invoice,
                                                                        LightningMoney part, LightningMoney total)
    {
        harness.Carol.OnPublish = message =>
        {
            if (message is UpdateFulfillHtlcMessage)
                harness.Carol.Probe.MarkLinkDown(ThreeNodeHarness.BobCarolChannelId);
        };
        await PayPartAsync(harness, invoice, part, total);
        await harness.PumpAsync();
        harness.Carol.OnPublish = null;

        Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" } && s.Message is UpdateFulfillHtlcMessage);
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                        h => h is { Direction: HtlcDirection.Incoming, Removal: null });

        // NL-322/NL-323: every part left without its fulfill was committed first (the preimage on its record)
        Assert.All(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                          .Where(h => h is { Direction: HtlcDirection.Incoming, Removal: null }),
                   h => Assert.Equal(invoice.Preimage, h.KnownPreimage));
    }

    private static Task<InvoiceModel> GetInvoiceAsync(ThreeNodeHarness harness, InvoiceModel invoice) =>
        harness.Carol.InScopeAsync(async u => (await u.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash))!);

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed) =>
        Decrypt(harness, onion, failed, allowUnreadable: false)!;

    private static DecryptedFailure? Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed,
                                             bool allowUnreadable)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        var decrypted = service.DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        if (!allowUnreadable)
            Assert.NotNull(decrypted);
        return decrypted;
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}