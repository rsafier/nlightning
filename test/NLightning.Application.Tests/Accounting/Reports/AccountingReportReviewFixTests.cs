namespace NLightning.Application.Tests.Accounting.Reports;

using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using static AccountingBooksTestKit;

/// <summary>
/// The fixes of the accounting live review (NL-602, 2026-10-02) in the reports: a channel known only from the cutover
/// (NL-622, NL-623), the net yield (NL-625) and the last entry of a past balance sheet (NL-627).
/// </summary>
public class AccountingReportReviewFixTests
{
    private static readonly DateTimeOffset s_cutover = T0.AddDays(50);
    private static readonly ShortChannelId s_scid = new(800_000, 25, 1);

    private readonly AccountingBooksTestKit _kit = new();

    [Fact]
    public async Task Given_AChannelKnownOnlyFromItsOpeningBalance_When_TheChannelsViewIsRead_Then_ItsDetailsAreShown()
    {
        // Arrange (NL-622): the cutover's opening balances of a channel without ChannelFunded (a dual-funded channel
        // spliced before the feed began) and of the wallet, and the cutover marker
        var channel = Channel(0x76);
        var peer = Peer(0x33);
        AddOpening(channel, peer, AccountingDetailsCodec.Create(("bucket", "channel"), ("capacitySat", "2000000"),
                                                                ("state", "Open"), ("scid", s_scid.ToString()),
                                                                ("isInitiator", "false")));
        AddOpening(null, null, AccountingDetailsCodec.Create(("bucket", "wallet")));
        AddOpening(null, null, AccountingDetailsCodec.Create(("cutoverAt", s_cutover.ToString("O"))));

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, null,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var line = Assert.Single(report.Channels);
        Assert.Equal(channel, line.ChannelId);
        Assert.Equal("800000x25x1", line.ShortChannelId);
        Assert.Equal(peer, line.Counterparty);
        Assert.Equal(2_000_000_000, line.CapacityMsat);
        Assert.False(line.IsInitiator);
        Assert.Null(line.OpenedAt);
        Assert.Equal(s_cutover, line.TrackedSince);
        Assert.Equal(2_000_000_000, Assert.Single(report.Peers).CapacityMsat);
    }

    [Fact]
    public async Task Given_AnOpeningBalanceThenASplice_When_TheChannelsViewIsRead_Then_TheSplicedCapacityWins()
    {
        // Arrange (NL-622): the opening's capacity is only the fallback
        var channel = Channel(0x76);
        AddOpening(channel, Peer(0x33), AccountingDetailsCodec.Create(("capacitySat", "2000000"),
                                                                      ("isInitiator", "true")));
        _kit.Add(AccountingEventKind.SpliceLocked, s_cutover.AddDays(5), 500_000_000, 1_000, channel, Peer(0x33),
                 AccountingDetailsCodec.Create(("capacitySat", "2500000"), ("isInitiator", "false")));

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, channel,
                                                                       TestContext.Current.CancellationToken);

        // Assert: the splice attempt's initiator is not the channel's opener
        var line = Assert.Single(report.Channels);
        Assert.Equal(2_500_000_000, line.CapacityMsat);
        Assert.True(line.IsInitiator);
        Assert.Equal(1_000, line.SpliceFeeMsat);
    }

    [Fact]
    public async Task Given_AMemoFundingWithoutABlockTimeSource_When_TheChannelsViewIsRead_Then_TheCutoverIsNotTheOpenTime()
    {
        // Arrange (NL-623): the backfill's memo ChannelFunded is dated at the cutover
        var channel = Channel(0xA1);
        AddOpening(channel, Peer(0x11), AccountingDetailsCodec.Create(("capacitySat", "1000000")));
        AddMemoFunding(channel);
        _kit.Add(AccountingEventKind.ForwardSettled, s_cutover.AddDays(20), 1_000, 0, Channel(0xB2), Peer(0x22),
                 AccountingDetailsCodec.Create(("incomingChannelId", Channel(0xB2).ToString()),
                                               ("outgoingChannelId", channel.ToString())));
        var reports = _kit.CreateReports();

        // Act
        var whole = await reports.GetChannelsReportAsync(null, null, channel, TestContext.Current.CancellationToken);
        var afterCutover = await reports.GetChannelsReportAsync(s_cutover.AddDays(10), null, channel,
                                                                TestContext.Current.CancellationToken);

        // Assert: open since the feed began, funded at block 800,000; no annualized yield over an unknown window
        var line = Assert.Single(whole.Channels);
        Assert.Null(line.OpenedAt);
        Assert.Equal(s_cutover, line.TrackedSince);
        Assert.Equal(800_000u, line.OpenedAtBlockHeight);
        Assert.Equal(1_000d / 1_000_000_000, line.YieldOnCapacity!.Value, 12);
        Assert.Null(line.AnnualizedYield);
        Assert.Null(line.NetAnnualizedYield);

        // A period that starts after the feed began is fully known: days 60 to 100, 40 days
        var period = Assert.Single(afterCutover.Channels);
        Assert.Equal(1_000d / 1_000_000_000 * (365.25 / 40), period.AnnualizedYield!.Value, 10);
    }

    [Fact]
    public async Task Given_AMemoFundingAndItsBlockTime_When_TheChannelsViewIsRead_Then_TheBlockDatesTheOpen()
    {
        // Arrange (NL-623)
        var channel = Channel(0xA1);
        AddOpening(channel, Peer(0x11), AccountingDetailsCodec.Create(("capacitySat", "1000000")));
        AddMemoFunding(channel);
        _kit.Add(AccountingEventKind.ForwardSettled, s_cutover.AddDays(20), 1_000, 0, Channel(0xB2), Peer(0x22),
                 AccountingDetailsCodec.Create(("incomingChannelId", Channel(0xB2).ToString()),
                                               ("outgoingChannelId", channel.ToString())));
        var blockTimes = new Mock<IBlockTimeSource>();
        blockTimes.Setup(b => b.GetBlockTimeAsync(800_000, It.IsAny<CancellationToken>())).ReturnsAsync(T0);

        // Act
        var report = await _kit.CreateReports(blockTimes: blockTimes.Object)
                               .GetChannelsReportAsync(null, null, channel, TestContext.Current.CancellationToken);

        // Assert: open from the funding block (day 0) to now (day 100)
        var line = Assert.Single(report.Channels);
        Assert.Equal(T0, line.OpenedAt);
        Assert.Equal(s_cutover, line.TrackedSince);
        Assert.Equal(1_000d / 1_000_000_000 * (365.25 / 100), line.AnnualizedYield!.Value, 10);
    }

    [Fact]
    public async Task Given_OnlyAMemoForceCloseOfASplicedChannel_When_TheChannelsViewIsRead_Then_ItDatesItsLife()
    {
        // Arrange (NL-682): a spliced channel force-closed before the cutover: no ChannelFunded (its funding is the
        // splice's), only the backfill's memo ChannelForceClosed with the capacity and the original funding's block
        var channel = Channel(0xFE);
        var closedAt = s_cutover.AddDays(-5);
        var forceClosed = Event(1, AccountingEventKind.ChannelForceClosed, closedAt, channel,
                                AccountingDetailsCodec.Create(("memo", "true"), ("capacitySat", "449816"),
                                                              ("openedAtHeight", "790000"), ("funder", "true")),
                                AccountingEventFlags.Backfilled);
        _kit.AddBuilt(seq => new AccountingEventModel
        {
            EventKey = forceClosed.EventKey,
            Kind = forceClosed.Kind,
            OccurredAt = forceClosed.OccurredAt,
            BlockHeight = forceClosed.BlockHeight,
            ChannelId = forceClosed.ChannelId,
            ShortChannelId = forceClosed.ShortChannelId,
            Counterparty = forceClosed.Counterparty,
            AmountMsat = -300_000_000,
            FeeMsat = 1_340_000,
            Finality = forceClosed.Finality,
            Flags = forceClosed.Flags,
            Details = forceClosed.Details,
            LedgerSeq = seq,
            Hash = new byte[32]
        });
        var blockTimes = new Mock<IBlockTimeSource>();
        blockTimes.Setup(b => b.GetBlockTimeAsync(790_000, It.IsAny<CancellationToken>())).ReturnsAsync(T0);

        // Act
        var report = await _kit.CreateReports(blockTimes: blockTimes.Object)
                               .GetChannelsReportAsync(null, null, channel, TestContext.Current.CancellationToken);

        // Assert: capacity, initiator, open (the original funding's block) and close from the memo close
        var line = Assert.Single(report.Channels);
        Assert.Equal(449_816_000, line.CapacityMsat);
        Assert.True(line.IsInitiator);
        Assert.Equal(790_000u, line.OpenedAtBlockHeight);
        Assert.Equal(T0, line.OpenedAt);
        Assert.Equal(closedAt, line.ClosedAt);
        Assert.Equal(1_340_000, line.CommitmentFeeMsat);
    }

    [Fact]
    public async Task Given_ABlockTimeSourceThatFails_When_TheChannelsViewIsRead_Then_TheReportStillAnswers()
    {
        // Arrange (NL-623)
        var channel = Channel(0xA1);
        AddOpening(channel, Peer(0x11), AccountingDetailsCodec.Create(("capacitySat", "1000000")));
        AddMemoFunding(channel);
        var blockTimes = new Mock<IBlockTimeSource>();
        blockTimes.Setup(b => b.GetBlockTimeAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("bitcoind is down"));

        // Act
        var report = await _kit.CreateReports(blockTimes: blockTimes.Object)
                               .GetChannelsReportAsync(null, null, null, TestContext.Current.CancellationToken);

        // Assert
        var line = Assert.Single(report.Channels);
        Assert.Null(line.OpenedAt);
        Assert.Equal(s_cutover, line.TrackedSince);
    }

    [Fact]
    public async Task Given_ALiveFunding_When_TheChannelsViewIsRead_Then_ItsTimeIsTheOpenTimeAndNoBlockIsAsked()
    {
        // Arrange (NL-623): a channel funded after the cutover keeps its ChannelFunded time
        var channel = Channel(0xA1);
        _kit.AddBuilt(seq => Event(seq, AccountingEventKind.ChannelFunded, T0.AddDays(3), channel,
                                   AccountingDetailsCodec.Create(("capacitySat", "1000000")),
                                   AccountingEventFlags.None));
        var blockTimes = new Mock<IBlockTimeSource>(MockBehavior.Strict);

        // Act
        var report = await _kit.CreateReports(blockTimes: blockTimes.Object)
                               .GetChannelsReportAsync(null, null, null, TestContext.Current.CancellationToken);

        // Assert
        var line = Assert.Single(report.Channels);
        Assert.Equal(T0.AddDays(3), line.OpenedAt);
        Assert.Equal(800_000u, line.OpenedAtBlockHeight);
        Assert.Null(line.TrackedSince);
    }

    [Fact]
    public async Task Given_AFundingFeeAboveTheRoutingEarned_When_TheChannelsViewIsRead_Then_TheNetYieldIsNegative()
    {
        // Arrange (NL-625): 155,000 msat funding fee against 2,005 msat routing earned
        var channel = Channel(0xA1);
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 1_000_000_000, 155_000, channel, Peer(0x11),
                 AccountingDetailsCodec.Create(("capacitySat", "1000000")));
        _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(1), 2_005, 0, Channel(0xB2), Peer(0x22),
                 AccountingDetailsCodec.Create(("incomingChannelId", Channel(0xB2).ToString()),
                                               ("outgoingChannelId", channel.ToString())));

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, channel,
                                                                       TestContext.Current.CancellationToken);

        // Assert: the yield next to Net follows its sign; the routing yield stays apart
        var line = Assert.Single(report.Channels);
        Assert.Equal(-152_995, line.NetMsat);
        Assert.Equal(-152_995d / 1_000_000_000, line.NetYieldOnCapacity!.Value, 12);
        Assert.Equal(-152_995d / 1_000_000_000 * (365.25 / 100), line.NetAnnualizedYield!.Value, 10);
        Assert.Equal(2_005d / 1_000_000_000, line.YieldOnCapacity!.Value, 12);
        Assert.True(line.AnnualizedYield > 0);
    }

    [Fact]
    public async Task Given_APastTime_When_TheBalanceSheetIsRead_Then_ItNamesTheLastEntryItCounts()
    {
        // Arrange (NL-627): entries 1-3 on days 1-3, entry 4 on day 10, then entry 5 recorded later for day 2 (a memo)
        for (var day = 1; day <= 3; day++)
            _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(day), 100, 0, Channel(1));
        _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(10), 100, 0, Channel(1));
        _kit.Add(AccountingEventKind.InvoiceSettled, T0.AddDays(2), 100, 0, Channel(1));
        var reports = _kit.CreateReports();

        // Act
        var past = await reports.GetBalanceSheetAsync(T0.AddDays(5), TestContext.Current.CancellationToken);
        var beforeAll = await reports.GetBalanceSheetAsync(T0, TestContext.Current.CancellationToken);
        var now = await reports.GetBalanceSheetAsync(null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5, past.LastLedgerSeqAt);
        Assert.Equal(5, past.ProjectedLedgerSeq);
        Assert.Equal(0, beforeAll.LastLedgerSeqAt);
        Assert.Null(now.LastLedgerSeqAt);
    }

    private void AddOpening(ChannelId? channel, CompactPubKey? peer, IReadOnlyDictionary<string, string> details) =>
        _kit.AddBuilt(seq => new AccountingEventModel
        {
            EventKey = $"open:{seq}",
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = s_cutover,
            BlockHeight = 900_000,
            ChannelId = channel,
            ShortChannelId = channel is null ? (ShortChannelId?)null : s_scid,
            Counterparty = peer,
            AmountMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            Details = details,
            LedgerSeq = seq,
            Hash = new byte[32]
        });

    private void AddMemoFunding(ChannelId channel) =>
        _kit.AddBuilt(seq => Event(seq, AccountingEventKind.ChannelFunded, s_cutover.AddMinutes(1), channel,
                                   AccountingDetailsCodec.Create(("capacitySat", "1000000"), ("memo", "true")),
                                   AccountingEventFlags.Backfilled));

    private static AccountingEventModel Event(long seq, AccountingEventKind kind, DateTimeOffset at, ChannelId channel,
                                              IReadOnlyDictionary<string, string> details,
                                              AccountingEventFlags flags) =>
        new()
        {
            EventKey = $"test:{seq}",
            Kind = kind,
            OccurredAt = at,
            BlockHeight = 800_003,
            ChannelId = channel,
            ShortChannelId = new ShortChannelId(800_000, 25, 1),
            Counterparty = Peer(0x11),
            Finality = AccountingFinality.Confirmed,
            Flags = flags,
            Details = details,
            LedgerSeq = seq,
            Hash = new byte[32]
        };
}