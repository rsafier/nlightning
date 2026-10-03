namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;

/// <summary>
/// NL-899: <c>listpayments</c> leaves the outgoing legs of trampoline relays out unless asked, says how many it left
/// out, marks a listed one, and shows the trampoline node and inner route of a payment of ours sent through one.
/// </summary>
public class ListPaymentsClientHandlerTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_790_812_800);
    private static readonly Hash s_ourHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Hash s_relayHash = new(Enumerable.Repeat((byte)0xac, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly CompactPubKey s_trampoline = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);
    private static readonly CompactPubKey s_otherTrampoline = new([0x02, .. Enumerable.Repeat((byte)0x33, 32)]);

    private readonly Mock<IPaymentService> _paymentService = new(MockBehavior.Strict);
    private readonly Mock<IPaymentDbRepository> _payments = new();
    private readonly Mock<IPaymentTrampolineHopDbRepository> _hops = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ListPaymentsClientHandlerTests()
    {
        _unitOfWork.SetupGet(u => u.PaymentTrampolineHopDbRepository).Returns(_hops.Object);
        _hops.Setup(h => h.GetByPaymentAsync(It.IsAny<Hash>(), null)).ReturnsAsync([]);
    }

    [Fact]
    public async Task Given_RelayLegsStored_When_ListedByDefault_Then_TheyAreLeftOutAndCounted()
    {
        // Arrange
        _payments.Setup(p => p.ListAsync(0, 100, false)).ReturnsAsync([OurPayment()]);
        _payments.Setup(p => p.CountTrampolineRelaysAsync()).ReturnsAsync(3);
        var handler = new ListPaymentsClientHandler(_paymentService.Object, _payments.Object, _unitOfWork.Object);

        // Act
        var response = await handler.HandleAsync(new ListPaymentsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payment = Assert.Single(response.Payments);
        Assert.False(payment.IsTrampolineRelay);
        Assert.Equal(3, response.HiddenRelayLegs);
        _payments.Verify(p => p.ListAsync(0, 100, false), Times.Once);
    }

    [Fact]
    public async Task Given_IncludeRelayLegs_When_Listed_Then_TheLegIsListedAndMarkedWithoutTrampolineHops()
    {
        // Arrange: a relay leg whose hash also has hops stored (never ours to show)
        _payments.Setup(p => p.ListAsync(5, 10, true)).ReturnsAsync([RelayLeg(), OurPayment()]);
        _hops.Setup(h => h.GetByPaymentAsync(s_relayHash, null)).ReturnsAsync([Hop(s_relayHash, 0, 0, s_trampoline)]);
        var handler = new ListPaymentsClientHandler(_paymentService.Object, _payments.Object, _unitOfWork.Object);

        // Act
        var response = await handler.HandleAsync(new ListPaymentsClientRequest
        {
            Skip = 5,
            Take = 10,
            IncludeRelayLegs = true
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, response.HiddenRelayLegs);
        Assert.Collection(response.Payments,
                          leg =>
                          {
                              Assert.True(leg.IsTrampolineRelay);
                              Assert.Null(leg.TrampolineNodeId);
                              Assert.Empty(leg.TrampolineRoute);
                          },
                          ours => Assert.False(ours.IsTrampolineRelay));
        _payments.Verify(p => p.CountTrampolineRelaysAsync(), Times.Never);
        _hops.Verify(h => h.GetByPaymentAsync(s_relayHash, It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task Given_ATrampolinePaymentOfTwoAttempts_When_Listed_Then_TheLastAttemptsNodeAndRouteAreShown()
    {
        // Arrange: attempt 0 through one trampoline node, attempt 1 through another
        _payments.Setup(p => p.ListAsync(0, 100, false)).ReturnsAsync([OurPayment()]);
        _hops.Setup(h => h.GetByPaymentAsync(s_ourHash, null))
             .ReturnsAsync([
                 Hop(s_ourHash, 0, 0, s_otherTrampoline, 1_002_000, 900),
                 Hop(s_ourHash, 0, 1, s_payee, 1_000_000, 800),
                 Hop(s_ourHash, 1, 0, s_trampoline, 1_004_000, 950),
                 Hop(s_ourHash, 1, 1, s_payee, 1_000_000, 820)
             ]);
        var handler = new ListPaymentsClientHandler(_paymentService.Object, _payments.Object, _unitOfWork.Object);

        // Act
        var response = await handler.HandleAsync(new ListPaymentsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payment = Assert.Single(response.Payments);
        Assert.Equal(s_trampoline, payment.TrampolineNodeId);
        Assert.Equal(2, payment.TrampolineAttempts);
        Assert.Collection(payment.TrampolineRoute,
                          hop =>
                          {
                              Assert.Equal(s_trampoline, hop.NodeId);
                              Assert.Equal(LightningMoney.MilliSatoshis(1_004_000), hop.Amount);
                              Assert.Equal(950u, hop.CltvExpiry);
                          },
                          hop => Assert.Equal(s_payee, hop.NodeId));
    }

    [Fact]
    public async Task Given_AUnitOfWorkWithoutTrampolineHops_When_Listed_Then_ThePaymentIsListedWithoutThem()
    {
        // Arrange: a test double whose hop repository is the interface's throwing default
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.PaymentTrampolineHopDbRepository).Throws(new NotSupportedException());
        _payments.Setup(p => p.ListAsync(0, 100, false)).ReturnsAsync([OurPayment()]);
        var handler = new ListPaymentsClientHandler(_paymentService.Object, _payments.Object, unitOfWork.Object);

        // Act
        var response = await handler.HandleAsync(new ListPaymentsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(Assert.Single(response.Payments).TrampolineNodeId);
    }

    [Fact]
    public async Task Given_NoPaymentRepository_When_Listed_Then_TheServicesPageIsFiltered()
    {
        // Arrange (hosts without the scope's payment repository: the payment service's page)
        _paymentService.Setup(s => s.ListPaymentsAsync(0, 100, It.IsAny<CancellationToken>()))
                       .ReturnsAsync([RelayLeg(), OurPayment()]);
        var handler = new ListPaymentsClientHandler(_paymentService.Object);

        // Act
        var hidden = await handler.HandleAsync(new ListPaymentsClientRequest(), TestContext.Current.CancellationToken);
        var all = await handler.HandleAsync(new ListPaymentsClientRequest { IncludeRelayLegs = true },
                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_ourHash, Assert.Single(hidden.Payments).PaymentHash);
        Assert.Equal(1, hidden.HiddenRelayLegs);
        Assert.Equal(2, all.Payments.Count);
        Assert.Equal(0, all.HiddenRelayLegs);
    }

    private static PaymentModel OurPayment() =>
        new(s_ourHash, "lnbcrt1pay", s_payee, LightningMoney.MilliSatoshis(1_000_000),
            LightningMoney.MilliSatoshis(4_000), s_now);

    private static PaymentModel RelayLeg() =>
        PaymentModel.Restore(s_relayHash, null, s_trampoline, LightningMoney.MilliSatoshis(2_000_000),
                             LightningMoney.MilliSatoshis(1_000), s_now.AddMinutes(1), PaymentStatus.Failed, null,
                             null, null, null, null, "no route", s_now.AddMinutes(2), isTrampolineRelay: true);

    private static PaymentTrampolineHopModel Hop(Hash hash, int attempt, int index, CompactPubKey node,
                                                 ulong amountMsat = 1_000_000, uint cltv = 800) =>
        new(hash, attempt, index, node, new Secret(Enumerable.Repeat((byte)(0x40 + index), 32).ToArray()),
            LightningMoney.MilliSatoshis(amountMsat), cltv);
}