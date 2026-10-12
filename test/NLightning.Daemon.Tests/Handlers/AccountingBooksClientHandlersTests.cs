using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Accounting;
using Daemon.Handlers;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Persistence.Interfaces;

/// <summary>
/// The books' commands (NL-602 A2): <c>accounting report</c> (43) dispatches by kind with its filters and refuses when
/// the books are off, <c>accounting export</c> (44) pages through the export service, and the admin command (45)
/// reconciles and rebuilds through the books and verifies the feed's chain with the books on or off.
/// </summary>
public class AccountingBooksClientHandlersTests
{
    private static readonly DateTimeOffset s_since = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_until = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x44, 32).ToArray());

    private readonly Mock<IAccountingReports> _reports = new(MockBehavior.Strict);
    private readonly Mock<IAccountingExports> _exports = new(MockBehavior.Strict);
    private readonly Mock<IAccountingBooks> _books = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IAccountingEventDbRepository> _feed = new();

    public AccountingBooksClientHandlersTests()
    {
        _books.SetupGet(b => b.IsEnabled).Returns(true);
        _unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_feed.Object);
    }

    [Fact]
    public async Task Given_EachReportKind_When_Handled_Then_TheMatchingReportIsAskedWithItsPeriod()
    {
        // Arrange
        var sheet = new AccountingBalanceSheet(s_until, 9, [], [], [], 0);
        var statement = new AccountingIncomeStatement(s_since, s_until, 9, [], []);
        var channels = new AccountingChannelsReport(s_since, s_until, s_until, 9, [], []);
        var fees = new AccountingFeesReport(s_since, s_until, 9, [], []);
        _reports.Setup(r => r.GetBalanceSheetAsync(s_until, It.IsAny<CancellationToken>())).ReturnsAsync(sheet);
        _reports.Setup(r => r.GetIncomeStatementAsync(s_since, s_until, It.IsAny<CancellationToken>()))
                .ReturnsAsync(statement);
        _reports.Setup(r => r.GetChannelsReportAsync(s_since, s_until, s_channel, It.IsAny<CancellationToken>()))
                .ReturnsAsync(channels);
        _reports.Setup(r => r.GetFeesReportAsync(s_since, s_until, It.IsAny<CancellationToken>())).ReturnsAsync(fees);
        var handler = CreateReportHandler();

        // Act
        var balance = await Handle(handler, AccountingReportKind.BalanceSheet);
        var income = await Handle(handler, AccountingReportKind.IncomeStatement);
        var channelView = await Handle(handler, AccountingReportKind.Channels);
        var peers = await Handle(handler, AccountingReportKind.Peers);
        var feeReport = await Handle(handler, AccountingReportKind.Fees);

        // Assert
        Assert.Same(sheet, balance.BalanceSheet);
        Assert.Same(statement, income.IncomeStatement);
        Assert.Same(channels, channelView.Channels);
        Assert.Same(channels, peers.Channels);
        Assert.Equal(AccountingReportKind.Peers, peers.Kind);
        Assert.Same(fees, feeReport.Fees);
        Assert.Equal(ClientCommand.AccountingReport, handler.Command);
    }

    [Fact]
    public async Task Given_ARegisterRequest_When_Handled_Then_TheQueryCarriesEveryFilterAndTheAccount()
    {
        // Arrange
        AccountingEntryQuery? query = null;
        _reports.Setup(r => r.GetRegisterAsync(It.IsAny<AccountingEntryQuery>(), It.IsAny<CancellationToken>()))
                .Callback((AccountingEntryQuery q, CancellationToken _) => query = q)
                .ReturnsAsync(new AccountingRegister([], 5, false, 9));
        var options = new AccountingOptions { AccountNames = { [AccountRole.Routing] = "income:routing" } };
        var handler = new AccountingReportClientHandler(_reports.Object, Options.Create(options));

        // Act
        var response = await handler.HandleAsync(new AccountingReportClientRequest
        {
            Kind = AccountingReportKind.Register,
            Since = s_since,
            Until = s_until,
            ChannelId = s_channel,
            Account = "income:routing",
            EventKinds = [AccountingEventKind.ForwardSettled],
            AfterLedgerSeq = 5,
            Take = 50
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(response.Register);
        Assert.Equal("income:routing", response.Names[AccountRole.Routing]);
        Assert.NotNull(query);
        Assert.Equal(new AccountingEntryQuery(5, 50, s_since, s_until, query.Kinds, s_channel, AccountRole.Routing),
                     query);
        Assert.Equal([AccountingEventKind.ForwardSettled], query.Kinds!);
    }

    [Theory]
    [InlineData("Routing", AccountRole.Routing)]
    [InlineData("feesweep", AccountRole.FeeSweep)]
    [InlineData("ASSETS:LIGHTNING:CHANNELS", AccountRole.Channels)]
    [InlineData("expenses:onchain:fees:cpfp", AccountRole.FeeCpfp)]
    public void Given_AnAccountName_When_Parsed_Then_ItsRoleIsFound(string text, AccountRole expected)
    {
        // Act / Assert
        Assert.Equal(expected, CreateReportHandler().ParseAccount(text));
    }

    [Theory]
    [InlineData("11")]
    [InlineData("income:coffee")]
    public void Given_AnUnknownAccount_When_Parsed_Then_InvalidOperation(string text)
    {
        // Act / Assert
        var e = Assert.Throws<ClientException>(() => CreateReportHandler().ParseAccount(text));
        Assert.Contains("Unknown account", e.Message);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_AReportOrExportIsAsked_Then_BooksDisabled()
    {
        // Arrange
        _reports.Setup(r => r.GetBalanceSheetAsync(null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AccountingBooksDisabledException());
        _exports.Setup(e => e.ExportAsync(It.IsAny<AccountingExportQuery>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AccountingBooksDisabledException());

        // Act
        var report = await Assert.ThrowsAsync<ClientException>(
            () => CreateReportHandler().HandleAsync(new AccountingReportClientRequest(),
                                                    TestContext.Current.CancellationToken));
        var notRegistered = await Assert.ThrowsAsync<ClientException>(
            () => new AccountingReportClientHandler(null).HandleAsync(new AccountingReportClientRequest(),
                                                                      TestContext.Current.CancellationToken));
        var export = await Assert.ThrowsAsync<ClientException>(
            () => new AccountingExportClientHandler(_exports.Object).HandleAsync(
                new AccountingExportClientRequest(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("books are disabled", report.Message);
        Assert.Contains("books are disabled", notRegistered.Message);
        Assert.Contains("books are disabled", export.Message);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    public async Task Given_ABadRegisterPage_When_Handled_Then_InvalidOperation(long after, int take)
    {
        // Act / Assert
        await Assert.ThrowsAsync<ClientException>(
            () => CreateReportHandler().HandleAsync(new AccountingReportClientRequest
            {
                Kind = AccountingReportKind.Register,
                AfterLedgerSeq = after,
                Take = take
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AnEmptyPeriod_When_AReportIsAsked_Then_InvalidOperation()
    {
        // Act / Assert
        var e = await Assert.ThrowsAsync<ClientException>(
            () => CreateReportHandler().HandleAsync(new AccountingReportClientRequest
            {
                Kind = AccountingReportKind.IncomeStatement,
                Since = s_until,
                Until = s_since
            }, TestContext.Current.CancellationToken));
        Assert.Contains("until must be after since", e.Message);
    }

    [Fact]
    public async Task Given_AShortChannelId_When_TheChannelsViewIsAsked_Then_ItIsResolvedThroughTheLoadedChannels()
    {
        // Arrange
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var handler = new AccountingReportClientHandler(_reports.Object, null, memory.Object);

        // Act / Assert: no loaded channel has it
        var e = await Assert.ThrowsAsync<ClientException>(
            () => handler.HandleAsync(new AccountingReportClientRequest
            {
                Kind = AccountingReportKind.Channels,
                ChannelScid = new ShortChannelId(800_000, 1, 0)
            }, TestContext.Current.CancellationToken));
        Assert.Contains("No loaded channel", e.Message);
    }

    [Fact]
    public async Task Given_AnExportPage_When_Handled_Then_TheChunkIsReturnedWithItsFormat()
    {
        // Arrange
        var chunk = new AccountingExportChunk("text", 12, true, 3);
        _exports.Setup(e => e.ExportAsync(
                           new AccountingExportQuery(AccountingExportFormat.Csv, 9, 3, s_since, s_until),
                           It.IsAny<CancellationToken>()))
                .ReturnsAsync(chunk);
        var handler = new AccountingExportClientHandler(_exports.Object);

        // Act
        var response = await handler.HandleAsync(new AccountingExportClientRequest
        {
            Format = AccountingExportFormat.Csv,
            AfterLedgerSeq = 9,
            Take = 3,
            Since = s_since,
            Until = s_until
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AccountingExportFormat.Csv, response.Format);
        Assert.Same(chunk, response.Chunk);
        Assert.Equal(ClientCommand.AccountingExport, handler.Command);
    }

    [Fact]
    public async Task Given_ReconcileAndRebuild_When_Handled_Then_TheBooksAnswer()
    {
        // Arrange
        var result = new AccountingReconcileResult(s_until, 800_000, 9,
                                                   [new AccountingReconcileLine(AccountRole.Wallet, 10, 10)]);
        _books.Setup(b => b.ReconcileAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        _books.Setup(b => b.RebuildAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        var handler = CreateAdminHandler();

        // Act
        var reconcile = await handler.HandleAsync(new AccountingAdminClientRequest
        {
            Action = AccountingAdminAction.Reconcile
        }, TestContext.Current.CancellationToken);
        var rebuild = await handler.HandleAsync(new AccountingAdminClientRequest
        {
            Action = AccountingAdminAction.Rebuild
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(result, reconcile.Reconcile);
        Assert.Equal(42, rebuild.RebuiltEntries);
        Assert.Equal(ClientCommand.AccountingAdmin, handler.Command);
    }

    [Fact]
    public async Task Given_TheBooksOff_When_VerifyIsAsked_Then_TheChainIsStillWalkedButReconcileIsRefused()
    {
        // Arrange: two events sealed in a proper chain
        _books.SetupGet(b => b.IsEnabled).Returns(false);
        var sealedEvents = SealChain(2);
        _feed.Setup(f => f.GetSealedRangeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((long from, int take, CancellationToken _) =>
                               sealedEvents.Where(e => e.LedgerSeq >= from).Take(take).ToList());
        var handler = CreateAdminHandler();

        // Act
        var verify = await handler.HandleAsync(new AccountingAdminClientRequest
        {
            Action = AccountingAdminAction.Verify
        }, TestContext.Current.CancellationToken);
        var reconcile = await Assert.ThrowsAsync<ClientException>(
            () => handler.HandleAsync(new AccountingAdminClientRequest { Action = AccountingAdminAction.Reconcile },
                                      TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(verify.Verification);
        Assert.True(verify.Verification.IsIntact);
        Assert.Equal(2, verify.Verification.TipLedgerSeq);
        Assert.Equal(sealedEvents[1].Hash, verify.Verification.TipHash);
        Assert.Contains("books are disabled", reconcile.Message);
        _books.Verify(b => b.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private AccountingReportClientHandler CreateReportHandler() =>
        new(_reports.Object, Options.Create(new AccountingOptions()));

    private AccountingAdminClientHandler CreateAdminHandler() =>
        new(_unitOfWork.Object, _books.Object, Options.Create(new AccountingOptions()));

    private Task<Domain.Client.Responses.AccountingReportClientResponse> Handle(
        AccountingReportClientHandler handler, AccountingReportKind kind) =>
        handler.HandleAsync(new AccountingReportClientRequest
        {
            Kind = kind,
            Since = s_since,
            Until = s_until,
            ChannelId = s_channel
        }, TestContext.Current.CancellationToken);

    private static List<AccountingEventModel> SealChain(int count)
    {
        var events = new List<AccountingEventModel>();
        var previous = new byte[32];
        for (var seq = 1L; seq <= count; seq++)
        {
            var unsealed = new AccountingEventModel
            {
                EventKey = $"k:{seq}",
                Kind = AccountingEventKind.ForwardSettled,
                OccurredAt = s_since.AddMinutes(seq),
                AmountMsat = seq * 1_000
            };
            var hash = AccountingEventHasher.ComputeHash(previous, seq, unsealed);
            events.Add(new AccountingEventModel
            {
                EventKey = unsealed.EventKey,
                Kind = unsealed.Kind,
                OccurredAt = unsealed.OccurredAt,
                AmountMsat = unsealed.AmountMsat,
                LedgerSeq = seq,
                Hash = hash
            });
            previous = hash;
        }

        return events;
    }
}