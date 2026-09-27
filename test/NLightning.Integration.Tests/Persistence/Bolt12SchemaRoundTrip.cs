using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddBolt12Offers</c> (NL-447, BOLT 12 plan B2), shared by the SQLite test
/// and the Docker Postgres/SQL Server tests: a BOLT 11 invoice and a payment written with the schema before the
/// migration load after it unchanged (<c>Kind</c> 0, their BOLT 11 string kept, no BOLT 12 side); then offers, BOLT 12
/// invoices (no BOLT 11 string) and BOLT 12 payments round-trip every field, an offer is found by its exact bytes,
/// disabled and listed, the per-offer and node-wide invoice counts see only open, unexpired and accepted BOLT 12 rows,
/// and the expired open BOLT 12 rows are pruned, oldest expiry first and at most the given number.
/// </summary>
internal static class Bolt12SchemaRoundTrip
{
    private const string MigrationName = "_AddBolt12Offers";

    /// <summary>A non-UTC offset and a sub-millisecond tick: the stored instants must be exact.</summary>
    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 9, 27, 9, 15, 30, TimeSpan.FromHours(-4))
       .AddTicks(7_654_321);

    private static readonly CompactPubKey s_payerId =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddBolt12Offers, with a BOLT 11 invoice and a payment in it
        var legacyInvoice = PaymentSchemaRoundTrip.CreateInvoice(0x71, LightningMoney.MilliSatoshis(50_000_001));
        var legacyPayment = PaymentSchemaRoundTrip.CreatePayment(0x81);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await SeedAsync(context, databaseType, legacyInvoice, legacyPayment, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded rows are BOLT 11 rows with their string kept
        await using (var context = contextFactory())
        {
            var invoiceRow = await context.Invoices.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal((byte)InvoiceKind.Bolt11, invoiceRow.Kind);
            Assert.Null(invoiceRow.OfferId);

            var invoice = await new InvoiceDbRepository(context).GetByPaymentHashAsync(legacyInvoice.PaymentHash);
            PaymentSchemaRoundTrip.AssertInvoice(legacyInvoice, invoice);
            Assert.Equal(InvoiceKind.Bolt11, invoice!.Kind);
            Assert.Null(invoice.Bolt12);

            var payment = await new PaymentDbRepository(context).GetByPaymentHashAsync(legacyPayment.PaymentHash);
            PaymentSchemaRoundTrip.AssertPayment(legacyPayment, payment);
            Assert.Null(payment!.Bolt12);
        }

        await AssertTablesRoundTripAsync(contextFactory, cancellationToken);
    }

    /// <summary>
    /// Offers, BOLT 12 invoices and BOLT 12 payments round-trip on a migrated database (no BOLT 12 rows in it yet).
    /// </summary>
    public static async Task AssertTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        // Arrange: a full offer (every optional field, the largest quantity) and a minimal one
        var full = CreateOffer(0x01, LightningMoney.MilliSatoshis(123_456_789), "USD", "coffee ☕", "Café ACME",
                               ulong.MaxValue, s_now.AddDays(30), OfferIssuerKind.NodeId, true, s_now);
        var minimal = CreateOffer(0x02, null, null, null, null, null, null, OfferIssuerKind.BlindedPaths, true,
                                  s_now.AddMinutes(1));
        await using (var context = contextFactory())
        {
            var repository = new OfferDbRepository(context);
            await repository.AddAsync(full);
            await repository.AddAsync(minimal);

            // Staged rows are visible to the key reads of the same unit of work
            AssertOffer(full, await repository.GetByIdAsync(full.OfferId));
            AssertOffer(minimal, await repository.GetByOfferBytesAsync(minimal.OfferBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(full));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field, by id and by exact bytes
        await using (var context = contextFactory())
        {
            var repository = new OfferDbRepository(context);
            AssertOffer(full, await repository.GetByIdAsync(full.OfferId));
            AssertOffer(minimal, await repository.GetByIdAsync(minimal.OfferId));
            AssertOffer(full, await repository.GetByOfferBytesAsync(full.OfferBytes));
            Assert.Null(await repository.GetByIdAsync(new Hash(Enumerable.Repeat((byte)0xEE, 32).ToArray())));

            var otherBytes = full.OfferBytes.ToArray();
            otherBytes[^1] ^= 0x01;
            Assert.Null(await repository.GetByOfferBytesAsync(otherBytes));
            Assert.Null(await repository.GetByOfferBytesAsync(ReadOnlyMemory<byte>.Empty));

            // Newest first, paged
            Assert.Equal([minimal.OfferId, full.OfferId],
                         (await repository.ListAsync(false, 0, 10)).Select(o => o.OfferId));
            Assert.Equal([full.OfferId], (await repository.ListAsync(false, 1, 10)).Select(o => o.OfferId));
            Assert.Empty(await repository.ListAsync(false, 0, 0));
        }

        // Act: disable the minimal offer
        var disabledAt = s_now.AddHours(1).AddTicks(3);
        await using (var context = contextFactory())
        {
            var repository = new OfferDbRepository(context);
            var offer = await repository.GetByIdAsync(minimal.OfferId);
            offer!.Disable(disabledAt);
            await repository.UpdateAsync(offer);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new OfferDbRepository(context);
            var disabled = await repository.GetByIdAsync(minimal.OfferId);
            Assert.Equal(OfferStatus.Disabled, disabled!.Status);
            Assert.Equal(disabledAt, disabled.DisabledAt);
            Assert.Equal([full.OfferId], (await repository.ListAsync(true, 0, 10)).Select(o => o.OfferId));
            Assert.Equal(2, (await repository.ListAsync(false, 0, 10)).Count);
        }

        await AssertBolt12InvoicesAsync(contextFactory, full, minimal, cancellationToken);
        await AssertBolt12PaymentsAsync(contextFactory, full, cancellationToken);
    }

    private static async Task AssertBolt12InvoicesAsync(Func<NLightningDbContext> contextFactory, OfferModel offer,
                                                        OfferModel otherOffer, CancellationToken cancellationToken)
    {
        // Arrange: for the offer one settled, one open, one expired, one canceled and one accepted (an HTLC set held,
        // past its expiry) BOLT 12 invoice; one open and one short-lived for the other offer; one BOLT 11 invoice
        // (never counted)
        var invoiceBytes = Enumerable.Range(0, 1_500).Select(i => (byte)(i * 7)).ToArray();
        var open = CreateBolt12Invoice(0x10, offer.OfferId, invoiceBytes, ulong.MaxValue, "for the café ☕", s_now);
        var settled = CreateBolt12Invoice(0x20, offer.OfferId, [0x01], null, null, s_now.AddMinutes(-10));
        var expired = CreateBolt12Invoice(0x30, offer.OfferId, [0x02], 2, null, s_now.AddHours(-2));
        var canceled = CreateBolt12Invoice(0x40, offer.OfferId, [0x03], null, null, s_now.AddMinutes(-5));
        var otherOpen = CreateBolt12Invoice(0x50, otherOffer.OfferId, [0x04], null, null, s_now.AddMinutes(-1));
        var accepted = CreateBolt12Invoice(0x15, offer.OfferId, [0x06], null, null, s_now.AddHours(-3));
        var shortLived = CreateBolt12Invoice(0x16, otherOffer.OfferId, [0x07], null, null, s_now.AddSeconds(-59), 60);
        var bolt11 = PaymentSchemaRoundTrip.CreateInvoice(0x60, null, s_now);
        settled.Accept(LightningMoney.MilliSatoshis(123_456_789));
        canceled.Cancel();
        accepted.Accept(LightningMoney.MilliSatoshis(1_000));
        var all = new[] { open, settled, expired, canceled, accepted, otherOpen, shortLived, bolt11 };
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            foreach (var invoice in all)
                await repository.AddAsync(invoice);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act: settle the accepted one
        var settledAt = s_now.AddMinutes(-9).AddTicks(11);
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            var stored = await repository.GetByPaymentHashAsync(settled.PaymentHash);
            stored!.Settle(settledAt);
            await repository.UpdateAsync(stored);
            await context.SaveChangesAsync(cancellationToken);
        }

        settled.Settle(settledAt);

        // Assert: every field, BOLT 11 string absent
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            foreach (var expected in all)
                AssertInvoice(expected, await repository.GetByPaymentHashAsync(expected.PaymentHash));

            var listed = await repository.ListAsync(0, 100);
            Assert.Null(listed.Single(i => i.PaymentHash == open.PaymentHash).Bolt11);

            var offers = new OfferDbRepository(context);
            Assert.Equal(new OfferInvoiceCounts(1, 2), await offers.GetInvoiceCountsAsync(offer.OfferId, s_now));
            Assert.Equal(new OfferInvoiceCounts(0, 2), await offers.GetInvoiceCountsAsync(otherOffer.OfferId, s_now));
            Assert.Equal(4, await offers.CountUnpaidInvoicesAsync(s_now));

            // An open invoice counts as unpaid until the instant it expires (per expiry length); an accepted one always
            Assert.Equal(new OfferInvoiceCounts(1, 1),
                         await offers.GetInvoiceCountsAsync(offer.OfferId, open.ExpiresAt));
            Assert.Equal(2, await offers.CountUnpaidInvoicesAsync(open.ExpiresAt.AddTicks(-1)));
            Assert.Equal(new OfferInvoiceCounts(0, 2),
                         await offers.GetInvoiceCountsAsync(otherOffer.OfferId, shortLived.ExpiresAt.AddTicks(-1)));
            Assert.Equal(new OfferInvoiceCounts(0, 1),
                         await offers.GetInvoiceCountsAsync(otherOffer.OfferId, shortLived.ExpiresAt));
        }

        await AssertPruneAsync(contextFactory, offer, otherOffer, all, cancellationToken);

        // Assert: a BOLT 12 invoice needs an offer we stored (foreign key)
        await using (var context = contextFactory())
        {
            var orphan = CreateBolt12Invoice(0x70, new Hash(Enumerable.Repeat((byte)0xAB, 32).ToArray()), [0x05],
                                             null, null, s_now);
            await new InvoiceDbRepository(context).AddAsync(orphan);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }
    }

    /// <summary>
    /// The expired open BOLT 12 invoices are pruned (staged, oldest expiry first, at most the given number); BOLT 11,
    /// accepted, settled and canceled rows stay, and so does a row this unit of work already accepted.
    /// </summary>
    private static async Task AssertPruneAsync(Func<NLightningDbContext> contextFactory, OfferModel offer,
                                               OfferModel otherOffer, InvoiceModel[] all,
                                               CancellationToken cancellationToken)
    {
        // all: open, settled, expired, canceled, accepted, otherOpen, shortLived, bolt11
        var (open, expired, otherOpen, shortLived) = (all[0], all[2], all[5], all[6]);

        // Act: at shortLived's expiry both it and `expired` (which expired at s_now) are expired; prune one
        var at = shortLived.ExpiresAt;
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            Assert.Equal(0, await repository.PruneExpiredBolt12InvoicesAsync(at, 0));
            Assert.Equal(1, await repository.PruneExpiredBolt12InvoicesAsync(at, 1));

            // Staged only
            await using (var other = contextFactory())
                Assert.NotNull(await new InvoiceDbRepository(other).GetByPaymentHashAsync(expired.PaymentHash));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: the oldest expiry went first
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            Assert.Null(await repository.GetByPaymentHashAsync(expired.PaymentHash));
            Assert.NotNull(await repository.GetByPaymentHashAsync(shortLived.PaymentHash));
            Assert.Equal(0, await repository.PruneExpiredBolt12InvoicesAsync(at.AddTicks(-1), 10));
            Assert.Equal(1, await repository.PruneExpiredBolt12InvoicesAsync(at, 10));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act: a day later every open BOLT 12 row is expired; accept one in this unit of work before pruning
        var later = open.ExpiresAt.AddDays(1);
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            var stillOpen = await repository.GetByPaymentHashAsync(otherOpen.PaymentHash);
            stillOpen!.Accept(LightningMoney.MilliSatoshis(2_000));
            await repository.UpdateAsync(stillOpen);
            Assert.Equal(1, await repository.PruneExpiredBolt12InvoicesAsync(later, 10));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: only the open one went; the rest stays and counts
        await using (var context = contextFactory())
        {
            var repository = new InvoiceDbRepository(context);
            Assert.Null(await repository.GetByPaymentHashAsync(open.PaymentHash));
            Assert.Null(await repository.GetByPaymentHashAsync(shortLived.PaymentHash));
            var remaining = (await repository.ListAsync(0, 100)).Select(i => i.PaymentHash).ToHashSet();
            foreach (var kept in new[] { all[1], all[3], all[4], otherOpen, all[7] })
                Assert.Contains(kept.PaymentHash, remaining);
            Assert.Equal(0, await repository.PruneExpiredBolt12InvoicesAsync(later, 10));

            var offers = new OfferDbRepository(context);
            Assert.Equal(new OfferInvoiceCounts(1, 1), await offers.GetInvoiceCountsAsync(offer.OfferId, later));
            Assert.Equal(new OfferInvoiceCounts(0, 1), await offers.GetInvoiceCountsAsync(otherOffer.OfferId, later));
            Assert.Equal(2, await offers.CountUnpaidInvoicesAsync(later));
        }
    }

    private static async Task AssertBolt12PaymentsAsync(Func<NLightningDbContext> contextFactory, OfferModel offer,
                                                        CancellationToken cancellationToken)
    {
        // Arrange: a BOLT 12 payment without a BOLT 11 string, with and without a payer note
        var invoiceBytes = Enumerable.Range(0, 2_000).Select(i => (byte)(i * 13)).ToArray();
        var metadata = Enumerable.Range(0, 32).Select(i => (byte)(0xC0 + i)).ToArray();
        var withNote = CreateBolt12Payment(0x91, offer.Bolt12, invoiceBytes, metadata, "thanks ☕");
        var withoutNote = CreateBolt12Payment(0xA1, offer.Bolt12, [0x09], [0x0A], null);
        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            await repository.AddAsync(withNote);
            await repository.AddAsync(withoutNote);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act: fail one, then retry it as a BOLT 11 payment (the replacement drops the BOLT 12 side)
        var retry = PaymentSchemaRoundTrip.CreatePayment(0xA1, s_now.AddMinutes(5));
        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            var failed = await repository.GetByPaymentHashAsync(withoutNote.PaymentHash);
            failed!.Fail(null, null, "no route", s_now.AddMinutes(1));
            await repository.UpdateAsync(failed);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            await new PaymentDbRepository(context).AddAsync(retry);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert
        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            var stored = await repository.GetByPaymentHashAsync(withNote.PaymentHash);
            PaymentSchemaRoundTrip.AssertPayment(withNote, stored);
            AssertBolt12Payment(withNote.Bolt12!, stored!.Bolt12);

            var replaced = await repository.GetByPaymentHashAsync(retry.PaymentHash);
            PaymentSchemaRoundTrip.AssertPayment(retry, replaced);
            Assert.Null(replaced!.Bolt12);

            var listed = (await repository.ListAsync(0, 100)).Single(p => p.PaymentHash == withNote.PaymentHash);
            AssertBolt12Payment(withNote.Bolt12!, listed.Bolt12);
            Assert.Contains(await repository.GetInFlightAsync(), p => p.PaymentHash == withNote.PaymentHash
                                                                    && p.Bolt12 is not null);
        }
    }

    /// <summary>
    /// Writes, with raw SQL, a BOLT 11 invoice and a payment (usable on the schema right before
    /// <c>AddBolt12Offers</c>).
    /// </summary>
    private static async Task SeedAsync(NLightningDbContext context, DatabaseType databaseType, InvoiceModel invoice,
                                        PaymentModel payment, CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Invoices",
                       ("PaymentHash", "{0}"), ("Preimage", "{1}"), ("PaymentSecret", "{2}"),
                       ("AmountMsat", $"{invoice.Amount!.MilliSatoshi}"), ("Description", "{3}"), ("Bolt11", "{4}"),
                       ("CreatedAt", $"{invoice.CreatedAt.UtcTicks}"), ("ExpirySeconds", $"{invoice.ExpirySeconds}"),
                       ("MinFinalCltvExpiry", $"{invoice.MinFinalCltvExpiry}"), ("Status", "0")),
            [
                (byte[])invoice.PaymentHash, (byte[])invoice.Preimage, (byte[])invoice.PaymentSecret,
                invoice.Description!, invoice.Bolt11!
            ], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Payments",
                       ("PaymentHash", "{0}"), ("Bolt11", "{1}"), ("PayeeNodeId", "{2}"),
                       ("AmountMsat", $"{payment.Amount.MilliSatoshi}"), ("FeeMsat", $"{payment.Fee.MilliSatoshi}"),
                       ("CreatedAt", $"{payment.CreatedAt.UtcTicks}"), ("Status", "0")),
            [(byte[])payment.PaymentHash, payment.Bolt11!, (byte[])payment.PayeeNodeId], cancellationToken);
        for (var i = 0; i < payment.Route.Count; i++)
        {
            var hop = payment.Route[i];
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("PaymentHops",
                           ("PaymentHash", "{0}"), ("HopIndex", $"{i}"), ("NodeId", "{1}"), ("ShortChannelId", "{2}"),
                           ("AmountMsat", $"{hop.Amount.MilliSatoshi}"), ("CltvExpiry", $"{hop.CltvExpiry}"),
                           ("SharedSecret", "{3}")),
                [(byte[])payment.PaymentHash, (byte[])hop.NodeId, (byte[])hop.ShortChannelId,
                 (byte[])hop.SharedSecret], cancellationToken);
        }
    }

    internal static OfferModel CreateOffer(byte seed, LightningMoney? amount, string? currency, string? description,
                                           string? issuer, ulong? quantityMax, DateTimeOffset? absoluteExpiry,
                                           OfferIssuerKind issuerKind, bool hasPaths, DateTimeOffset createdAt)
    {
        // Stand-in TLV bytes (the codec is lane B12-A's); the id is their SHA256, as OfferModel requires
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)(seed + i)).ToArray();
        var metadata = Enumerable.Repeat((byte)(seed + 0x80), 16).ToArray();
        return new OfferModel(new Hash(SHA256.HashData(bytes)), $"lno1offer{seed:x2}", bytes, description, amount,
                              currency, issuer, quantityMax, absoluteExpiry, metadata, issuerKind, hasPaths,
                              createdAt);
    }

    internal static InvoiceModel CreateBolt12Invoice(byte seed, Hash offerId, byte[] invoiceBytes, ulong? quantity,
                                                     string? payerNote, DateTimeOffset createdAt,
                                                     uint expirySeconds = 7_200) =>
        new(new Hash(Enumerable.Repeat(seed, 32).ToArray()), PaymentSchemaRoundTrip.SecretOf((byte)(seed + 1)),
            PaymentSchemaRoundTrip.SecretOf((byte)(seed + 2)), LightningMoney.MilliSatoshis(123_456_789), "coffee",
            null, createdAt, expirySeconds, 40, bolt12: new Bolt12InvoiceDetails(offerId, invoiceBytes, s_payerId, quantity,
                                                                           payerNote));

    private static PaymentModel CreateBolt12Payment(byte seed, string offer, byte[] invoiceBytes, byte[] metadata,
                                                    string? payerNote)
    {
        var template = PaymentSchemaRoundTrip.CreatePayment(seed, s_now);
        return new PaymentModel(template.PaymentHash, null, template.PayeeNodeId, template.Amount, template.Fee,
                                template.CreatedAt, template.Route,
                                new Bolt12PaymentDetails(offer, invoiceBytes, metadata, payerNote));
    }

    private static void AssertOffer(OfferModel expected, OfferModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OfferId, actual.OfferId);
        Assert.Equal(expected.Bolt12, actual.Bolt12);
        Assert.Equal(expected.OfferBytes.ToArray(), actual.OfferBytes.ToArray());
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.Issuer, actual.Issuer);
        Assert.Equal(expected.QuantityMax, actual.QuantityMax);
        Assert.Equal(expected.AbsoluteExpiry, actual.AbsoluteExpiry);
        Assert.Equal(expected.AbsoluteExpiry?.UtcTicks, actual.AbsoluteExpiry?.UtcTicks);
        Assert.Equal(expected.Metadata.ToArray(), actual.Metadata.ToArray());
        Assert.Equal(expected.IssuerKind, actual.IssuerKind);
        Assert.Equal(expected.HasPaths, actual.HasPaths);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.DisabledAt, actual.DisabledAt);
    }

    private static void AssertInvoice(InvoiceModel expected, InvoiceModel? actual)
    {
        PaymentSchemaRoundTrip.AssertInvoice(expected, actual);
        Assert.Equal(expected.Kind, actual!.Kind);
        if (expected.Bolt12 is null)
        {
            Assert.Null(actual.Bolt12);
            return;
        }

        Assert.NotNull(actual.Bolt12);
        Assert.Null(actual.Bolt11);
        Assert.Equal(expected.Bolt12.OfferId, actual.Bolt12.OfferId);
        Assert.Equal(expected.Bolt12.InvoiceBytes.ToArray(), actual.Bolt12.InvoiceBytes.ToArray());
        Assert.Equal(expected.Bolt12.PayerId, actual.Bolt12.PayerId);
        Assert.Equal(expected.Bolt12.Quantity, actual.Bolt12.Quantity);
        Assert.Equal(expected.Bolt12.PayerNote, actual.Bolt12.PayerNote);
    }

    private static void AssertBolt12Payment(Bolt12PaymentDetails expected, Bolt12PaymentDetails? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Offer, actual.Offer);
        Assert.Equal(expected.InvoiceBytes.ToArray(), actual.InvoiceBytes.ToArray());
        Assert.Equal(expected.InvoiceRequestMetadata.ToArray(), actual.InvoiceRequestMetadata.ToArray());
        Assert.Equal(expected.PayerNote, actual.PayerNote);
    }
}