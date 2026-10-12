using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting report</c> (ClientCommand 43), <c>accounting export</c> (44) and the admin command (45) over IPC,
/// NL-602 A2: every report, the export page and each admin action cross the envelope; unknown kinds, formats and
/// actions are <c>invalid_operation</c>; the registration is idempotent.
/// </summary>
public class AccountingBooksIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x33, 32).ToArray());

    private static readonly CompactPubKey s_peer =
        new(Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"));

    private readonly Mock<IAccountingReports> _reports = new();
    private readonly Mock<IAccountingExports> _exports = new();
    private readonly Mock<IAccountingBooks> _books = new();
    private readonly Mock<IAccountingEventDbRepository> _feed = new();

    public AccountingBooksIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _books.SetupGet(b => b.IsEnabled).Returns(true);
    }

    [Fact]
    public async Task Given_ABalanceSheet_When_Reported_Then_EveryFieldCrossesTheEnvelope()
    {
        // Arrange
        _reports.Setup(r => r.GetBalanceSheetAsync(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
                                                   It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountingBalanceSheet(
                                  DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 77,
                                  [new AccountingAccountLine(AccountRole.Channels, "assets:lightning:channels", 1_500)],
                                  [],
                                  [new AccountingAccountLine(AccountRole.TransfersIn, "equity:transfers:in", 1_000)],
                                  500));

        // Act
        var report = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.BalanceSheet,
            UntilUnixSeconds = 1_800_000_000
        });

        // Assert
        Assert.Equal((int)AccountingReportKind.BalanceSheet, report.Kind);
        Assert.Equal("BalanceSheet", report.KindName);
        Assert.Equal(77, report.ProjectedLedgerSeq);
        Assert.Equal(1_800_000_000_000, report.UntilUnixMilliseconds);
        var sheet = report.BalanceSheet!;
        var asset = Assert.Single(sheet.Assets);
        Assert.Equal((int)AccountRole.Channels, asset.Account);
        Assert.Equal("Channels", asset.Role);
        Assert.Equal("assets:lightning:channels", asset.Name);
        Assert.Equal(1_500, asset.AmountMsat);
        Assert.Equal(1_000, sheet.TotalEquityMsat);
        Assert.Equal(500, sheet.RetainedEarningsMsat);
        Assert.True(sheet.IsBalanced);
        Assert.Null(report.Channels);
    }

    [Fact]
    public async Task Given_TheChannelsView_When_ReportedAsChannelsAndAsPeers_Then_EachKindCarriesItsList()
    {
        // Arrange
        var line = new AccountingChannelLine
        {
            ChannelId = s_channel,
            ShortChannelId = "800000x1x0",
            Counterparty = s_peer,
            CapacityMsat = 1_000_000_000,
            IsInitiator = true,
            OpenedAt = s_at,
            RoutingInMsat = 10,
            RoutingOutMsat = 20,
            ForwardsOut = 2,
            RebalanceCostMsat = 3,
            FundingFeeMsat = 4,
            YieldOnCapacity = 2e-8,
            AnnualizedYield = 1e-7
        };
        var view = new AccountingChannelsReport(null, null, s_at, 5, [line],
                                                [new AccountingPeerLine { Counterparty = s_peer, ChannelCount = 1 }]);
        _reports.Setup(r => r.GetChannelsReportAsync(null, null, s_channel, It.IsAny<CancellationToken>()))
                .ReturnsAsync(view);

        // Act
        var channels = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Channels,
            Channel = s_channel.ToString()
        });
        var peers = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Peers,
            Channel = s_channel.ToString()
        });

        // Assert
        var channel = Assert.Single(channels.Channels!);
        Assert.Null(channels.Peers);
        Assert.Equal(s_channel.ToString(), channel.ChannelId);
        Assert.Equal("800000x1x0", channel.ShortChannelId);
        Assert.Equal(s_peer.ToString(), channel.Counterparty);
        Assert.Equal(1_000_000_000, channel.CapacityMsat);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), channel.OpenedAtUnixMilliseconds);
        Assert.Equal(20, channel.RoutingOutMsat);
        Assert.Equal(2, channel.ForwardsOut);
        Assert.Equal(20 - 3 - 4, channel.NetMsat);
        Assert.Equal(1e-7, channel.AnnualizedYield);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), channels.AsOfUnixMilliseconds);
        var peer = Assert.Single(peers.Peers!);
        Assert.Null(peers.Channels);
        Assert.Equal(s_peer.ToString(), peer.Counterparty);
        Assert.Equal(1, peer.ChannelCount);
    }

    [Fact]
    public async Task Given_ARegisterPage_When_Reported_Then_ThePostingsCarryTheAccountNames()
    {
        // Arrange
        var entry = new AccountingEntry(3, "fwd:x:1:settled", AccountingEventKind.ForwardSettled, s_at, s_channel,
                                        null,
                                        [
                                            new AccountingPosting(AccountRole.Channels, 100),
                                            new AccountingPosting(AccountRole.Routing, -100)
                                        ], "note");
        AccountingEntryQuery? query = null;
        _reports.Setup(r => r.GetRegisterAsync(It.IsAny<AccountingEntryQuery>(), It.IsAny<CancellationToken>()))
                .Callback((AccountingEntryQuery q, CancellationToken _) => query = q)
                .ReturnsAsync(new AccountingRegister([entry], 3, true, 9));

        // Act
        var report = await ReportAsync(new AccountingReportIpcRequest
        {
            Kind = (int)AccountingReportKind.Register,
            EventKinds = [(int)AccountingEventKind.ForwardSettled],
            Account = "Routing",
            AfterLedgerSeq = 2,
            Limit = 1,
            SinceUnixSeconds = 1_000
        });

        // Assert
        var page = report.Register!;
        Assert.Equal(3, page.NextAfter);
        Assert.True(page.HasMore);
        var listed = Assert.Single(page.Entries);
        Assert.Equal("fwd:x:1:settled", listed.EventKey);
        Assert.Equal("ForwardSettled", listed.KindName);
        Assert.Equal(s_channel.ToString(), listed.ChannelId);
        Assert.Equal("note", listed.Note);
        Assert.Equal(["assets:lightning:channels", "income:lightning:routing"], listed.Postings.Select(p => p.Name));
        Assert.Equal([100L, -100L], listed.Postings.Select(p => p.AmountMsat));
        Assert.NotNull(query);
        Assert.Equal(AccountRole.Routing, query.Account);
        Assert.Equal(2, query.AfterLedgerSeq);
        Assert.Equal(1, query.Take);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_000), query.Since);
    }

    [Theory]
    [InlineData(ClientCommand.AccountingReport)]
    [InlineData(ClientCommand.AccountingExport)]
    [InlineData(ClientCommand.AccountingAdmin)]
    public async Task Given_AnUnknownKindFormatOrAction_When_Sent_Then_InvalidOperation(ClientCommand command)
    {
        // Arrange
        var payload = command switch
        {
            ClientCommand.AccountingReport => Serialize(new AccountingReportIpcRequest { Kind = 99 }),
            ClientCommand.AccountingExport => Serialize(new AccountingExportIpcRequest { Format = 99 }),
            _ => Serialize(new AccountingAdminIpcRequest { Action = 99 })
        };

        // Act
        var response = await GetHandler(command).HandleAsync(CreateEnvelope(command, payload),
                                                             TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
    }

    [Fact]
    public async Task Given_TheBooksNotRegistered_When_AReportIsAsked_Then_BooksDisabled()
    {
        // Arrange
        var services = BuildServices(withBooks: false);
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.AccountingReport);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.AccountingReport,
                                                                Serialize(new AccountingReportIpcRequest())),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("books are disabled", error.Message);
    }

    [Fact]
    public async Task Given_AnExportPage_When_Asked_Then_TheTextAndCursorCrossTheEnvelope()
    {
        // Arrange
        _exports.Setup(e => e.ExportAsync(new AccountingExportQuery(AccountingExportFormat.Beancount, 4, 2,
                                                                    DateTimeOffset.FromUnixTimeSeconds(10), null),
                                          It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountingExportChunk("2026-01-01 * \"x\"\n", 6, true, 2));

        // Act
        var response = await GetHandler(ClientCommand.AccountingExport)
                          .HandleAsync(CreateEnvelope(ClientCommand.AccountingExport,
                                                      Serialize(new AccountingExportIpcRequest
                                                      {
                                                          Format = (int)AccountingExportFormat.Beancount,
                                                          AfterLedgerSeq = 4,
                                                          Limit = 2,
                                                          SinceUnixSeconds = 10
                                                      })), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var page = MessagePackSerializer.Deserialize<AccountingExportIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal((int)AccountingExportFormat.Beancount, page.Format);
        Assert.Equal("2026-01-01 * \"x\"\n", page.Text);
        Assert.Equal(6, page.NextAfter);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.EntryCount);
    }

    [Fact]
    public async Task Given_EachAdminAction_When_Asked_Then_ItsResultCrossesTheEnvelope()
    {
        // Arrange
        _books.Setup(b => b.ReconcileAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AccountingReconcileResult(s_at, 800_000, 12,
                                                          [
                                                              new AccountingReconcileLine(
                                                                  AccountRole.Wallet, 1_000, 900, "unconfirmed")
                                                          ]));
        _books.Setup(b => b.RebuildAsync(It.IsAny<CancellationToken>())).ReturnsAsync(12);
        _feed.Setup(f => f.GetSealedRangeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);

        // Act
        var reconcile = await AdminAsync(AccountingAdminAction.Reconcile);
        var rebuild = await AdminAsync(AccountingAdminAction.Rebuild);
        var verify = await AdminAsync(AccountingAdminAction.Verify);

        // Assert
        var result = reconcile.Reconcile!;
        Assert.False(result.IsClean);
        Assert.Equal(800_000u, result.BlockHeight);
        Assert.Equal(12, result.LedgerSeq);
        var line = Assert.Single(result.Lines);
        Assert.Equal("assets:onchain:wallet", line.Name);
        Assert.Equal(100, line.DriftMsat);
        Assert.Equal("unconfirmed", line.Note);
        Assert.Equal(12, rebuild.RebuiltEntries);
        Assert.True(verify.Verification!.IsIntact);
        Assert.Equal(0, verify.Verification.TipLedgerSeq);
        Assert.Equal(new string('0', 64), verify.Verification.TipHash);
    }

    [Fact]
    public async Task Given_ClearingHeldByTransactionsInFlight_When_Reconciled_Then_ItCrossesTheEnvelopeAsOutstanding()
    {
        // Arrange (NL-621): a splice below its lock explains the whole clearing balance
        _books.Setup(b => b.ReconcileAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AccountingReconcileResult(s_at, 800_000, 12,
                                                          [
                                                              new AccountingReconcileLine(
                                                                  AccountRole.Clearing, 10_252_000, 0, "in flight",
                                                                  10_252_000)
                                                          ]));

        // Act
        var reconcile = await AdminAsync(AccountingAdminAction.Reconcile);

        // Assert
        var result = reconcile.Reconcile!;
        Assert.True(result.IsClean);
        var line = Assert.Single(result.Lines);
        Assert.Equal(10_252_000, line.OutstandingMsat);
        Assert.Equal(0, line.DriftMsat);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandlerPerCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddAccountingIpcServices();

        // Act
        using var provider = services.BuildServiceProvider();
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert
        foreach (var command in new[]
                 {
                     ClientCommand.ListAccountingEvents, ClientCommand.AccountingSnapshot,
                     ClientCommand.AccountingReport, ClientCommand.AccountingExport, ClientCommand.AccountingAdmin
                 })
            Assert.Single(commands, c => c == command);
    }

    [Fact]
    public void Given_TheCommands_When_Read_Then_TheWireValuesAre43To45()
    {
        // Assert (append-only, never renumber)
        Assert.Equal(43, (int)ClientCommand.AccountingReport);
        Assert.Equal(44, (int)ClientCommand.AccountingExport);
        Assert.Equal(45, (int)ClientCommand.AccountingAdmin);
        Assert.Equal(1, (int)AccountingReportKind.BalanceSheet);
        Assert.Equal(6, (int)AccountingReportKind.Register);
        Assert.Equal(3, (int)AccountingExportFormat.Csv);
        Assert.Equal(3, (int)AccountingAdminAction.Verify);
    }

    private async Task<AccountingReportIpcResponse> ReportAsync(AccountingReportIpcRequest request)
    {
        var response = await GetHandler(ClientCommand.AccountingReport)
                          .HandleAsync(CreateEnvelope(ClientCommand.AccountingReport, Serialize(request)),
                                       TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingReportIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
    }

    private async Task<AccountingAdminIpcResponse> AdminAsync(AccountingAdminAction action)
    {
        var response = await GetHandler(ClientCommand.AccountingAdmin)
                          .HandleAsync(CreateEnvelope(ClientCommand.AccountingAdmin,
                                                      Serialize(new AccountingAdminIpcRequest { Action = (int)action })),
                                       TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler(ClientCommand command) =>
        BuildServices().BuildServiceProvider().GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private ServiceCollection BuildServices(bool withBooks = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_feed.Object);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        if (withBooks)
        {
            services.AddSingleton(_reports.Object);
            services.AddSingleton(_exports.Object);
            services.AddSingleton(_books.Object);
        }

        services.AddAccountingIpcServices();
        return services;
    }

    private static byte[] Serialize<T>(T request) =>
        MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);

    private static IpcError ReadError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private static IpcEnvelope CreateEnvelope(ClientCommand command, byte[] payload) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = payload
    };
}