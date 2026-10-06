using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Payments.Interception;

using Application.Payments;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// NL-1182: the accounting of a held forward the HTLC interceptor settled. One <c>InterceptedHtlcSettled</c> per
/// incoming HTLC books its whole amount as received; an HTLC lost on chain afterwards is a loss, as a forward's.
/// </summary>
public class InterceptedHtlcAccountingEventsTests
{
    private static readonly Hash s_hash = HashOf(SecretOf(7));
    private static readonly ShortChannelId s_outgoingScid = new(812, 3, 1);
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_ASettledInterceptedHtlc_When_Built_Then_ItsWholeAmountIsReceived()
    {
        // Act
        var settled = Settled();

        // Assert
        Assert.Equal(AccountingEventKeys.InterceptedHtlcSettled(TestChannelId, 5), settled.EventKey);
        Assert.Equal(AccountingEventKind.InterceptedHtlcSettled, settled.Kind);
        Assert.Equal(40_001_000, settled.AmountMsat);
        Assert.Equal(0, settled.FeeMsat);
        Assert.Equal(TestChannelId, settled.ChannelId);
        Assert.Equal(s_hash, settled.PaymentHash);
        Assert.Equal(s_now, settled.OccurredAt);
        Assert.Equal(900u, settled.BlockHeight);
        Assert.Equal(AccountingFinality.Final, settled.Finality);
        Assert.Equal(PaymentAccountingEvents.InterceptedKind, settled.Details["kind"]);
        Assert.Equal("5", settled.Details["incomingHtlcId"]);
        Assert.Equal("40001000", settled.Details["incomingAmountMsat"]);
        Assert.Equal(s_outgoingScid.ToString(), settled.Details[AccountingDetailKeys.OutgoingScid]);
        Assert.Equal("40000000", settled.Details["amountToForwardMsat"]);
        Assert.False(settled.Details.ContainsKey("nextNodeId"));

        // ... posted as a payment received: the channels up by the HTLC, no routing income
        var books = BooksSimulator.Of([settled]);
        Assert.Equal(40_001_000, books[AccountRole.Channels]);
        Assert.Equal(-40_001_000, books[AccountRole.Received]);
        Assert.Equal(0, books[AccountRole.Routing]);
    }

    [Fact]
    public async Task Given_AnInterceptedSettle_When_StagedTwice_Then_ItsEventIsStagedOnce()
    {
        // Arrange
        var (unitOfWork, staged) = UnitOfWork();

        // Act
        await Stage(unitOfWork.Object);
        await Stage(unitOfWork.Object);

        // Assert
        Assert.Equal(AccountingEventKind.InterceptedHtlcSettled, Assert.Single(staged).Kind);
    }

    [Fact]
    public async Task Given_AStoreThatFails_When_Staged_Then_NothingIsThrown()
    {
        // Arrange
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Throws(new InvalidOperationException("database down"));

        // Act
        var exception = await Record.ExceptionAsync(() => Stage(unitOfWork.Object));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_ALostInterceptedHtlc_When_Built_Then_ItIsAForwardLossOfTheHtlcsAmount()
    {
        // Act
        var closeTxId = new TxId(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var spenderTxId = new TxId(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var lost = PaymentAccountingEvents.InterceptedHtlcLostOnchain("fwd:x", Settled(), null, closeTxId, spenderTxId,
                                                                      s_now, 950);

        // Assert
        Assert.Equal("fwd:x", lost.EventKey);
        Assert.Equal(AccountingEventKind.ForwardLostOnchain, lost.Kind);
        Assert.Equal(-40_001_000, lost.AmountMsat);
        Assert.Equal(TestChannelId, lost.ChannelId);
        Assert.Equal(s_hash, lost.PaymentHash);
        Assert.Equal(950u, lost.BlockHeight);
        Assert.Equal(AccountingFinality.Confirmed, lost.Finality);
        Assert.Equal(PaymentAccountingEvents.InterceptedKind, lost.Details["kind"]);
        Assert.Equal(PaymentAccountingEvents.UpstreamOnchainCause, lost.Details["cause"]);
        Assert.Equal(AccountingEventKeys.InterceptedHtlcSettled(TestChannelId, 5), lost.Details["settledKey"]);
        Assert.Equal(closeTxId.ToString(), lost.Details[AccountingDetailKeys.CloseTxId]);
        Assert.Equal(spenderTxId.ToString(), lost.Details["spenderTxId"]);

        // ... which takes the settle back out of the channels as a loss
        var books = BooksSimulator.Of([Settled(), lost]);
        Assert.Equal(0, books[AccountRole.Channels]);
        Assert.Equal(40_001_000, books[AccountRole.LossOnchain]);
    }

    private static AccountingEventModel Settled() =>
        PaymentAccountingEvents.InterceptedHtlcSettled(TestChannelId, 5, s_hash, LightningMoney.MilliSatoshis(40_001_000),
                                                       null, s_outgoingScid, null,
                                                       LightningMoney.MilliSatoshis(40_000_000), s_now, 900);

    private static Task Stage(IUnitOfWork unitOfWork) =>
        PaymentAccountingEvents.StageInterceptedHtlcSettledAsync(unitOfWork, TestChannelId, 5, Settled,
                                                                 NullLogger.Instance,
                                                                 TestContext.Current.CancellationToken);

    private static (Mock<IUnitOfWork> UnitOfWork, List<AccountingEventModel> Staged) UnitOfWork()
    {
        var staged = new List<AccountingEventModel>();
        var events = new Mock<IAccountingEventDbRepository>();
        events.Setup(e => e.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(staged.Add);
        events.Setup(e => e.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string key, CancellationToken _) => staged.Any(s => s.EventKey == key));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(events.Object);
        return (unitOfWork, staged);
    }
}