using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddInvoicesPaymentsAndCircuits</c> (ABCD W1-C: BOLT2 N8, ONION M4-T7,
/// NL-137, NL-242), shared by the SQLite test and the Docker Postgres/SQL Server tests: a commitment snapshot written
/// with the schema before the migration loads after it (no origin, no dust policy), an origin can be stored on the
/// migrated HTLC row, and invoices, payments (with their route) and forward circuits round-trip.
/// </summary>
internal static class PaymentSchemaRoundTrip
{
    private const string MigrationName = "_AddInvoicesPaymentsAndCircuits";

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x09, 32).ToArray());
    private static readonly HtlcKey s_seededHtlc = new(HtlcDirection.Outgoing, 0);

    private static readonly CompactPubKey s_payee =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private static readonly CompactPubKey s_hop =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    /// <summary>A non-UTC offset and a sub-millisecond tick: the stored instant must be exact.</summary>
    private static readonly DateTimeOffset s_createdAt = new DateTimeOffset(2026, 9, 25, 12, 34, 56, TimeSpan.FromHours(2))
       .AddTicks(1_234_567);

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddInvoicesPaymentsAndCircuits, with a commitment snapshot in it
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await SeedAsync(context, databaseType, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded rows moved forward untouched, with the new columns empty
        var @params = new CommitmentParams(true, 1_000_000, false, new CommitmentParty(546, 0, 1, 30, 0),
                                           new CommitmentParty(546, 0, 1, 30, 0));
        await using (var context = contextFactory())
        {
            var channel = await context.Channels.AsNoTracking()
                                       .SingleAsync(c => c.ChannelId == s_channelId, cancellationToken);
            Assert.Null(channel.MaxDustHtlcExposureMsat);

            var htlc = await context.Htlcs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Null(htlc.OriginKind);
            Assert.Null(htlc.OriginPaymentHash);
            Assert.Null(htlc.OriginIncomingChannelId);
            Assert.Null(htlc.OriginIncomingHtlcId);
            Assert.Empty(await context.Invoices.ToListAsync(cancellationToken));
            Assert.Empty(await context.Payments.ToListAsync(cancellationToken));
            Assert.Empty(await context.PaymentHops.ToListAsync(cancellationToken));
            Assert.Empty(await context.ForwardCircuits.ToListAsync(cancellationToken));

            var repository = new ChannelStateDbRepository(context);
            var state = await repository.LoadAsync(s_channelId, @params);
            Assert.NotNull(state);
            var record = Assert.Single(state.Commitments.Htlcs.Values);
            Assert.Equal(s_seededHtlc, record.Key);
            Assert.Equal(HtlcState.SentAddHtlc, record.State);
            Assert.Equal(600_000_000UL, state.Commitments.LocalBalanceMsat);
            Assert.Null(await repository.GetHtlcOriginAsync(s_channelId, s_seededHtlc));
        }

        // Assert: the migrated HTLC row takes an origin, found again by the replay lookup
        var paymentHash = new Hash(Enumerable.Repeat((byte)0x44, 32).ToArray());
        await using (var context = contextFactory())
        {
            await new ChannelStateDbRepository(context).SetHtlcOriginAsync(s_channelId, s_seededHtlc,
                                                                           HtlcOrigin.Local(paymentHash));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new ChannelStateDbRepository(context);
            Assert.Equal(HtlcOrigin.Local(paymentHash), await repository.GetHtlcOriginAsync(s_channelId, s_seededHtlc));
            var found = Assert.Single(await repository.FindHtlcsByOriginAsync(HtlcOrigin.Local(paymentHash)));
            Assert.Equal((s_channelId, s_seededHtlc), found);
            Assert.Empty(await repository.FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(s_channelId, 0)));
        }

        // Assert: the new tables round-trip on this provider
        await AssertTablesRoundTripAsync(contextFactory, cancellationToken);
    }

    /// <summary>
    /// Stores an invoice, a payment with a two-hop route and a forward circuit, moves each through its states in later
    /// units of work, and reloads each from a fresh context after every save.
    /// </summary>
    public static async Task AssertTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        // Invoice: open -> accepted -> settled
        var invoice = CreateInvoice(0x11, LightningMoney.MilliSatoshis(50_000_123));
        await SaveAsync(contextFactory, c => new InvoiceDbRepository(c).AddAsync(invoice), cancellationToken);
        await AssertInvoiceAsync(contextFactory, invoice);

        invoice.Accept(LightningMoney.MilliSatoshis(50_000_124));
        await SaveAsync(contextFactory, c => new InvoiceDbRepository(c).UpdateAsync(invoice), cancellationToken);
        await AssertInvoiceAsync(contextFactory, invoice);

        invoice.Settle(s_createdAt.AddMinutes(1).AddTicks(3));
        await SaveAsync(contextFactory, c => new InvoiceDbRepository(c).UpdateAsync(invoice), cancellationToken);
        await AssertInvoiceAsync(contextFactory, invoice);

        // Any-amount invoice with no description
        var anyAmount = new InvoiceModel(new Hash(Enumerable.Repeat((byte)0x12, 32).ToArray()),
                                         SecretOf(0x21), SecretOf(0x22), null, null, "lnbcrt1anyamount",
                                         s_createdAt.AddSeconds(1), 60, 18);
        await SaveAsync(contextFactory, c => new InvoiceDbRepository(c).AddAsync(anyAmount), cancellationToken);
        await AssertInvoiceAsync(contextFactory, anyAmount);

        // Payment: in flight with a route -> HTLC recorded -> succeeded
        var payment = CreatePayment(0x31);
        await SaveAsync(contextFactory, c => new PaymentDbRepository(c).AddAsync(payment), cancellationToken);
        await AssertPaymentAsync(contextFactory, payment);

        payment.AddOutgoingHtlc(s_channelId, 7);
        await SaveAsync(contextFactory, c => new PaymentDbRepository(c).UpdateAsync(payment), cancellationToken);
        await AssertPaymentAsync(contextFactory, payment);

        payment.Succeed(SecretOf(0x32), s_createdAt.AddSeconds(5));
        await SaveAsync(contextFactory, c => new PaymentDbRepository(c).UpdateAsync(payment), cancellationToken);
        await AssertPaymentAsync(contextFactory, payment);

        // Payment: failed with a decoded failure
        var failed = CreatePayment(0x33);
        failed.Fail(FailureCode.IncorrectOrUnknownPaymentDetails, 1, "the payee refused it", s_createdAt.AddSeconds(9));
        await SaveAsync(contextFactory, c => new PaymentDbRepository(c).AddAsync(failed), cancellationToken);
        await AssertPaymentAsync(contextFactory, failed);

        // Forward circuit: pending -> offered -> fulfilled
        var circuit = new ForwardCircuitModel(s_channelId, 3, LightningMoney.MilliSatoshis(50_030_000), 700,
                                              new Hash(Enumerable.Repeat((byte)0x51, 32).ToArray()), SecretOf(0x52),
                                              new ShortChannelId(812_345, 678, 3),
                                              LightningMoney.MilliSatoshis(50_000_000), 660, s_createdAt);
        await SaveAsync(contextFactory, c => new ForwardCircuitDbRepository(c).AddAsync(circuit), cancellationToken);
        await AssertCircuitAsync(contextFactory, circuit);

        var outgoingChannel = new ChannelId(Enumerable.Repeat((byte)0x0A, 32).ToArray());
        circuit.AddOutgoingHtlc(outgoingChannel, 12);
        await SaveAsync(contextFactory, c => new ForwardCircuitDbRepository(c).UpdateAsync(circuit), cancellationToken);
        await AssertCircuitAsync(contextFactory, circuit);
        await using (var context = contextFactory())
        {
            var byOutgoing = await new ForwardCircuitDbRepository(context).GetByOutgoingAsync(outgoingChannel, 12);
            AssertCircuit(circuit, byOutgoing);
            Assert.Single(await new ForwardCircuitDbRepository(context).GetUnresolvedAsync());
        }

        circuit.MarkFulfilled(s_createdAt.AddSeconds(2));
        await SaveAsync(contextFactory, c => new ForwardCircuitDbRepository(c).UpdateAsync(circuit), cancellationToken);
        await AssertCircuitAsync(contextFactory, circuit);
        await using (var context = contextFactory())
            Assert.Empty(await new ForwardCircuitDbRepository(context).GetUnresolvedAsync());
    }

    internal static Secret SecretOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    internal static InvoiceModel CreateInvoice(byte seed, LightningMoney? amount, DateTimeOffset? createdAt = null) =>
        new(new Hash(Enumerable.Repeat(seed, 32).ToArray()), SecretOf((byte)(seed + 1)), SecretOf((byte)(seed + 2)),
            amount, $"invoice {seed}", $"lnbcrt500u1invoice{seed}", createdAt ?? s_createdAt, 3_600, 40);

    internal static PaymentModel CreatePayment(byte seed, DateTimeOffset? createdAt = null, int hops = 2)
    {
        PaymentHop[] route =
        [
            new(s_hop, new ShortChannelId(800_000, 1, 0), LightningMoney.MilliSatoshis(20_002_001), 850,
                SecretOf((byte)(seed + 1))),
            new(s_payee, new ShortChannelId(800_001, 2, 1), LightningMoney.MilliSatoshis(20_000_000), 810,
                SecretOf((byte)(seed + 2)))
        ];

        return new PaymentModel(new Hash(Enumerable.Repeat(seed, 32).ToArray()), $"lnbcrt200u1payment{seed}",
                                s_payee, LightningMoney.MilliSatoshis(20_000_000), LightningMoney.MilliSatoshis(2_001),
                                createdAt ?? s_createdAt, route[^hops..]);
    }

    internal static void AssertInvoice(InvoiceModel expected, InvoiceModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.Preimage, actual.Preimage);
        Assert.Equal(expected.PaymentSecret, actual.PaymentSecret);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Bolt11, actual.Bolt11);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
        Assert.Equal(expected.ExpirySeconds, actual.ExpirySeconds);
        Assert.Equal(expected.MinFinalCltvExpiry, actual.MinFinalCltvExpiry);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.AmountReceived, actual.AmountReceived);
        Assert.Equal(expected.SettledAt, actual.SettledAt);
    }

    internal static void AssertPayment(PaymentModel expected, PaymentModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.Bolt11, actual.Bolt11);
        Assert.Equal(expected.PayeeNodeId, actual.PayeeNodeId);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Fee, actual.Fee);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.OutgoingChannelId, actual.OutgoingChannelId);
        Assert.Equal(expected.OutgoingHtlcId, actual.OutgoingHtlcId);
        Assert.Equal(expected.Preimage, actual.Preimage);
        Assert.Equal(expected.FailureCode, actual.FailureCode);
        Assert.Equal(expected.FailureSourceIndex, actual.FailureSourceIndex);
        Assert.Equal(expected.FailureReason, actual.FailureReason);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.Route, actual.Route);
        Assert.Equal(expected.HopSharedSecrets, actual.HopSharedSecrets);
    }

    internal static void AssertCircuit(ForwardCircuitModel expected, ForwardCircuitModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.IncomingChannelId, actual.IncomingChannelId);
        Assert.Equal(expected.IncomingHtlcId, actual.IncomingHtlcId);
        Assert.Equal(expected.IncomingAmount, actual.IncomingAmount);
        Assert.Equal(expected.IncomingCltvExpiry, actual.IncomingCltvExpiry);
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.IncomingSharedSecret, actual.IncomingSharedSecret);
        Assert.Equal(expected.OutgoingShortChannelId, actual.OutgoingShortChannelId);
        Assert.Equal(expected.OutgoingAmount, actual.OutgoingAmount);
        Assert.Equal(expected.OutgoingCltvExpiry, actual.OutgoingCltvExpiry);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.OutgoingChannelId, actual.OutgoingChannelId);
        Assert.Equal(expected.OutgoingHtlcId, actual.OutgoingHtlcId);
        Assert.Equal(expected.ResolvedAt, actual.ResolvedAt);
        Assert.Equal(expected.Fee, actual.Fee);
    }

    private static async Task SaveAsync(Func<NLightningDbContext> contextFactory,
                                        Func<NLightningDbContext, Task> stage, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        await stage(context);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task AssertInvoiceAsync(Func<NLightningDbContext> contextFactory, InvoiceModel expected)
    {
        await using var context = contextFactory();
        AssertInvoice(expected, await new InvoiceDbRepository(context).GetByPaymentHashAsync(expected.PaymentHash));
        var listed = await new InvoiceDbRepository(context).ListAsync(0, 100);
        AssertInvoice(expected, listed.SingleOrDefault(i => i.PaymentHash == expected.PaymentHash));
    }

    private static async Task AssertPaymentAsync(Func<NLightningDbContext> contextFactory, PaymentModel expected)
    {
        await using var context = contextFactory();
        AssertPayment(expected, await new PaymentDbRepository(context).GetByPaymentHashAsync(expected.PaymentHash));
        var listed = await new PaymentDbRepository(context).ListAsync(0, 100);
        AssertPayment(expected, listed.SingleOrDefault(p => p.PaymentHash == expected.PaymentHash));
        var inFlight = await new PaymentDbRepository(context).GetInFlightAsync();
        Assert.Equal(expected.Status == PaymentStatus.InFlight,
                     inFlight.Any(p => p.PaymentHash == expected.PaymentHash));
    }

    private static async Task AssertCircuitAsync(Func<NLightningDbContext> contextFactory,
                                                 ForwardCircuitModel expected)
    {
        await using var context = contextFactory();
        AssertCircuit(expected,
                      await new ForwardCircuitDbRepository(context).GetByIncomingAsync(expected.IncomingChannelId,
                                                                                        expected.IncomingHtlcId));
    }

    private static async Task SeedAsync(NLightningDbContext context, DatabaseType databaseType,
                                        CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var remoteNodeId = new byte[33];
        remoteNodeId[0] = 0x02;
        remoteNodeId[32] = 0x09;
        var point = new byte[33];
        point[0] = 0x03;

        // A funded channel whose snapshot has one outgoing HTLC we have not signed yet (SentAddHtlc)
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Channels",
                       ("ChannelId", "{0}"), ("FundingCreatedAtBlockHeight", "100"), ("FundingTxId", "{1}"),
                       ("FundingOutputIndex", "0"), ("FundingAmountSatoshis", "1000000"),
                       ("IsInitiator", sql.Bool(true)), ("RemoteNodeId", "{2}"), ("LocalNextHtlcId", "1"),
                       ("RemoteNextHtlcId", "0"), ("LocalRevocationNumber", "0"), ("RemoteRevocationNumber", "0"),
                       ("LocalCommitmentNumber", "0"), ("RemoteCommitmentNumber", "0"), ("State", "40"),
                       ("Version", "1"), ("LocalBalanceMsat", "600000000"), ("RemoteBalanceMsat", "400000000"),
                       ("RemoteNextPerCommitmentPoint", "{3}"), ("LastSentOrder", "0"),
                       ("DataLossDetected", sql.Bool(false))),
            [(byte[])s_channelId, new byte[32], remoteNodeId, point], cancellationToken);

        // Local (slot 0, no signature yet) and remote (slot 1) commitments number 0, no HTLC in either
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Commitments",
                       ("ChannelId", "{0}"), ("Slot", "0"), ("Number", "0"), ("FeeratePerKw", "253"),
                       ("LocalMsat", "600000000"), ("RemoteMsat", "400000000"), ("Htlcs", "{1}")),
            [(byte[])s_channelId, Array.Empty<byte>()], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Commitments",
                       ("ChannelId", "{0}"), ("Slot", "1"), ("Number", "0"), ("FeeratePerKw", "253"),
                       ("LocalMsat", "600000000"), ("RemoteMsat", "400000000"), ("Htlcs", "{1}"),
                       ("PerCommitmentPoint", "{2}")),
            [(byte[])s_channelId, Array.Empty<byte>(), point], cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("FeeUpdates",
                       ("ChannelId", "{0}"), ("Sequence", "0"), ("FeeratePerKw", "253"),
                       ("State", $"{(byte)HtlcState.SentAddAckRevocation}")),
            [(byte[])s_channelId], cancellationToken);

        var onion = Enumerable.Range(0, 1366).Select(i => (byte)(i * 3)).ToArray();
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Htlcs",
                       ("ChannelId", "{0}"), ("HtlcId", "0"), ("Direction", $"{(byte)HtlcDirection.Outgoing}"),
                       ("AmountMsat", "5000000"), ("PaymentHash", "{1}"), ("CltvExpiry", "600"),
                       ("State", $"{(byte)HtlcState.SentAddHtlc}"), ("OnionRoutingPacket", "{2}")),
            [(byte[])s_channelId, Enumerable.Repeat((byte)0x44, 32).ToArray(), onion], cancellationToken);
    }
}