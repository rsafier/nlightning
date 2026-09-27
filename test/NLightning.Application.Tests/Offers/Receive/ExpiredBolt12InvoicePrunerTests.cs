using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers.Receive;
using Application.Payments.FinalHop;
using Application.Payments.Switch;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;

/// <summary>
/// NL-448: expired unpaid BOLT 12 invoice rows are deleted by <see cref="ExpiredBolt12InvoicePruner"/> in bounded
/// batches, and a late HTLC for a pruned invoice fails like one for the expired invoice.
/// </summary>
public class ExpiredBolt12InvoicePrunerTests
{
    private const uint ExpirySeconds = 7_200;

    private static readonly TimeSpan s_grace = new OfferOptions().ExpiredInvoicePruneGrace;

    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static InvoiceModel Bolt12Invoice(byte fill, InvoiceStatus status = InvoiceStatus.Open) =>
        Bolt12Invoice(Enumerable.Repeat(fill, 32).ToArray(), status);

    private static InvoiceModel Bolt12Invoice(byte[] preimage, InvoiceStatus status = InvoiceStatus.Open)
    {
        var details = new Bolt12InvoiceDetails(new Hash(new byte[32]), new byte[] { 1 }, TestPaths.Point(0x02));
        return new InvoiceModel(SHA256.HashData(preimage), preimage, Enumerable.Repeat((byte)0x53, 32).ToArray(),
                                LightningMoney.MilliSatoshis(1_000), "coffee", null, s_createdAt, ExpirySeconds, 40,
                                status, status == InvoiceStatus.Open ? null : LightningMoney.MilliSatoshis(1_000),
                                bolt12: details);
    }

    private static InvoiceModel Bolt11Invoice(byte fill)
    {
        var preimage = Enumerable.Repeat(fill, 32).ToArray();
        return new InvoiceModel(SHA256.HashData(preimage), preimage, Enumerable.Repeat((byte)0x53, 32).ToArray(),
                                LightningMoney.MilliSatoshis(1_000), "tea", "lnbcrt1dummy", s_createdAt, ExpirySeconds,
                                40);
    }

    private static async Task AddExpiredBolt12InvoicesAsync(OfferTestStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var preimage = new byte[32];
            BitConverter.TryWriteBytes(preimage, i);
            await store.Invoices.AddAsync(Bolt12Invoice(preimage));
        }
    }

    private static ExpiredBolt12InvoicePruner CreatePruner(OfferTestStore store, TimeProvider clock,
                                                           OfferOptions? options = null) =>
        new(store.ScopeFactory, NullLogger<ExpiredBolt12InvoicePruner>.Instance,
            Options.Create(options ?? new OfferOptions()), clock);

    [Fact]
    public async Task Given_ExpiredAndLiveInvoices_When_PrunedOnce_Then_OnlyExpiredOpenBolt12InvoicesAreDeleted()
    {
        // Arrange
        using var store = new OfferTestStore();
        var expired = Bolt12Invoice(0xA1);
        var accepted = Bolt12Invoice(0xA2, InvoiceStatus.Accepted);
        var bolt11 = Bolt11Invoice(0xA3);
        await store.Invoices.AddAsync(expired);
        await store.Invoices.AddAsync(accepted);
        await store.Invoices.AddAsync(bolt11);
        var clock = new ManualClock(s_createdAt.AddSeconds(ExpirySeconds) + s_grace);
        var live = new InvoiceModel(SHA256.HashData(new byte[32]), new byte[32], new byte[32], null, null, null,
                                    clock.GetUtcNow(), ExpirySeconds, 40,
                                    bolt12: new Bolt12InvoiceDetails(new Hash(new byte[32]), new byte[] { 1 },
                                                                     TestPaths.Point(0x02)));
        await store.Invoices.AddAsync(live);
        await using var pruner = CreatePruner(store, clock);

        // Act
        var pruned = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, pruned);
        Assert.Equal(1, store.Saves);
        Assert.DoesNotContain(store.Invoices.Invoices, i => i.PaymentHash == expired.PaymentHash);
        Assert.Contains(store.Invoices.Invoices, i => i.PaymentHash == accepted.PaymentHash);
        Assert.Contains(store.Invoices.Invoices, i => i.PaymentHash == bolt11.PaymentHash);
        Assert.Contains(store.Invoices.Invoices, i => i.PaymentHash == live.PaymentHash);
    }

    [Fact]
    public async Task Given_NothingExpired_When_PrunedOnce_Then_NoSave()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, 3);
        await using var pruner = CreatePruner(store, new ManualClock(s_createdAt));

        // Act
        var pruned = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, pruned);
        Assert.Equal(0, store.Saves);
        Assert.Equal(3, store.Invoices.Invoices.Count);
    }

    [Fact]
    public async Task Given_MoreExpiredThanABatch_When_PrunedOnce_Then_OneSavePerBatchUntilAShortBatch()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, 5);
        await using var pruner = CreatePruner(store, new ManualClock(s_createdAt.AddDays(1)),
                                              new OfferOptions { ExpiredInvoicePruneBatchSize = 2 });

        // Act
        var pruned = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, pruned);
        Assert.Equal(3, store.Saves);
        Assert.Empty(store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_MoreExpiredThanARound_When_PrunedOnce_Then_AtMostMaxBatchesPerRound()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, ExpiredBolt12InvoicePruner.MaxBatchesPerRound + 3);
        await using var pruner = CreatePruner(store, new ManualClock(s_createdAt.AddDays(1)),
                                              new OfferOptions { ExpiredInvoicePruneBatchSize = 1 });

        // Act
        var first = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);
        var second = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ExpiredBolt12InvoicePruner.MaxBatchesPerRound, first);
        Assert.Equal(3, second);
        Assert.Empty(store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_StartedPruner_When_InvoicesExpire_Then_RoundsDeleteThemUntilStopped()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, 2);
        var pruner = CreatePruner(store, TimeProvider.System,
                                  new OfferOptions { ExpiredInvoicePruneInterval = TimeSpan.FromMilliseconds(20) });

        // Act
        pruner.Start();
        pruner.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Invoices.Invoices.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        await pruner.StopAsync();
        await pruner.DisposeAsync();

        // Assert
        Assert.Empty(store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_ZeroInterval_When_Started_Then_NothingRuns()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, 1);
        await using var pruner = CreatePruner(store, TimeProvider.System,
                                              new OfferOptions { ExpiredInvoicePruneInterval = TimeSpan.Zero });

        // Act
        pruner.Start();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await pruner.StopAsync();

        // Assert
        Assert.Single(store.Invoices.Invoices);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Given_APrunedInvoice_When_ALateHtlcArrives_Then_FailsLikeTheExpiredInvoice()
    {
        // Arrange: the fix sketch of NL-448, a pruned invoice is an unknown payment hash, which fails exactly like the
        // expired invoice did (incorrect_or_unknown_payment_details)
        using var store = new OfferTestStore();
        var invoice = Bolt12Invoice(0xB1);
        await store.Invoices.AddAsync(invoice);
        var clock = new ManualClock(s_createdAt.AddSeconds(ExpirySeconds + 1) + s_grace);
        var processor = new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance, clock);
        var amount = LightningMoney.MilliSatoshis(1_000);
        var payload = new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(900),
                                     new EncryptedRecipientDataTlv(new byte[20]), new TotalAmountMsatTlv(amount));
        var recipientData = new BlindedRecipientData { PathId = BlindedPathId.Compute(invoice.Preimage) };
        var beforePrune = processor.Evaluate(await store.Invoices.GetByPaymentHashAsync(invoice.PaymentHash),
                                             invoice.PaymentHash, amount, 900, payload, 800,
                                             blindedRecipientData: recipientData);
        await using var pruner = CreatePruner(store, clock);

        // Act
        await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);
        var afterPrune = processor.Evaluate(await store.Invoices.GetByPaymentHashAsync(invoice.PaymentHash),
                                            invoice.PaymentHash, amount, 900, payload, 800,
                                            blindedRecipientData: recipientData);

        // Assert
        Assert.False(beforePrune.IsAccepted);
        Assert.False(afterPrune.IsAccepted);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, beforePrune.Failure!.Code);
        Assert.Equal(beforePrune.Failure.Code, afterPrune.Failure!.Code);
        Assert.Equal(beforePrune.Failure.Data.ToArray(), afterPrune.Failure.Data.ToArray());
    }

    [Fact]
    public async Task Given_AnHtlcSetAcceptedBeforeExpiry_When_APruneRoundRunsAfterExpiry_Then_TheSetStillSettles()
    {
        // Arrange: NL-448 review, the final hop checks the expiry only when each part arrives, so a set whose parts
        // passed the check before the expiry but completes after it (held up to the MPP timeout) must still find its
        // Open invoice at the settle, even when a prune round ran in between
        using var store = new OfferTestStore();
        var invoice = Bolt12Invoice(0xC1);
        await store.Invoices.AddAsync(invoice);
        var clock = new ManualClock(s_createdAt.AddSeconds(ExpirySeconds - 1));
        var processor = new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance, clock);
        var amount = LightningMoney.MilliSatoshis(1_000);
        var payload = new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(900),
                                     new EncryptedRecipientDataTlv(new byte[20]), new TotalAmountMsatTlv(amount));
        var recipientData = new BlindedRecipientData { PathId = BlindedPathId.Compute(invoice.Preimage) };
        var atArrival = processor.Evaluate(await store.Invoices.GetByPaymentHashAsync(invoice.PaymentHash),
                                           invoice.PaymentHash, amount, 900, payload, 800,
                                           blindedRecipientData: recipientData);
        await using var pruner = CreatePruner(store, clock);

        // Act: the set completes one MPP timeout after the expiry, with a prune round just before the settle
        clock.Advance(TimeSpan.FromSeconds(1) + HtlcSwitchOptions.DefaultMppTimeout);
        var pruned = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);
        var atSettle = await store.Invoices.GetByPaymentHashAsync(invoice.PaymentHash);

        // Assert
        Assert.True(atArrival.IsAccepted);
        Assert.Equal(0, pruned);
        Assert.NotNull(atSettle);
        Assert.Equal(InvoiceStatus.Open, atSettle.Status);
        atSettle.Accept(amount);
        atSettle.Settle(clock.GetUtcNow());
        Assert.Equal(InvoiceStatus.Settled, atSettle.Status);
    }

    [Fact]
    public async Task Given_AnInvoiceExpiredWithinTheGrace_When_PrunedOnce_Then_KeptUntilTheGraceHasPassed()
    {
        // Arrange
        using var store = new OfferTestStore();
        await AddExpiredBolt12InvoicesAsync(store, 1);
        var clock = new ManualClock(s_createdAt.AddSeconds(ExpirySeconds) + s_grace - TimeSpan.FromSeconds(1));
        await using var pruner = CreatePruner(store, clock);

        // Act
        var withinGrace = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterGrace = await pruner.PruneOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, withinGrace);
        Assert.Equal(1, afterGrace);
        Assert.Empty(store.Invoices.Invoices);
    }

    [Fact]
    public void Given_AGraceShorterThanTheMppTimeout_When_Created_Then_TheMppTimeoutIsUsed()
    {
        // Arrange
        using var store = new OfferTestStore();
        var mppTimeout = TimeSpan.FromMinutes(5);

        // Act
        var pruner = new ExpiredBolt12InvoicePruner(store.ScopeFactory, NullLogger<ExpiredBolt12InvoicePruner>.Instance,
                                                    Options.Create(new OfferOptions
                                                    {
                                                        ExpiredInvoicePruneGrace = TimeSpan.Zero
                                                    }), TimeProvider.System,
                                                    Options.Create(new HtlcSwitchOptions { MppTimeout = mppTimeout }));

        // Assert
        Assert.Equal(mppTimeout, pruner.EffectiveGrace);
    }

    [Theory]
    [InlineData(-1, 500, 3600)]
    [InlineData(600, 0, 3600)]
    [InlineData(600, 500, -1)]
    public void Given_InvalidPruneOptions_When_Validated_Then_Errors(int intervalSeconds, int batchSize,
                                                                    int graceSeconds)
    {
        // Arrange
        var options = new OfferOptions
        {
            ExpiredInvoicePruneInterval = TimeSpan.FromSeconds(intervalSeconds),
            ExpiredInvoicePruneBatchSize = batchSize,
            ExpiredInvoicePruneGrace = TimeSpan.FromSeconds(graceSeconds)
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors);
    }
}