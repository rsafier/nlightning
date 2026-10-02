namespace NLightning.Application.Tests.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Services;
using static AccountingBooksTestKit;

/// <summary>
/// The books' reports (NL-602 A2, plan §6.1 "Reports") over in-memory books and feed: the balance identity, period
/// boundaries, the per-channel numbers from hand-built events, the fee breakdown and the register's paging.
/// </summary>
public class AccountingReportServiceTests
{
    private readonly AccountingBooksTestKit _kit = new();

    [Fact]
    public async Task Given_AFewMonths_When_TheBalanceSheetIsRead_Then_AssetsEqualEquityPlusEarnings()
    {
        // Arrange
        AddSampleLedger();

        // Act
        var sheet = await _kit.CreateReports().GetBalanceSheetAsync(null, TestContext.Current.CancellationToken);

        // Assert: wallet 1,000,000 - 502,000; channels 500,000 + 10,000 + 100 - 5,050; clearing 0
        Assert.Equal(498_000, Line(sheet.Assets, AccountRole.Wallet));
        Assert.Equal(505_050, Line(sheet.Assets, AccountRole.Channels));
        Assert.Equal(0, Line(sheet.Assets, AccountRole.Clearing));
        Assert.Equal(1_003_050, sheet.TotalAssetsMsat);
        Assert.Empty(sheet.Liabilities);
        Assert.Equal(1_000_000, Line(sheet.Equity, AccountRole.TransfersIn));
        Assert.Equal(10_000 + 100 - 2_000 - 5_000 - 50, sheet.RetainedEarningsMsat);
        Assert.True(sheet.IsBalanced);
        Assert.Equal(sheet.TotalAssetsMsat, sheet.TotalEquityMsat + sheet.RetainedEarningsMsat);
        Assert.Equal(6, sheet.ProjectedLedgerSeq);
        Assert.Equal("assets:lightning:channels", sheet.Assets.Single(a => a.Account == AccountRole.Channels).Name);
        Assert.Equal(505.05m, sheet.Assets.Single(a => a.Account == AccountRole.Channels).AmountSat);
        _kit.Books.Verify(b => b.ProjectNowAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_APastTime_When_TheBalanceSheetIsRead_Then_OnlyEarlierEntriesCount()
    {
        // Arrange
        AddSampleLedger();

        // Act: before the payment (day 4) and at the exact time of the forward (day 3, excluded)
        var sheet = await _kit.CreateReports().GetBalanceSheetAsync(T0.AddDays(3),
                                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(510_000, Line(sheet.Assets, AccountRole.Channels));
        Assert.Equal(10_000 - 2_000, sheet.RetainedEarningsMsat);
        Assert.True(sheet.IsBalanced);
        Assert.Equal(T0.AddDays(3), sheet.At);
    }

    [Fact]
    public async Task Given_APeriod_When_TheIncomeStatementIsRead_Then_TheStartCountsAndTheEndDoesNot()
    {
        // Arrange
        AddSampleLedger();

        // Act: [day 2, day 4): the invoice at day 2 and the forward at day 3, not the payment at day 4
        var statement = await _kit.CreateReports()
                                  .GetIncomeStatementAsync(T0.AddDays(2), T0.AddDays(4),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(10_000, Line(statement.Income, AccountRole.Received));
        Assert.Equal(100, Line(statement.Income, AccountRole.Routing));
        Assert.Empty(statement.Expenses);
        Assert.Equal(10_100, statement.NetIncomeMsat);

        // And the whole history: income less the funding fee, the payment and its route fee
        var all = await _kit.CreateReports().GetIncomeStatementAsync(null, null, TestContext.Current.CancellationToken);
        Assert.Equal(10_100, all.TotalIncomeMsat);
        Assert.Equal(2_000 + 5_000 + 50, all.TotalExpensesMsat);
        Assert.Equal(3_050, all.NetIncomeMsat);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_AReportIsAsked_Then_BooksDisabledAndNothingProjected()
    {
        // Arrange
        _kit.Books.SetupGet(b => b.IsEnabled).Returns(false);
        var reports = _kit.CreateReports();

        // Act / Assert
        await Assert.ThrowsAsync<AccountingBooksDisabledException>(
            () => reports.GetBalanceSheetAsync(null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AccountingBooksDisabledException>(
            () => reports.GetChannelsReportAsync(null, null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AccountingBooksDisabledException>(
            () => _kit.CreateReports(withBooks: false)
                      .GetFeesReportAsync(null, null, TestContext.Current.CancellationToken));
        _kit.Books.Verify(b => b.ProjectNowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnEmptyPeriod_When_AReportIsAsked_Then_ArgumentException()
    {
        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _kit.CreateReports().GetIncomeStatementAsync(T0.AddDays(2), T0.AddDays(2),
                                                               TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ForwardsPaymentsAndCloses_When_TheChannelsViewIsRead_Then_EachChannelHasItsNumbers()
    {
        // Arrange: channel A (1,000,000 sat, ours) and B (500,000 sat, the peer's) opened at day 0, forwards A -> B,
        // a self-payment out of A, B force-closed at day 10 with its CPFP, a reorg reversing B's close fee once
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        var (alice, bob) = (Peer(0x11), Peer(0x22));
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 1_000_000_000, 3_000_000, a, alice,
                 AccountingDetailsCodec.Create(("capacitySat", "1000000"), ("isInitiator", "true")));
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 0, 0, b, bob,
                 AccountingDetailsCodec.Create(("capacitySat", "500000"), ("isInitiator", "false")));
        _kit.Add(AccountingEventKind.PushReceived, T0, 7_000, 0, b, bob);
        for (var i = 0; i < 3; i++)
            _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(1 + i), 1_000, 0, a, alice,
                     Forward(a, b, 101_000, 100_000));
        _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(5), 500, 0, b, bob, Forward(b, a, 50_500, 50_000));
        _kit.Add(AccountingEventKind.PaymentSucceeded, T0.AddDays(6), -20_200, 200, a, Peer(0x99),
                 AccountingDetailsCodec.Create(("selfPayment", "true")));
        // NL-609: the rebalance's incoming side (our own invoice) is no payment received on B
        _kit.Add(AccountingEventKind.InvoiceSettled, T0.AddDays(6), 20_000, 0, b, bob,
                 AccountingDetailsCodec.Create(("selfPayment", "true")));
        _kit.Add(AccountingEventKind.PaymentSucceeded, T0.AddDays(6), -30_300, 300, b, Peer(0x98));
        _kit.Add(AccountingEventKind.InvoiceSettled, T0.AddDays(7), 20_000, 0, b, bob);
        _kit.Add(AccountingEventKind.ChannelForceClosed, T0.AddDays(10), -400_000, 4_000, b, bob,
                 AccountingDetailsCodec.Create(("lostMsat", "600")));
        _kit.Add(AccountingEventKind.AnchorCpfpFee, T0.AddDays(10), 0, 1_500, b);
        _kit.Add(AccountingEventKind.OutputResolved, T0.AddDays(11), 0, 700, b);
        _kit.Add(AccountingEventKind.Reversal, T0.AddDays(11), 400_000, -4_000, b, bob,
                 AccountingDetailsCodec.Create(("originalKind", "ChannelForceClosed")));
        _kit.Add(AccountingEventKind.ChannelForceClosed, T0.AddDays(12), -400_000, 4_100, b, bob);
        _kit.Clock.Now = T0.AddDays(73.05); // 0.2 year after the open

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, null,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var lineA = report.Channels.Single(c => c.ChannelId == a);
        Assert.Equal(alice, lineA.Counterparty);
        Assert.Equal(1_000_000_000, lineA.CapacityMsat);
        Assert.True(lineA.IsInitiator);
        Assert.Equal(T0, lineA.OpenedAt);
        Assert.Null(lineA.ClosedAt);
        Assert.Equal(3_000, lineA.RoutingInMsat);
        Assert.Equal(3, lineA.ForwardsIn);
        Assert.Equal(303_000, lineA.ForwardedInMsat);
        Assert.Equal(500, lineA.RoutingOutMsat);
        Assert.Equal(1, lineA.ForwardsOut);
        Assert.Equal(50_000, lineA.ForwardedOutMsat);
        Assert.Equal(20_000, lineA.RebalancedOutMsat);
        Assert.Equal(200, lineA.RebalanceCostMsat);
        Assert.Equal(0, lineA.PaymentsSentMsat);
        Assert.Equal(3_000_000, lineA.FundingFeeMsat);
        Assert.Equal(500 - 200 - 3_000_000, lineA.NetMsat);
        Assert.Equal(500d / 1_000_000_000, lineA.YieldOnCapacity!.Value, 12);
        Assert.Equal(500d / 1_000_000_000 * 5, lineA.AnnualizedYield!.Value, 10);

        var lineB = report.Channels.Single(c => c.ChannelId == b);
        Assert.Equal(bob, lineB.Counterparty);
        Assert.Equal(500_000_000, lineB.CapacityMsat);
        Assert.False(lineB.IsInitiator);
        Assert.Equal(T0.AddDays(10), lineB.ClosedAt);
        Assert.Equal(3_000, lineB.RoutingOutMsat);
        Assert.Equal(500, lineB.RoutingInMsat);
        Assert.Equal(30_000, lineB.PaymentsSentMsat);
        Assert.Equal(300, lineB.RoutingFeesPaidMsat);
        Assert.Equal(20_000, lineB.PaymentsReceivedMsat);
        Assert.Equal(7_000, lineB.PushMsat);
        Assert.Equal(4_100, lineB.CommitmentFeeMsat); // 4,000 + 4,100 - 4,000 reversed
        Assert.Equal(1_500, lineB.CpfpFeeMsat);
        Assert.Equal(700, lineB.SweepFeeMsat);
        Assert.Equal(600, lineB.OnchainLossMsat);
        Assert.Equal(4_100 + 1_500 + 700, lineB.OnchainFeesMsat);
        // Open for 10 days: the yield annualized over them
        Assert.Equal(3_000d / 500_000_000 * (365.25 / 10), lineB.AnnualizedYield!.Value, 10);

        var peerA = report.Peers.Single(p => p.Counterparty == alice);
        Assert.Equal(1, peerA.ChannelCount);
        Assert.Equal(3_000, peerA.RoutingInMsat);
        Assert.Equal(2, report.Peers.Count);
        Assert.Equal(report.Channels.Sum(c => c.NetMsat), report.Peers.Sum(p => p.NetMsat));
    }

    [Fact]
    public async Task Given_HtlcsLostOnChainAndAReorg_When_TheChannelsViewIsRead_Then_OnlyTheStandingLossesCount()
    {
        // Arrange (NL-688): a settled invoice's HTLC and a settled forward's HTLC lost on chain; a reorg reverses the
        // forward's loss once (before the fix the reversal left the loss counted)
        var b = Channel(0xB2);
        var bob = Peer(0x22);
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 0, 0, b, bob,
                 AccountingDetailsCodec.Create(("capacitySat", "500000"), ("isInitiator", "false")));
        _kit.Add(AccountingEventKind.ChannelForceClosed, T0.AddDays(10), -400_000, 4_000, b, bob);
        _kit.Add(AccountingEventKind.InvoiceLostOnchain, T0.AddDays(11), -20_000, 0, b, bob);
        _kit.Add(AccountingEventKind.ForwardLostOnchain, T0.AddDays(11), -1_000, 0, b, bob);
        _kit.Add(AccountingEventKind.Reversal, T0.AddDays(11), 1_000, 0, b, bob,
                 AccountingDetailsCodec.Create(("originalKind", "ForwardLostOnchain")));
        _kit.Clock.Now = T0.AddDays(20);

        // Act
        var report = await _kit.CreateReports().GetChannelsReportAsync(null, null, null,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var line = report.Channels.Single(c => c.ChannelId == b);
        Assert.Equal(20_000, line.OnchainLossMsat);
    }

    [Fact]
    public async Task Given_APeriodAfterTheOpen_When_TheChannelsViewIsRead_Then_TheCapacityIsStillKnown()
    {
        // Arrange
        var a = Channel(0xA1);
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 1_000_000_000, 1_000, a, Peer(0x11),
                 AccountingDetailsCodec.Create(("capacitySat", "1000000")));
        _kit.Add(AccountingEventKind.SpliceLocked, T0.AddDays(5), 200_000_000, 2_000, a, Peer(0x11),
                 AccountingDetailsCodec.Create(("capacitySat", "1200000")));
        _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(20), 1_200, 0, Channel(0xB2), Peer(0x22),
                 Forward(Channel(0xB2), a, 1_201_200, 1_200_000));

        // Act: only days 10 to 30 count for the money; the open and the splice before set the capacity
        var report = await _kit.CreateReports().GetChannelsReportAsync(T0.AddDays(10), T0.AddDays(30), a,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var line = Assert.Single(report.Channels);
        Assert.Equal(1_200_000_000, line.CapacityMsat);
        Assert.Equal(1_200, line.RoutingOutMsat);
        Assert.Equal(0, line.FundingFeeMsat);
        Assert.Equal(0, line.SpliceFeeMsat);
        Assert.Equal(1_200d / 1_200_000_000 * (365.25 / 20), line.AnnualizedYield!.Value, 10);
        Assert.Equal(T0.AddDays(30), report.AsOf);
    }

    [Fact]
    public async Task Given_FeesAndSweepBumps_When_TheFeesReportIsRead_Then_EveryFeeAccountAndTheBumpsAreListed()
    {
        // Arrange
        _kit.Add(AccountingEventKind.ChannelFunded, T0, 0, 2_000, Channel(1), null, null, null,
                 (AccountRole.FeeFunding, 2_000), (AccountRole.Clearing, -2_000));
        _kit.Add(AccountingEventKind.OutputResolved, T0.AddDays(1), 0, 900, Channel(1), null, null, null,
                 (AccountRole.FeeSweep, 900), (AccountRole.Pending, -900));
        _kit.Add(AccountingEventKind.SweepFeeBump, T0.AddDays(1), 0, 400, Channel(1),
                 details: AccountingDetailsCodec.Create(("purpose", "Sweep")));
        _kit.Add(AccountingEventKind.Reversal, T0.AddDays(2), 0, -400, Channel(1),
                 details: AccountingDetailsCodec.Create(("originalKind", "SweepFeeBump")));
        _kit.Add(AccountingEventKind.Reversal, T0.AddDays(2), 0, -2_000, Channel(1),
                 details: AccountingDetailsCodec.Create(("originalKind", "ChannelFunded")));

        // Act
        var fees = await _kit.CreateReports().GetFeesReportAsync(null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AccountingAccountCategories.FeeAccounts, fees.Accounts.Select(a => a.Account));
        Assert.Equal(2_000, Line(fees.Accounts, AccountRole.FeeFunding));
        Assert.Equal(900, Line(fees.Accounts, AccountRole.FeeSweep));
        Assert.Equal(0, Line(fees.Accounts, AccountRole.FeeCpfp));
        Assert.Equal(2_900, fees.TotalMsat);
        Assert.Equal([3L, 4L], fees.SweepFeeBumps.Select(b => b.LedgerSeq));
        Assert.Equal("Sweep", fees.SweepFeeBumps[0].Purpose);
        Assert.Equal(0, fees.SweepFeeBumpTotalMsat);
    }

    [Fact]
    public async Task Given_ManyEntries_When_TheRegisterIsPaged_Then_ItFollowsTheLedgerOrder()
    {
        // Arrange
        AddSampleLedger();
        var reports = _kit.CreateReports();

        // Act
        var first = await reports.GetRegisterAsync(new AccountingEntryQuery(0, 4),
                                                   TestContext.Current.CancellationToken);
        var second = await reports.GetRegisterAsync(new AccountingEntryQuery(first.NextAfter, 4),
                                                    TestContext.Current.CancellationToken);
        var routing = await reports.GetRegisterAsync(new AccountingEntryQuery(0, 10, Account: AccountRole.Routing),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([1L, 2L, 3L, 4L], first.Entries.Select(e => e.LedgerSeq));
        Assert.True(first.HasMore);
        Assert.Equal(4, first.NextAfter);
        Assert.Equal([5L, 6L], second.Entries.Select(e => e.LedgerSeq));
        Assert.False(second.HasMore);
        Assert.Equal(6, second.NextAfter);
        Assert.Equal(AccountingEventKind.ForwardSettled, Assert.Single(routing.Entries).Kind);
        Assert.Equal(AccountRole.Routing, _kit.BooksRepository.LastQuery!.Account);
    }

    // Day 0 deposit, day 1 funding (with the wallet output spent), day 2 invoice, day 3 forward, day 4 payment
    private void AddSampleLedger()
    {
        _kit.Add(AccountingEventKind.WalletReceived, T0, 1_000_000, 0, null, null, null, null,
                 (AccountRole.Wallet, 1_000_000), (AccountRole.TransfersIn, -1_000_000));
        _kit.Add(AccountingEventKind.ChannelFunded, T0.AddDays(1), 500_000, 2_000, Channel(1), null, null, null,
                 (AccountRole.Channels, 500_000), (AccountRole.FeeFunding, 2_000), (AccountRole.Clearing, -502_000));
        _kit.Add(AccountingEventKind.WalletOutputSpent, T0.AddDays(1), -502_000, 0, null, null, null, null,
                 (AccountRole.Wallet, -502_000), (AccountRole.Clearing, 502_000));
        _kit.Add(AccountingEventKind.InvoiceSettled, T0.AddDays(2), 10_000, 0, Channel(1), null, null, null,
                 (AccountRole.Channels, 10_000), (AccountRole.Received, -10_000));
        _kit.Add(AccountingEventKind.ForwardSettled, T0.AddDays(3), 100, 0, Channel(1), null, null, null,
                 (AccountRole.Channels, 100), (AccountRole.Routing, -100));
        _kit.Add(AccountingEventKind.PaymentSucceeded, T0.AddDays(4), -5_050, 50, Channel(1), null, null, null,
                 (AccountRole.Channels, -5_050), (AccountRole.Sent, 5_000), (AccountRole.RoutingFees, 50));
    }

    private static IReadOnlyDictionary<string, string> Forward(Domain.Channels.ValueObjects.ChannelId incoming,
                                                               Domain.Channels.ValueObjects.ChannelId outgoing,
                                                               long incomingMsat, long outgoingMsat) =>
        AccountingDetailsCodec.Create(("incomingChannelId", incoming.ToString()),
                                      ("outgoingChannelId", outgoing.ToString()),
                                      ("incomingAmountMsat", incomingMsat.ToString()),
                                      ("outgoingAmountMsat", outgoingMsat.ToString()));

    private static long Line(IReadOnlyList<AccountingAccountLine> lines, AccountRole role) =>
        lines.Single(l => l.Account == role).AmountMsat;
}