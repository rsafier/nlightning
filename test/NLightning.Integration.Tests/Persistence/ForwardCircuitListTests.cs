using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// The <c>listforwards</c> query of <see cref="ForwardCircuitDbRepository"/> (NL-597): the filters (time range,
/// status, channel by incoming id, outgoing id or scid), the newest-first page and the aggregates over the whole
/// filtered set run in the database.
/// </summary>
public class ForwardCircuitListTests
{
    internal static readonly ChannelId IncomingA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    internal static readonly ChannelId IncomingB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());
    internal static readonly ChannelId OutgoingC = new(Enumerable.Repeat((byte)0xC3, 32).ToArray());
    internal static readonly ShortChannelId ScidC = new(800_000, 12, 0);
    internal static readonly ShortChannelId ScidOther = new(900_000, 5, 1);

    [Fact]
    public async Task Given_CircuitsAcrossTimeAndStatus_When_Listed_Then_NewestFirstPagedAndFiltered()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();
        var repository = new ForwardCircuitDbRepository(context);
        var baseTime = BaseTime;
        Seed(context, IncomingA, 0, baseTime, ForwardCircuitStatus.Fulfilled, scid: ScidC);
        Seed(context, IncomingA, 1, baseTime.AddMinutes(1), ForwardCircuitStatus.Failed);
        Seed(context, IncomingB, 0, baseTime.AddMinutes(2), ForwardCircuitStatus.Offered, scid: ScidC);
        Seed(context, IncomingB, 1, baseTime.AddMinutes(3), ForwardCircuitStatus.Pending, scid: ScidOther);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act: newest first
        var all = await repository.ListAsync(new ForwardCircuitListQuery(0, 100), TestContext.Current.CancellationToken);

        // Assert: newest first, B/1 (3 min), B/0 (2 min), A/1 (1 min), A/0
        Assert.Equal([1UL, 0, 1, 0], all.Select(f => f.IncomingHtlcId));
        Assert.Equal([IncomingB, IncomingB, IncomingA, IncomingA],
                     all.Select(f => f.IncomingChannelId));

        // paging: the second page of two
        var page = await repository.ListAsync(new ForwardCircuitListQuery(2, 2), TestContext.Current.CancellationToken);
        Assert.Equal([1UL, 0], page.Select(f => f.IncomingHtlcId));

        // status filter
        var failed = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, Status: ForwardCircuitStatus.Failed),
                                   TestContext.Current.CancellationToken);
        var single = Assert.Single(failed);
        Assert.Equal((IncomingA, 1UL), (single.IncomingChannelId, single.IncomingHtlcId));

        // time range: from the second minute on / up to the first minute
        var since = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, Since: baseTime.AddMinutes(1)),
                                   TestContext.Current.CancellationToken);
        Assert.Equal([1UL, 0, 1], since.Select(f => f.IncomingHtlcId));
        var until = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, Until: baseTime.AddMinutes(1)),
                                   TestContext.Current.CancellationToken);
        Assert.Equal([1UL, 0], until.Select(f => f.IncomingHtlcId));
    }

    [Fact]
    public async Task Given_AChannelFilter_When_Listed_Then_ItMatchesIncomingOutgoingAndScid()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();
        var repository = new ForwardCircuitDbRepository(context);
        var now = DateTimeOffset.UtcNow;
        Seed(context, IncomingA, 0, now, ForwardCircuitStatus.Fulfilled, scid: ScidC);
        Seed(context, OutgoingC, 7, now.AddSeconds(1), ForwardCircuitStatus.Failed,
             outgoingChannelId: OutgoingC, scid: ScidOther);
        Seed(context, IncomingB, 0, now.AddSeconds(2), ForwardCircuitStatus.Offered,
             outgoingChannelId: IncomingB, scid: ScidOther);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act & Assert: the incoming channel of the first and the outgoing channel of the second
        var byId = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, ChannelId: OutgoingC),
                                   TestContext.Current.CancellationToken);
        Assert.Equal(2, byId.Count);
        Assert.Contains(byId, f => f.IncomingChannelId == IncomingA);
        Assert.Contains(byId, f => f.IncomingChannelId == OutgoingC && f.OutgoingChannelId == OutgoingC);

        // the scid matches the requested outgoing side even when the channel was never resolved
        var byScid = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, ChannelScid: ForwardCircuitListTests.ScidC),
                                   TestContext.Current.CancellationToken);
        var byScidSingle = Assert.Single(byScid);
        Assert.Equal(IncomingA, byScidSingle.IncomingChannelId);
    }

    [Fact]
    public async Task Given_CircuitsOfEveryStatus_When_Summarized_Then_TheCountsAndFeesCoverTheWholeFilteredSet()
    {
        // Arrange: two fulfilled (fees 100 and 200 msat), one failed, one offered, one pending; the circuit outside
        // the filter must not count
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = db.CreateDbContext();
        var repository = new ForwardCircuitDbRepository(context);
        var now = DateTimeOffset.UtcNow;
        Seed(context, IncomingA, 0, now, ForwardCircuitStatus.Fulfilled, inMsat: 1_100, outMsat: 1_000);
        Seed(context, IncomingA, 1, now, ForwardCircuitStatus.Fulfilled, inMsat: 1_200, outMsat: 1_000);
        Seed(context, IncomingA, 2, now, ForwardCircuitStatus.Failed, inMsat: 900, outMsat: 800);
        Seed(context, IncomingB, 0, now, ForwardCircuitStatus.Offered, inMsat: 500, outMsat: 450);
        Seed(context, IncomingB, 1, now, ForwardCircuitStatus.Pending, inMsat: 700, outMsat: 650);
        Seed(context, IncomingB, 2, now.AddSeconds(10), ForwardCircuitStatus.Fulfilled, inMsat: 9_999,
             outMsat: 9_000);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act: everything but the last circuit (created 10 s later); fees over the two fulfilled inside
        var totals = await repository.SummarizeAsync(new ForwardCircuitListQuery(0, 100, Until: now.AddSeconds(5)),
                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, totals.Pending);
        Assert.Equal(1, totals.Offered);
        Assert.Equal(2, totals.Fulfilled);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(5, totals.Total);
        Assert.Equal(300, totals.FulfilledFeesMsat);
    }

    internal static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Stages one circuit row through the model and repository, exactly as the switch's saves would.</summary>
    internal static void Seed(NLightningDbContext context, ChannelId incomingChannelId, ulong incomingHtlcId,
                             DateTimeOffset createdAt, ForwardCircuitStatus status, long inMsat = 1_000_000,
                             long outMsat = 900_000, ChannelId? outgoingChannelId = null,
                             ShortChannelId? scid = null)
    {
        var paymentHash = new Hash(Enumerable.Repeat((byte)(incomingHtlcId + 1), 32).ToArray());
        var sharedSecret = new Secret(Enumerable.Repeat((byte)(incomingHtlcId + 2), 32).ToArray());
        var circuit = new ForwardCircuitModel(incomingChannelId, incomingHtlcId,
                                              LightningMoney.MilliSatoshis((ulong)inMsat), 900, paymentHash,
                                              sharedSecret, scid ?? ScidOther,
                                              LightningMoney.MilliSatoshis((ulong)outMsat), 800, createdAt);
        if (status != ForwardCircuitStatus.Pending)
            circuit.AddOutgoingHtlc(outgoingChannelId ?? OutgoingC, 42);
        if (status == ForwardCircuitStatus.Fulfilled)
            circuit.MarkFulfilled(createdAt);
        if (status == ForwardCircuitStatus.Failed)
            circuit.MarkFailed(createdAt);

        new ForwardCircuitDbRepository(context).AddAsync(circuit).GetAwaiter().GetResult();
    }
}

/// <summary>
/// The shared assertion that <c>Docker/PostgresTests</c> runs against a real server (NL-597).
/// </summary>
internal static class ForwardCircuitListRoundTrip
{
    /// <summary>
    /// The same query against a real server (the shared Postgres path of <c>Docker/PostgresTests</c>, NL-597): seeds
    /// a small set, then asserts the newest-first page, the channel filter and the aggregates.
    /// </summary>
    public static async Task AssertListOnServerAsync(Func<NLightningDbContext> contextFactory,
                                                     CancellationToken cancellationToken)
    {
        await using (var context = contextFactory())
        {
            ForwardCircuitListTests.Seed(context, ForwardCircuitListTests.IncomingA, 0,
                                         ForwardCircuitListTests.BaseTime, ForwardCircuitStatus.Fulfilled,
                                         inMsat: 1_100, outMsat: 1_000, scid: ForwardCircuitListTests.ScidC);
            ForwardCircuitListTests.Seed(context, ForwardCircuitListTests.IncomingA, 1,
                                         ForwardCircuitListTests.BaseTime.AddMinutes(1),
                                         ForwardCircuitStatus.Failed);
            ForwardCircuitListTests.Seed(context, ForwardCircuitListTests.IncomingB, 0,
                                         ForwardCircuitListTests.BaseTime.AddMinutes(2),
                                         ForwardCircuitStatus.Offered, scid: ForwardCircuitListTests.ScidOther);
            await context.SaveChangesAsync(cancellationToken);
        }

        var repository = new ForwardCircuitDbRepository(contextFactory());
        var all = await repository.ListAsync(new ForwardCircuitListQuery(0, 100), cancellationToken);
        Assert.Equal([0UL, 1, 0], all.Select(f => f.IncomingHtlcId));

        var fulfilled = await repository.ListAsync(new ForwardCircuitListQuery(0, 100,
            Status: ForwardCircuitStatus.Fulfilled), cancellationToken);
        Assert.Single(fulfilled, f => f.IncomingHtlcId == 0 && f.IncomingChannelId == ForwardCircuitListTests.IncomingA);

        var byScid = await repository.ListAsync(new ForwardCircuitListQuery(0, 100, ChannelScid: ForwardCircuitListTests.ScidC),
                                                cancellationToken);
        Assert.Single(byScid, f => f.IncomingChannelId == ForwardCircuitListTests.IncomingA);

        var totals = await repository.SummarizeAsync(new ForwardCircuitListQuery(0, 100), cancellationToken);
        Assert.Equal((0, 1, 1, 1, 100), (totals.Pending, totals.Offered, totals.Fulfilled, totals.Failed,
                                         totals.FulfilledFeesMsat));
    }
}