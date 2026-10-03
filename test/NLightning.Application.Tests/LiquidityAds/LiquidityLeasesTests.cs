using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.LiquidityAds;

using Application.Channels.Close;
using Application.Channels.Interfaces;
using Application.Channels.Services;
using Application.LiquidityAds;
using Channels.Handlers;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The lease of a liquidity sale (liquidity ads decision D-L4, NL-850): our cooperative close of a channel we sold
/// liquidity on is refused inside the lease unless forced (<see cref="ChannelCloseService"/>), and a channel's purchases
/// are marked closed in the save that makes the channel Closed (<see cref="LiquidityLeases.StageChannelClosedAsync"/>).
/// </summary>
public class LiquidityLeasesTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x0e, 32).ToArray());
    private static readonly FundingRate s_rate = new(100_000, 1_000_000, 500, 100, 10, 1_000);

    private readonly Mock<ILiquidityPurchaseDbRepository> _purchases = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<LiquidityPurchaseModel> _updated = [];

    public LiquidityLeasesTests()
    {
        _unitOfWork.SetupGet(u => u.LiquidityPurchaseDbRepository).Returns(_purchases.Object);
        _purchases.Setup(r => r.Update(It.IsAny<LiquidityPurchaseModel>()))
                  .Callback((LiquidityPurchaseModel p) => _updated.Add(p));
    }

    #region Marking purchases closed

    [Fact]
    public async Task Given_PurchasesInEveryStatus_When_TheChannelCloses_Then_PendingAndActiveAreClosed()
    {
        // Arrange: active with a lease from block 100 (ends 4,132), pending, replaced and an earlier closed one
        var active = Purchase(1, LiquidityPurchaseStatus.Active);
        var pending = Purchase(2, LiquidityPurchaseStatus.Pending);
        var replaced = Purchase(3, LiquidityPurchaseStatus.Replaced);
        var closed = Purchase(4, LiquidityPurchaseStatus.Closed);
        _purchases.Setup(r => r.GetByChannelIdAsync(s_channelId)).ReturnsAsync([active, pending, replaced, closed]);

        // Act
        await LiquidityLeases.StageChannelClosedAsync(_unitOfWork.Object, s_channelId, 2_000,
                                                      NullLogger.Instance);

        // Assert
        Assert.Equal([active, pending], _updated);
        Assert.Equal(LiquidityPurchaseStatus.Closed, active.Status);
        Assert.Equal(2_000U, active.ClosedAtHeight);
        Assert.True(active.ClosedEarly);
        Assert.Equal(LiquidityPurchaseStatus.Closed, pending.Status);
        Assert.True(pending.ClosedEarly);
        Assert.Equal(LiquidityPurchaseStatus.Replaced, replaced.Status);
        Assert.Equal(1_500U, closed.ClosedAtHeight);
    }

    [Fact]
    public async Task Given_AnActivePurchase_When_TheChannelClosesAfterTheLease_Then_NotEarly()
    {
        // Arrange
        var active = Purchase(1, LiquidityPurchaseStatus.Active);
        _purchases.Setup(r => r.GetByChannelIdAsync(s_channelId)).ReturnsAsync([active]);

        // Act: the lease ends at 4,132
        await LiquidityLeases.StageChannelClosedAsync(_unitOfWork.Object, s_channelId, 4_132,
                                                      NullLogger.Instance);

        // Assert
        Assert.Equal(LiquidityPurchaseStatus.Closed, active.Status);
        Assert.False(active.ClosedEarly);
    }

    [Fact]
    public async Task Given_AUnitOfWorkThatStoresNoPurchases_When_TheChannelCloses_Then_NothingThrows()
    {
        // Arrange: the interface's default throws NotSupportedException
        var unitOfWork = new Mock<IUnitOfWork> { CallBase = true };

        // Act
        var exception = await Record.ExceptionAsync(() => LiquidityLeases.StageChannelClosedAsync(
                                                        unitOfWork.Object, s_channelId, 1, NullLogger.Instance));
        var lease = await LiquidityLeases.GetActiveSaleLeaseAsync(unitOfWork.Object, s_channelId, 1);

        // Assert
        Assert.Null(exception);
        Assert.Null(lease);
    }

    [Fact]
    public async Task Given_AFailingRepository_When_TheChannelCloses_Then_ItIsLoggedAndTheCloseGoesOn()
    {
        // Arrange
        _purchases.Setup(r => r.GetByChannelIdAsync(s_channelId)).ThrowsAsync(new InvalidOperationException("db"));

        // Act
        var exception = await Record.ExceptionAsync(() => LiquidityLeases.StageChannelClosedAsync(
                                                        _unitOfWork.Object, s_channelId, 1, NullLogger.Instance));

        // Assert
        Assert.Null(exception);
        Assert.Empty(_updated);
    }

    #endregion

    #region The cooperative close guard

    [Fact]
    public async Task Given_ASaleLeaseInForce_When_WeCloseTheChannel_Then_RefusedNamingTheLease()
    {
        // Arrange
        var lease = Purchase(7, LiquidityPurchaseStatus.Active);
        _purchases.Setup(r => r.GetActiveSaleLeaseAsync(s_channelId, 1_000)).ReturnsAsync(lease);
        var (service, publisher, probe) = CreateCloseService();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => service.CloseChannelAsync(s_channelId, new ChannelCloseRequest(),
                                                            TestContext.Current.CancellationToken));

        // Assert: nothing went out, the link was not even checked
        Assert.Contains("until block 4132 (3132 blocks left)", exception.Message);
        Assert.Contains("closechannel --force", exception.Message);
        Assert.Contains("410000 sat of inbound liquidity", exception.Message);
        publisher.VerifyNoOtherCalls();
        probe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_APendingSale_When_WeCloseTheChannel_Then_RefusedUntilTheLeaseIsOver()
    {
        // Arrange: the funding has not confirmed, so the lease has not even started
        _purchases.Setup(r => r.GetActiveSaleLeaseAsync(s_channelId, 1_000))
                  .ReturnsAsync(Purchase(7, LiquidityPurchaseStatus.Pending));
        var (service, _, _) = CreateCloseService();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => service.CloseChannelAsync(s_channelId, new ChannelCloseRequest(),
                                                            TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("for 4032 blocks once its funding confirms", exception.Message);
    }

    [Fact]
    public async Task Given_ASaleLeaseInForce_When_WeForceTheClose_Then_TheLeaseIsNotChecked()
    {
        // Arrange: the peer is away, so the close stops at the link check, past the guard
        _purchases.Setup(r => r.GetActiveSaleLeaseAsync(s_channelId, It.IsAny<uint>()))
                  .ReturnsAsync(Purchase(7, LiquidityPurchaseStatus.Active));
        var (service, _, probe) = CreateCloseService();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => service.CloseChannelAsync(s_channelId, new ChannelCloseRequest { Force = true },
                                                            TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("not connected", exception.Message);
        _purchases.Verify(r => r.GetActiveSaleLeaseAsync(It.IsAny<ChannelId>(), It.IsAny<uint>()), Times.Never);
        probe.Verify(p => p.IsAliveAsync(s_channelId, NormalOperationTestContext.PeerNodeId,
                                         It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NoSaleLease_When_WeCloseTheChannel_Then_TheCloseGoesOn()
    {
        // Arrange: no lease in force (no sale, a purchase we made, or a lease that ended)
        _purchases.Setup(r => r.GetActiveSaleLeaseAsync(s_channelId, 1_000))
                  .ReturnsAsync((LiquidityPurchaseModel?)null);
        var (service, _, probe) = CreateCloseService();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => service.CloseChannelAsync(s_channelId, new ChannelCloseRequest(),
                                                            TestContext.Current.CancellationToken));

        // Assert: past the guard, stopped by the link check
        Assert.Contains("not connected", exception.Message);
        probe.Verify(p => p.IsAliveAsync(s_channelId, NormalOperationTestContext.PeerNodeId,
                                         It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AChannelAlreadyClosing_When_ACloseIsAsked_Then_TheLeaseIsNotChecked()
    {
        // Arrange: the peer started the close (ShuttingDown); our call only reports
        _purchases.Setup(r => r.GetActiveSaleLeaseAsync(s_channelId, It.IsAny<uint>()))
                  .ReturnsAsync(Purchase(7, LiquidityPurchaseStatus.Active));
        var (service, _, _) = CreateCloseService(ChannelState.ShuttingDown);

        // Act
        var result = await service.CloseChannelAsync(s_channelId, new ChannelCloseRequest(),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.ShuttingDown, result.State);
        _purchases.Verify(r => r.GetActiveSaleLeaseAsync(It.IsAny<ChannelId>(), It.IsAny<uint>()), Times.Never);
    }

    #endregion

    private (ChannelCloseService Service, Mock<IChannelMessagePublisher> Publisher, Mock<IPeerLivenessProbe> Probe)
        CreateCloseService(ChannelState state = ChannelState.Open)
    {
        var channel = CreateChannel(state);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(s_channelId, out channel)).Returns(true);
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(1_000u);
        var probe = new Mock<IPeerLivenessProbe>();
        probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                        It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);
        var publisher = new Mock<IChannelMessagePublisher>();
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddSingleton(monitor.Object);
        var provider = services.BuildServiceProvider();
        var service = new ChannelCloseService(new ChannelLockProvider(), memory.Object, publisher.Object,
                                              NullLogger<ChannelCloseService>.Instance, probe.Object,
                                              new ClosingNegotiationRegistry(),
                                              provider.GetRequiredService<IServiceScopeFactory>());
        return (service, publisher, probe);
    }

    /// <summary>A sale on the channel, restored in <paramref name="status"/> (a lease from block 100).</summary>
    private static LiquidityPurchaseModel Purchase(long id, LiquidityPurchaseStatus status) =>
        LiquidityPurchaseModel.Restore(id, s_channelId, new TxId(Enumerable.Repeat((byte)id, 32).ToArray()),
                                       LiquidityPurchaseRole.Seller, LiquidityPurchaseKind.ChannelOpen, 400_000,
                                       410_000, s_rate, LiquidityPaymentType.FromChannelBalance, 1_250, 5_010,
                                       new CompactSignature(new byte[64]), [0x00, 0x20],
                                       NormalOperationTestContext.PeerNodeId, 4_032, DateTimeOffset.UnixEpoch,
                                       status,
                                       status is LiquidityPurchaseStatus.Active or LiquidityPurchaseStatus.Closed
                                           ? 100
                                           : null,
                                       status == LiquidityPurchaseStatus.Closed ? 1_500 : null,
                                       status == LiquidityPurchaseStatus.Closed);

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var capacity = LightningMoney.Satoshis(1_000_000);
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new Domain.Bitcoin.Transactions.Outputs.FundingOutputInfo(
            capacity, NormalOperationTestContext.Point(0x01), NormalOperationTestContext.Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray()),
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                            NormalOperationTestContext.Point(0x03),
                                            NormalOperationTestContext.Point(0x04),
                                            NormalOperationTestContext.Point(0x05),
                                            NormalOperationTestContext.Point(0x06),
                                            NormalOperationTestContext.Point(0x07));
        return new ChannelModel(channelParams, s_channelId, null, fundingOutput, false, null, null,
                                LightningMoney.Zero, keySet, 0, 0, capacity, keySet, 0,
                                NormalOperationTestContext.PeerNodeId, 0, state, ChannelVersion.V1);
    }
}