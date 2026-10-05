using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Application.Payments.Switch;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Events;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-995 hold invoices: Carol issues an invoice for a caller-supplied payment hash (no preimage), Alice pays it
/// through Bob, and the completed set is held — locked in, nothing fulfilled or failed — until the operator settles
/// with the outside preimage or cancels, through the production <see cref="HtlcSwitch"/> on
/// <see cref="ThreeNodeHarness"/> (real onions, signatures and SQLite persistence).
/// </summary>
public class HoldInvoiceTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(60_000_000);
    private static readonly LightningMoney s_firstPart = LightningMoney.MilliSatoshis(25_000_000);
    private static readonly LightningMoney s_secondPart = LightningMoney.MilliSatoshis(35_000_000);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();
    private readonly uint _carolHeight = ThreeNodeHarness.BlockHeight;

    public HoldInvoiceTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => _carolHeight);
    }

    [Fact]
    public async Task Given_ASinglePartPaymentOfAHoldInvoice_When_TheSetCompletes_Then_TheInvoiceIsHeldWithNothingFulfilled()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        using var carolEvents = harness.Carol.Services.GetRequiredService<IPaymentEventSource>().Subscribe();

        // Act: Alice pays the whole invoice as one HTLC
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();

        // Assert: the invoice is Held with the amount received, no preimage of ours, and the part still locked in
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Held, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Null(stored.Preimage);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                      h => h is { Direction: HtlcDirection.Incoming, Removal: null });
        Assert.Contains(harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                        h => h is { Direction: HtlcDirection.Outgoing, Removal: null });

        // Nothing fulfilled or failed: the payer's part is still in flight and the mpp timer is gone
        Assert.DoesNotContain(harness.Sent, s => s.From == "Carol" && s.Message is UpdateFulfillHtlcMessage);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcsRemoved(harness);

        // Cashu plan C0: the hold is published once the set completed
        var held = Assert.IsType<InvoiceHeldEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents));
        Assert.Equal(invoice.PaymentHash, held.PaymentHash);
        Assert.Equal(s_amount, held.Amount);

        // Nothing was booked: no money moved while the operator decides
        Assert.Empty(await AccountingEventsAsync(harness.Carol));
    }

    [Fact]
    public async Task Given_AHeldSet_When_TheOperatorSettlesWithThePreimage_Then_ThePartsAreFulfilledAndTheInvoiceSettles()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        using var carolEvents = harness.Carol.Services.GetRequiredService<IPaymentEventSource>().Subscribe();
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);

        // Act
        await HoldService(harness).SettleHoldInvoiceAsync(invoice.PaymentHash, preimage,
                                                          TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert: the part is fulfilled with the operator's preimage, Bob forwards it upstream
        Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" } && s.Message is UpdateFulfillHtlcMessage);
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

        // The invoice settles storing that preimage
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(preimage, stored.Preimage);
        Assert.NotNull(stored.SettledAt);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);

        // One InvoiceSettled accounting event (staged in the settle's own save) and one InvoiceSettledEvent
        var settled = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal(AccountingEventKind.InvoiceSettled, settled.Kind);
        Assert.Equal(AccountingEventKeys.InvoiceSettled(invoice.PaymentHash), settled.EventKey);
        Assert.Equal((long)s_amount.MilliSatoshi, settled.AmountMsat);
        Assert.Equal("1", settled.Details["parts"]);
        Assert.Equal(harness.Bob.NodeId, settled.Counterparty);
        // The subscription saw the hold first, then the settle of the same payment
        Assert.Equal(invoice.PaymentHash,
                     Assert.IsType<InvoiceHeldEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents)).PaymentHash);
        var published = Assert.IsType<InvoiceSettledEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents));
        Assert.Equal(invoice.PaymentHash, published.PaymentHash);
        Assert.Equal(s_amount, published.Amount);
    }

    [Fact]
    public async Task Given_AHeldSet_When_SettledWithAWrongPreimage_Then_RefusedAndTheHoldStands()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);

        // Act: whoever settles does not know the preimage (it must hash to the payment hash)
        await Assert.ThrowsAsync<ArgumentException>(() => HoldService(harness).SettleHoldInvoiceAsync(
                                                         invoice.PaymentHash, NewPreimage(),
                                                         TestContext.Current.CancellationToken));
        await harness.PumpAsync();

        // Assert: nothing changed, the hold stands with its part locked in
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Held, stored.Status);
        Assert.Null(stored.Preimage);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.DoesNotContain(harness.Sent, s => s.From == "Carol" && s.Message is UpdateFulfillHtlcMessage);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                      h => h is { Direction: HtlcDirection.Incoming, Removal: null });

        // The real preimage still settles it afterwards
        await HoldService(harness).SettleHoldInvoiceAsync(invoice.PaymentHash, preimage,
                                                          TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Settled, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Equal(preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
    }

    [Fact]
    public async Task Given_AHeldSet_When_TheOperatorCancels_Then_ThePartIsFailedBackAndTheInvoiceCanceled()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        var onion = await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);

        // Act
        await HoldService(harness).CancelHoldInvoiceAsync(invoice.PaymentHash,
                                                          TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert: the part is failed back incorrect_or_unknown_payment_details (as a canceled invoice's would be)
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(InvoiceStatus.Canceled, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(0, _clock.PendingTimers);
        AssertNoHtlcs(harness);
        Assert.Empty(await AccountingEventsAsync(harness.Carol));
    }

    [Fact]
    public async Task Given_TwoPartsOfAHoldInvoice_When_OnlyTheFirstArrives_Then_TheInvoiceHoldsOnlyWhenTheSetCompletes()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        using var carolEvents = harness.Carol.Services.GetRequiredService<IPaymentEventSource>().Subscribe();

        // Act: the first part is held by the set's mpp hold
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert: the invoice is still open, the part locked in, one mpp timer running and nothing published
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(1, _clock.PendingTimers);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                      h => h is { Direction: HtlcDirection.Incoming, Removal: null });
        Assert.False(carolEvents.Overflowed);

        // Act: the second part completes the set
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();

        // Assert: only now is the invoice held, with both parts locked in and the mpp timer killed
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Held, stored.Status);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Equal(2, harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                              .Count(h => h is { Direction: HtlcDirection.Incoming, Removal: null }));
        Assert.Equal(0, _clock.PendingTimers);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var held = Assert.IsType<InvoiceHeldEvent>(await PaymentEventHubTests.ReadOneAsync(carolEvents));
        Assert.Equal(invoice.PaymentHash, held.PaymentHash);
        Assert.Equal(s_amount, held.Amount);

        // Act: the operator settles the held set
        await HoldService(harness).SettleHoldInvoiceAsync(invoice.PaymentHash, preimage,
                                                          TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert: both parts are fulfilled with the operator's preimage
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.Alice.PaymentHandler.Fulfilled, f => Assert.Equal(preimage, f.PaymentPreimage));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(preimage, stored.Preimage);
        Assert.Equal(s_amount, stored.AmountReceived);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);
        var settled = Assert.Single(await AccountingEventsAsync(harness.Carol));
        Assert.Equal(AccountingEventKind.InvoiceSettled, settled.Kind);
        Assert.Equal("2", settled.Details["parts"]);
    }

    [Fact]
    public async Task Given_AnIncompleteHoldSet_When_60SecondsPass_Then_ThePartsFailWithMppTimeoutAndTheInvoiceStaysOpen()
    {
        // Arrange: one part of two
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        var onion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();

        // Assert: the part is held by the set's mpp hold, the invoice still open
        Assert.Equal(InvoiceStatus.Open, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(1, _clock.PendingTimers);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

        // Act: 59 s is not enough
        await AdvanceAsync(harness, TimeSpan.FromSeconds(59));

        // Assert
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

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
        Assert.Empty(await AccountingEventsAsync(harness.Carol));
    }

    [Fact]
    public async Task Given_AHeldInvoice_When_CarolRestarts_Then_TheReplayedLockInsRebuildTheSetAndSettleStillWorks()
    {
        // Arrange: the single-part set is held when Carol restarts (the set lives only in memory)
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        await PayPartAsync(harness, invoice, s_amount, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);

        // Act: the restart rebuilds the set from the persisted incoming HTLC (startup replay, then link-up replay);
        // the row is already Held, so the re-hold is idempotent
        await harness.RestartAsync(harness.Carol);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert: the set is back, held, nothing fulfilled or failed
        Assert.Contains(invoice.PaymentHash, CarolSwitch(harness).HeldPaymentHashes);
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.DoesNotContain(harness.Sent, s => s.From == "Carol" && s.Message is UpdateFulfillHtlcMessage);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Single(harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values,
                      h => h is { Direction: HtlcDirection.Incoming, Removal: null });

        // Act: the operator settles the rebuilt set
        await HoldService(harness).SettleHoldInvoiceAsync(invoice.PaymentHash, preimage,
                                                          TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        var stored = await GetInvoiceAsync(harness, invoice);
        Assert.Equal(InvoiceStatus.Settled, stored.Status);
        Assert.Equal(preimage, stored.Preimage);
        Assert.Equal(preimage, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AHeldSet_When_OnePartIsFailedBack_Then_TheHoldIsCanceledAndTheRestCascades()
    {
        // Arrange: a complete two-part set is held
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        var firstOnion = await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        var secondOnion = await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Equal(0, _clock.PendingTimers);

        // Act: the deadline monitor's fail-back takes one part near its CLTV (the CLTV guard): Carol fails it back
        // herself, and the removal of a held part cancels the whole hold
        var firstIncoming = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                                   .Where(h => h is { Direction: HtlcDirection.Incoming, Removal: null })
                                   .OrderBy(h => h.Id)
                                   .ToList();
        Assert.Equal(2, firstIncoming.Count);
        await FailIncomingHtlcBackAsync(harness, ThreeNodeHarness.BobCarolChannelId, firstIncoming[0].Id);
        await harness.PumpAsync();

        // Assert: the invoice is canceled, both parts are back at Alice and nothing was fulfilled
        Assert.Equal(InvoiceStatus.Canceled, (await GetInvoiceAsync(harness, invoice)).Status);
        Assert.Empty(CarolSwitch(harness).HeldPaymentHashes);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(2, harness.Alice.PaymentHandler.Failed.Count);

        // The part we failed back ourselves carries our temporary_node_failure from hop 1 (Carol)...
        var direct = harness.Alice.PaymentHandler.Failed
                              .Single(f => DecryptOrNull(harness, firstOnion, f) is not null);
        var directDecrypted = Decrypt(harness, firstOnion, direct);
        Assert.Equal(1, directDecrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, directDecrypted.Code);

        // ...and the cascade failed the other one incorrect_or_unknown_payment_details (a canceled invoice's failure)
        var cascaded = harness.Alice.PaymentHandler.Failed
                              .Single(f => DecryptOrNull(harness, secondOnion, f) is not null);
        var cascadedDecrypted = Decrypt(harness, secondOnion, cascaded);
        Assert.Equal(1, cascadedDecrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, cascadedDecrypted.Code);

        AssertNoHtlcs(harness);
        Assert.Empty(await AccountingEventsAsync(harness.Carol));
    }

    [Fact]
    public async Task Given_AHoldCanceledByTheGuard_When_TheOperatorSettles_Then_Refused()
    {
        // Arrange: the guard's cascade already canceled the hold (one part was failed back)
        await using var harness = await CreateHarnessAsync();
        var preimage = NewPreimage();
        var invoice = await CreateHoldInvoiceAsync(harness, preimage);
        await PayPartAsync(harness, invoice, s_firstPart, s_amount);
        await harness.PumpAsync();
        await PayPartAsync(harness, invoice, s_secondPart, s_amount);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Held, (await GetInvoiceAsync(harness, invoice)).Status);
        var part = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                          .Where(h => h is { Direction: HtlcDirection.Incoming, Removal: null })
                          .OrderBy(h => h.Id)
                          .First();
        await FailIncomingHtlcBackAsync(harness, ThreeNodeHarness.BobCarolChannelId, part.Id);
        await harness.PumpAsync();
        Assert.Equal(InvoiceStatus.Canceled, (await GetInvoiceAsync(harness, invoice)).Status);

        // Act: the operator settles too late
        await Assert.ThrowsAsync<InvalidOperationException>(() => HoldService(harness).SettleHoldInvoiceAsync(
                                                             invoice.PaymentHash, preimage,
                                                             TestContext.Current.CancellationToken));

        // Assert: nothing was fulfilled on top of the cascade's failures
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(InvoiceStatus.Canceled, (await GetInvoiceAsync(harness, invoice)).Status);
    }

    private Task<ThreeNodeHarness> CreateHarnessAsync() =>
        ThreeNodeHarness.CreateAsync(h => h.Carol.ConfigureServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
        });

    /// <summary>The outside preimage the operator will settle with; the invoice's hash is SHA256 of it.</summary>
    private static Secret NewPreimage() => new(RandomNumberGenerator.GetBytes(32));

    private static Task<InvoiceModel> CreateHoldInvoiceAsync(ThreeNodeHarness harness, Secret preimage) =>
        harness.Carol.Invoices.CreateHoldInvoiceAsync(ThreeNodeHarness.Sha256Of(preimage), s_amount, "hold", null,
                                                      SourceLabels.None, TestContext.Current.CancellationToken);

    private static HtlcSwitch CarolSwitch(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<HtlcSwitch>();

    private static IHoldInvoiceService HoldService(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<IHoldInvoiceService>();

    private async Task AdvanceAsync(ThreeNodeHarness harness, TimeSpan by)
    {
        _clock.Advance(by);
        await CarolSwitch(harness).WhenIdleAsync();
        await harness.PumpAsync();
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC carrying <paramref name="part"/> to Carol, whose payload promises
    /// <paramref name="total"/> (<c>payment_data.total_msat</c>) — as <c>MppReceiveTests.PayPartAsync</c>.
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, InvoiceModel invoice,
                                                         LightningMoney part, LightningMoney total)
    {
        var route = harness.RouteToCarol(part, invoice.PaymentHash, invoice.PaymentSecret);
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
    /// Carol fails her incoming HTLC <paramref name="htlcId"/> back herself with an error onion built from the HTLC's
    /// stored shared secret — the deadline monitor's fail-back near the part's CLTV (the guard, NL-995): the removal
    /// reaches the switch as an <see cref="IncomingHtlcSettled"/> that is not a fulfill.
    /// </summary>
    private static async Task FailIncomingHtlcBackAsync(ThreeNodeHarness harness, ChannelId channelId,
                                                        ulong htlcId)
    {
        var sharedSecret = await harness.Carol.InScopeAsync(u => u.ChannelStateDbRepository
                                                                      .GetOnionSharedSecretAsync(
                                                                          channelId,
                                                                          new HtlcKey(HtlcDirection.Incoming,
                                                                                      htlcId)));
        if (sharedSecret is not { } secret)
            throw new InvalidOperationException($"No stored shared secret for HTLC {htlcId} of {channelId}");
        var packet = harness.Carol.Services.GetRequiredService<IFailureOnionService>()
                            .CreateErrorPacket(secret, FailureMessage.TemporaryNodeFailure());
        await harness.Carol.Operations.FailHtlcAsync(channelId, htlcId, packet, TestContext.Current.CancellationToken);
    }

    private static Task<InvoiceModel> GetInvoiceAsync(ThreeNodeHarness harness, InvoiceModel invoice) =>
        harness.Carol.InScopeAsync(async u => (await u.InvoiceDbRepository.GetByPaymentHashAsync(invoice.PaymentHash))!);

    /// <summary>The accounting events <paramref name="node"/> saved (NL-602; none is sealed in these tests).</summary>
    private static Task<IReadOnlyList<AccountingEventModel>> AccountingEventsAsync(SwitchNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000));

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = DecryptOrNull(harness, onion, failed);
        Assert.NotNull(decrypted);
        return decrypted!;
    }

    /// <summary>The failure decrypted with <paramref name="onion"/>'s secrets, or null when it belongs to another
    /// part's onion (no hop's HMAC matched).</summary>
    private static DecryptedFailure? DecryptOrNull(ThreeNodeHarness harness, PaymentOnion onion,
                                                   OutgoingHtlcFailed failed)
    {
        var service = harness.Alice.Services.GetRequiredService<IFailureOnionService>();
        return service.DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
    }

    /// <summary>No HTLC was removed anywhere while a hold stands (the parts stay locked in, bobbing in flight).</summary>
    private static void AssertNoHtlcsRemoved(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.DoesNotContain(channel.Commitments!.Htlcs.Values, h => h.Removal is not null);
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }
}