using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The period close over IPC 45 (NL-602 A3-T5): <c>close</c>, <c>close list</c>, <c>close show</c>, the closes in
/// <c>verify</c> and <c>rebuild --book financial</c> cross the envelope; a refused close, a bad period, an unknown book
/// and a daemon without closes are <c>invalid_operation</c>.
/// </summary>
public class AccountingCloseIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_end = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_closedAt = new(2026, 10, 2, 12, 0, 0, 250, TimeSpan.Zero);

    private readonly Mock<IAccountingBooks> _books = new();
    private readonly Mock<IAccountingPeriods> _periods = new();
    private readonly Mock<IAccountingEventDbRepository> _feed = new();

    public AccountingCloseIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _books.SetupGet(b => b.IsEnabled).Returns(true);
        _feed.Setup(f => f.GetSealedRangeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
    }

    [Fact]
    public async Task Given_AClose_When_Asked_Then_ThePeriodAndItsDigestCrossTheEnvelope()
    {
        // Arrange
        _periods.Setup(p => p.CloseAsync("2026-09", true, It.IsAny<CancellationToken>())).ReturnsAsync(Report());

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Close,
            Period = " 2026-09 ",
            Force = true
        });

        // Assert
        Assert.Equal((int)AccountingAdminAction.Close, response.Action);
        var period = response.Period!;
        Assert.Equal("2026-09", period.PeriodId);
        Assert.Equal(s_start.ToUnixTimeSeconds(), period.StartUnixSeconds);
        Assert.Equal(s_end.ToUnixTimeSeconds(), period.EndUnixSeconds);
        Assert.Equal(1, period.State);
        Assert.Equal(s_closedAt.ToUnixTimeMilliseconds(), period.ClosedAtUnixMilliseconds);
        Assert.Equal(17, period.LastLedgerSeq);
        Assert.Equal(new string('a', 64), period.ChainHash);
        Assert.Equal(new string('b', 64), period.Digest);
        Assert.Equal(new string('c', 128), period.Signature);
        Assert.Equal("02" + new string('d', 64), period.NodeId);
        Assert.True(period.Forced);
        Assert.Equal(15, period.ReplayAfterLedgerSeq);
        var balance = Assert.Single(period.Balances!);
        Assert.Equal((int)AccountRole.Channels, balance.Account);
        Assert.Equal("assets:channels", balance.AccountName);
        Assert.Equal(5_000, balance.BalanceMsat);
        Assert.Equal("1.25", balance.FiatAmount);
        Assert.Equal(4, period.EntryCount);
        Assert.Equal(2, period.ReliefCount);
        Assert.Equal(3, period.OpenLotCount);
        Assert.Equal(1, period.UnvaluedPostings);
        Assert.Equal(0, period.UnclassifiedEntries);
    }

    [Fact]
    public async Task Given_ListShowVerifyAndAFinancialRebuild_When_Asked_Then_EachResultCrossesTheEnvelope()
    {
        // Arrange
        _periods.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Report()]);
        _periods.Setup(p => p.GetAsync("2026-09", It.IsAny<CancellationToken>())).ReturnsAsync(Report());
        _periods.Setup(p => p.VerifyClosesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new AccountingCloseVerification("2026-09", false, true, true, true, true, 4, 2, 3,
                                                    "the digest does not match")
                ]);
        _periods.Setup(p => p.RebuildFinancialAsync(It.IsAny<CancellationToken>())).ReturnsAsync(9);
        _books.Setup(b => b.RebuildAsync(It.IsAny<CancellationToken>())).ReturnsAsync(30);

        // Act
        var list = await AdminAsync(new AccountingAdminIpcRequest { Action = (int)AccountingAdminAction.CloseList });
        var show = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.CloseShow,
            Period = "2026-09"
        });
        var verify = await AdminAsync(new AccountingAdminIpcRequest { Action = (int)AccountingAdminAction.Verify });
        var financial = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Rebuild,
            Book = (int)AccountingBook.Financial
        });
        var operational = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Rebuild
        });

        // Assert
        Assert.Equal("2026-09", Assert.Single(list.Periods!).PeriodId);
        Assert.Equal("2026-09", show.Period!.PeriodId);
        Assert.True(verify.Verification!.IsIntact);
        var close = Assert.Single(verify.PeriodVerifications!);
        Assert.False(close.IsIntact);
        Assert.False(close.DigestMatches);
        Assert.True(close.SignatureValid);
        Assert.Equal(4, close.EntryCount);
        Assert.Equal("the digest does not match", close.Problem);
        Assert.Equal(9, financial.RebuiltEntries);
        Assert.Equal(30, operational.RebuiltEntries);
    }

    [Fact]
    public async Task Given_ARefusedCloseABadPeriodOrAnUnknownPeriod_When_Asked_Then_InvalidOperation()
    {
        // Arrange
        _periods.Setup(p => p.CloseAsync("2026-09", false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AccountingCloseRefusedException("not ready: 3 postings have no fiat value", 3));
        _periods.Setup(p => p.CloseAsync("Sept", false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("'Sept' is not a period"));
        _periods.Setup(p => p.GetAsync("2026-08", It.IsAny<CancellationToken>()))
                .ReturnsAsync((AccountingCloseReport?)null);

        // Act
        var refused = await AdminErrorAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Close,
            Period = "2026-09"
        });
        var bad = await AdminErrorAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Close,
            Period = "Sept"
        });
        var missing = await AdminErrorAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.CloseShow,
            Period = "2026-08"
        });
        var noPeriod = await AdminErrorAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Close
        });
        var book = await AdminErrorAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Rebuild,
            Book = 7
        });

        // Assert
        Assert.All([refused, bad, missing, noPeriod, book], e => Assert.Equal(ErrorCodes.InvalidOperation, e.Code));
        Assert.Contains("3 postings have no fiat value", refused.Message);
        Assert.Contains("not a period", bad.Message);
        Assert.Contains("No accounting period 2026-08", missing.Message);
        Assert.Contains("period is required", noPeriod.Message);
        Assert.Contains("Unknown accounting book 7", book.Message);
    }

    [Fact]
    public async Task Given_ADaemonWithoutCloses_When_AsClosedOrVerified_Then_CloseIsRefusedAndVerifyHasNoCloses()
    {
        // Arrange
        var handler = BuildServices(withPeriods: false).BuildServiceProvider().GetServices<IIpcCommandHandler>()
                                                       .Single(h => h.Command == ClientCommand.AccountingAdmin);

        // Act
        var close = await handler.HandleAsync(CreateEnvelope(Serialize(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.CloseList
        })), TestContext.Current.CancellationToken);
        var verify = await handler.HandleAsync(CreateEnvelope(Serialize(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Verify
        })), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, close.Kind);
        Assert.Contains("does not serve", MessagePackSerializer.Deserialize<IpcError>(
                                              close.Payload, s_options, TestContext.Current.CancellationToken)
                                         .Message);
        var verified = MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(
            verify.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Null(verified.PeriodVerifications);
        Assert.NotNull(verified.Verification);
    }

    private static AccountingCloseReport Report() =>
        new(new AccountingPeriod("2026-09", s_start, s_end, AccountingPeriodState.Closed, s_closedAt, 17,
                                 Filled(0xAA, 32), Filled(0xBB, 32), Filled(0xCC, 64), true, "{}"),
            new AccountingClosingState(15,
            [
                new AccountingAccountBalance(AccountingBook.Financial, AccountRole.Channels, "assets:channels", 5_000,
                                             1.2500m)
            ]))
        {
            NodeId = [0x02, .. Filled(0xDD, 32)],
            EntryCount = 4,
            ReliefCount = 2,
            OpenLotCount = 3,
            UnvaluedPostings = 1,
            UnclassifiedEntries = 0
        };

    private static byte[] Filled(byte value, int length) => Enumerable.Repeat(value, length).ToArray();

    private async Task<AccountingAdminIpcResponse> AdminAsync(AccountingAdminIpcRequest request)
    {
        var response = await Handler().HandleAsync(CreateEnvelope(Serialize(request)),
                                                   TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
    }

    private async Task<IpcError> AdminErrorAsync(AccountingAdminIpcRequest request)
    {
        var response = await Handler().HandleAsync(CreateEnvelope(Serialize(request)),
                                                   TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler Handler() =>
        BuildServices().BuildServiceProvider().GetServices<IIpcCommandHandler>()
                       .Single(h => h.Command == ClientCommand.AccountingAdmin);

    private ServiceCollection BuildServices(bool withPeriods = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_feed.Object);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton(_books.Object);
        if (withPeriods)
            services.AddSingleton(_periods.Object);

        services.AddAccountingIpcServices();
        return services;
    }

    private static byte[] Serialize<T>(T request) =>
        MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);

    private static IpcEnvelope CreateEnvelope(byte[] payload) => new()
    {
        Version = 1,
        Command = ClientCommand.AccountingAdmin,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = payload
    };
}