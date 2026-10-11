using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Payments.Trampoline;

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
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// NL-875 (TR3-P4): the accounting of a trampoline relay. One <c>TrampolineRelaySettled</c> books the channels' net
/// change (the incoming parts minus what the outgoing payment took) as routing income; a part lost on chain afterwards
/// is a loss, as a forward's.
/// </summary>
public class TrampolineAccountingEventsTests
{
    private static readonly Secret s_preimage = SecretOf(7);
    private static readonly Hash s_hash = HashOf(s_preimage);
    private static readonly ChannelId s_otherChannelId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());
    private static readonly DateTimeOffset s_now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_AFulfilledRelayAndItsPayment_When_Built_Then_ItsNetIsTheIncomingPartsMinusThePayment()
    {
        // Arrange - 2 parts of 60,000 + 40,000 sat in; 99,000 sat out plus 500 sat of routing fees
        var relay = Fulfilled();
        TrampolineRelayPartModel[] parts = [Part(TestChannelId, 1, 60_000_000), Part(s_otherChannelId, 4, 40_000_000)];
        var payment = Payment(99_000_000, 500_000);

        // Act
        var settled = PaymentAccountingEvents.TrampolineRelaySettled(relay, parts, payment, null, 812);

        // Assert
        Assert.Equal(AccountingEventKeys.TrampolineRelaySettled(s_hash), settled.EventKey);
        Assert.Equal(AccountingEventKind.TrampolineRelaySettled, settled.Kind);
        Assert.Equal(500_000, settled.AmountMsat);
        Assert.Equal(0, settled.FeeMsat);
        Assert.Equal(TestChannelId, settled.ChannelId);
        Assert.Equal(s_hash, settled.PaymentHash);
        Assert.Equal(s_now, settled.OccurredAt);
        Assert.Equal(812u, settled.BlockHeight);
        Assert.Equal("trampoline", settled.Details["kind"]);
        Assert.Equal("2", settled.Details["parts"]);
        Assert.Equal("100000000", settled.Details["incomingAmountMsat"]);
        Assert.Equal("99500000", settled.Details["outgoingAmountMsat"]);
        Assert.Equal("500000", settled.Details["routingFeePaidMsat"]);
        Assert.Equal(s_otherChannelId.ToString(), settled.Details["outgoingChannelId"]);
        Assert.Equal($"{TestChannelId},{s_otherChannelId}", settled.Details["incomingChannelIds"]);
        // NL-899: what each incoming channel brought, for the channels report's split
        Assert.Equal($"{TestChannelId}:60000000,{s_otherChannelId}:40000000",
                     settled.Details[PaymentAccountingEvents.TrampolineIncomingAmountsDetail]);

        // Assert (the books): routing income, and nothing else moves
        var books = BooksSimulator.Of([settled]);
        Assert.Equal(500_000, books[AccountRole.Channels]);
        Assert.Equal(-500_000, books[AccountRole.Routing]);
        Assert.Equal(0, books[AccountRole.Sent]);
        Assert.Equal(0, books[AccountRole.RoutingFees]);
    }

    [Fact]
    public void Given_AFulfilledRelayWithoutItsPayment_When_Built_Then_ItsNetIsTheFeeEarned()
    {
        // Act
        var settled = PaymentAccountingEvents.TrampolineRelaySettled(Fulfilled(feeEarnedMsat: 1_234),
                                                                      [Part(TestChannelId, 1, 100_000_000)], null,
                                                                      null);

        // Assert
        Assert.Equal(1_234, settled.AmountMsat);
        Assert.False(settled.Details.ContainsKey("routingFeePaidMsat"));
    }

    [Fact]
    public void Given_ARelayThatCostMoreThanItBrought_When_Booked_Then_TheDifferenceIsARoutingExpense()
    {
        // Act
        var settled = PaymentAccountingEvents.TrampolineRelaySettled(Fulfilled(), [Part(TestChannelId, 1, 1_000_000)],
                                                                      Payment(999_000, 3_000), null);

        // Assert
        Assert.Equal(-2_000, settled.AmountMsat);
        var books = BooksSimulator.Of([settled]);
        Assert.Equal(-2_000, books[AccountRole.Channels]);
        Assert.Equal(2_000, books[AccountRole.RoutingFees]);
    }

    [Fact]
    public void Given_ARelayThatIsNotFulfilled_When_Built_Then_ItThrows()
    {
        // Arrange
        var relay = new TrampolineRelayModel(s_hash, Point(0x0B), LightningMoney.MilliSatoshis(1_000), 600,
                                             LightningMoney.MilliSatoshis(2_000), s_now);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => PaymentAccountingEvents.TrampolineRelaySettled(
                                                     relay, [Part(TestChannelId, 1, 2_000)], null, null));
    }

    [Fact]
    public async Task Given_AFulfilledRelay_When_Staged_Then_ItsEventIsStagedOnceWithItsRelayPayment()
    {
        // Arrange
        var (unitOfWork, staged) = UnitOfWork([Part(TestChannelId, 1, 100_000_000)], Payment(99_000_000, 400_000));
        var relay = Fulfilled();

        // Act
        await PaymentAccountingEvents.StageTrampolineRelaySettledAsync(unitOfWork.Object, relay, null, 0,
                                                                       NullLogger.Instance,
                                                                       TestContext.Current.CancellationToken);
        await PaymentAccountingEvents.StageTrampolineRelaySettledAsync(unitOfWork.Object, relay, null, 0,
                                                                       NullLogger.Instance,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        var settled = Assert.Single(staged);
        Assert.Equal(600_000, settled.AmountMsat);
    }

    [Fact]
    public async Task Given_AStoreThatFails_When_Staged_Then_NothingIsThrown()
    {
        // Arrange
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Throws(new InvalidOperationException("database down"));

        // Act
        var exception = await Record.ExceptionAsync(() => PaymentAccountingEvents.StageTrampolineRelaySettledAsync(
                                                        unitOfWork.Object, Fulfilled(), null, 0, NullLogger.Instance,
                                                        TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task Given_ARelayStillSending_When_Staged_Then_NothingIsStaged()
    {
        // Arrange
        var (unitOfWork, staged) = UnitOfWork([Part(TestChannelId, 1, 100_000_000)], null);
        var relay = new TrampolineRelayModel(s_hash, Point(0x0B), LightningMoney.MilliSatoshis(99_000_000), 600,
                                             LightningMoney.MilliSatoshis(100_000_000), s_now);
        relay.MarkSending();

        // Act
        await PaymentAccountingEvents.StageTrampolineRelaySettledAsync(unitOfWork.Object, relay, null, 0,
                                                                       NullLogger.Instance,
                                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(staged);
    }

    [Fact]
    public void Given_ALostPart_When_Built_Then_ItIsAForwardLossOfThePartsAmount()
    {
        // Act
        var part = Part(TestChannelId, 3, 40_000_000);
        var closeTxId = new TxId(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var lost = PaymentAccountingEvents.TrampolinePartLostOnchain("fwd:x", part, null, closeTxId, null, s_now, 900);

        // Assert
        Assert.Equal(AccountingEventKind.ForwardLostOnchain, lost.Kind);
        Assert.Equal(-40_000_000, lost.AmountMsat);
        Assert.Equal(PaymentAccountingEvents.UpstreamOnchainCause, lost.Details["cause"]);
        Assert.Equal(closeTxId.ToString(), lost.Details[AccountingDetailKeys.CloseTxId]);
        Assert.Equal(AccountingEventKeys.TrampolineRelaySettled(s_hash), lost.Details["settledKey"]);
        var books = BooksSimulator.Of([lost]);
        Assert.Equal(40_000_000, books[AccountRole.LossOnchain]);
    }

    [Fact]
    public void Given_PartsOnTheSameChannel_When_Built_Then_TheirAmountsAreSummedPerChannelInPartOrder()
    {
        // Arrange (NL-899): two parts on the other channel around one on ours
        TrampolineRelayPartModel[] parts =
        [
            Part(s_otherChannelId, 1, 30_000_000), Part(TestChannelId, 2, 50_000_000),
            Part(s_otherChannelId, 3, 20_000_000)
        ];

        // Act
        var settled = PaymentAccountingEvents.TrampolineRelaySettled(Fulfilled(), parts, null, null);

        // Assert
        Assert.Equal($"{s_otherChannelId}:50000000,{TestChannelId}:50000000",
                     settled.Details[PaymentAccountingEvents.TrampolineIncomingAmountsDetail]);
    }

    private static TrampolineRelayModel Fulfilled(ulong feeEarnedMsat = 1_000)
    {
        var relay = new TrampolineRelayModel(s_hash, Point(0x0B), LightningMoney.MilliSatoshis(99_000_000), 600,
                                             LightningMoney.MilliSatoshis(100_000_000), s_now.AddMinutes(-1));
        relay.MarkSending();
        relay.MarkFulfilled(s_preimage, LightningMoney.MilliSatoshis(feeEarnedMsat), s_now);
        return relay;
    }

    private static TrampolineRelayPartModel Part(ChannelId channelId, ulong htlcId, ulong amountMsat) =>
        new(s_hash, channelId, htlcId, LightningMoney.MilliSatoshis(amountMsat), 640, SecretOf(0x31), SecretOf(0x32),
            null);

    private static PaymentModel Payment(ulong amountMsat, ulong feeMsat) =>
        PaymentModel.Restore(s_hash, null, Point(0x0C), LightningMoney.MilliSatoshis(amountMsat),
                             LightningMoney.MilliSatoshis(feeMsat), s_now.AddMinutes(-1), PaymentStatus.Succeeded,
                             s_otherChannelId, 2, s_preimage, null, null, null, s_now, isTrampolineRelay: true);

    private static (Mock<IUnitOfWork> UnitOfWork, List<AccountingEventModel> Staged) UnitOfWork(
        IReadOnlyList<TrampolineRelayPartModel> parts, PaymentModel? payment)
    {
        var staged = new List<AccountingEventModel>();
        var events = new Mock<IAccountingEventDbRepository>();
        events.Setup(e => e.Add(It.IsAny<AccountingEventModel>())).Callback<AccountingEventModel>(staged.Add);
        events.Setup(e => e.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string key, CancellationToken _) => staged.Any(s => s.EventKey == key));
        var relays = new Mock<ITrampolineRelayDbRepository>();
        relays.Setup(r => r.GetPartsAsync(s_hash)).ReturnsAsync(parts);
        var payments = new Mock<IPaymentDbRepository>();
        payments.Setup(p => p.GetByPaymentHashAsync(s_hash)).ReturnsAsync(payment);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(events.Object);
        unitOfWork.SetupGet(u => u.TrampolineRelayDbRepository).Returns(relays.Object);
        unitOfWork.SetupGet(u => u.PaymentDbRepository).Returns(payments.Object);
        return (unitOfWork, staged);
    }
}