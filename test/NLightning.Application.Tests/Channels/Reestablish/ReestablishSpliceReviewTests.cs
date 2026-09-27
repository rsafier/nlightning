using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Handlers;
using Application.Channels.Reestablish;
using Application.Channels.Splicing.Interfaces;
using Application.InteractiveTx.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Handlers;

/// <summary>
/// Review fixes of lane SP2-A: bit 0 of <c>my_current_funding_locked</c> for a locked splice (SP-RE-02) and the
/// <c>tx_abort</c> for an unknown <c>next_funding</c> through the interactive-tx driver.
/// </summary>
public class ReestablishSpliceReviewTests
{
    private static readonly ChannelId s_channelId = NormalOperationTestContext.TestChannelId;
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.PeerNodeId;
    private static readonly TxId s_originalFunding = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly TxId s_splice = new(Enumerable.Repeat((byte)0x88, 32).ToArray());

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IRevocationVerifier> _revocationVerifier = new();
    private readonly Mock<IChannelFundingDbRepository> _fundingRows = new();

    [Fact]
    public async Task Given_ALockedPublicSpliceWithOnlyTheOldFundingsHalf_When_OursIsBuilt_Then_Bit0AsksForTheSplices()
    {
        // Arrange: the stored half signs the original funding's announcement; the splice's row has none
        var channel = CreatePublicChannel();
        var service = CreateService(FundingSet.Single(Funding(s_splice, ChannelFundingKind.Splice)),
                                    [Funding(s_splice, ChannelFundingKind.Splice)]);

        // Act
        var local = await service.GetLocalStateAsync(channel, SpliceFeatures());
        var own = await service.CreateOwnAsync(channel, SpliceFeatures());

        // Assert (SP-RE-02: bit 0 while we lack the peer's announcement_signatures for that transaction)
        Assert.DoesNotContain(s_splice, local.Splice!.AnnouncementSignaturesReceivedFor);
        Assert.Equal(s_splice, own.MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.Equal(1, own.MyCurrentFundingLockedTlv.RetransmitFlags);
    }

    [Fact]
    public async Task Given_ALockedPublicSpliceWhoseRowHoldsThePeersHalf_When_OursIsBuilt_Then_Bit0IsClear()
    {
        // Arrange
        var channel = CreatePublicChannel();
        var service = CreateService(FundingSet.Single(Funding(s_splice, ChannelFundingKind.Splice)),
                                    [
                                        Funding(s_splice, ChannelFundingKind.Splice) with
                                        {
                                            AnnouncementSignaturesReceived = true
                                        }
                                    ]);

        // Act
        var own = await service.CreateOwnAsync(channel, SpliceFeatures());

        // Assert
        Assert.Equal(s_splice, own.MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.Equal(0, own.MyCurrentFundingLockedTlv.RetransmitFlags);
    }

    [Fact]
    public async Task Given_TheOriginalFundingWithThePeersHalf_When_OursIsBuilt_Then_Bit0IsClear()
    {
        // Arrange
        var channel = CreatePublicChannel();
        var service = CreateService(FundingSet.Single(Funding(s_originalFunding, ChannelFundingKind.Initial)), []);

        // Act
        var own = await service.CreateOwnAsync(channel, SpliceFeatures());

        // Assert
        Assert.Equal(s_originalFunding, own.MyCurrentFundingLockedTlv!.FundingTxId);
        Assert.Equal(0, own.MyCurrentFundingLockedTlv.RetransmitFlags);
    }

    [Fact]
    public async Task Given_TheDriverSendsNoTxAbort_When_AnUnknownNextFundingArrives_Then_NoRawTxAbortGoesOut()
    {
        // Arrange: the driver holds a negotiation or waits for its tx_abort's echo
        var driver = new Mock<IInteractiveTxDriver>();
        driver.Setup(d => d.AbortQuiescence(s_channelId, s_peer, It.IsAny<string>())).Returns([]);
        var handler = CreateHandler(driver.Object);

        // Act
        var replies = await handler.HandleAsync(ReestablishWithNextFunding(), ChannelState.Open,
                                                new FeatureOptions(), s_peer);

        // Assert
        Assert.DoesNotContain(replies, m => m is TxAbortMessage);
        driver.Verify(d => d.AbortQuiescence(s_channelId, s_peer, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Given_TheDriverSendsATxAbort_When_AnUnknownNextFundingArrives_Then_OnlyTheDriversGoesOut()
    {
        // Arrange
        var driverAbort = new TxAbortMessage(new TxAbortPayload(s_channelId, Encoding.ASCII.GetBytes("driver")));
        var driver = new Mock<IInteractiveTxDriver>();
        driver.Setup(d => d.AbortQuiescence(s_channelId, s_peer, It.IsAny<string>())).Returns([driverAbort]);
        var handler = CreateHandler(driver.Object);

        // Act
        var replies = await handler.HandleAsync(ReestablishWithNextFunding(), ChannelState.Open,
                                                new FeatureOptions(), s_peer);

        // Assert
        Assert.Same(driverAbort, Assert.Single(replies.OfType<TxAbortMessage>()));
    }

    [Fact]
    public async Task Given_NoDriver_When_AnUnknownNextFundingArrives_Then_ARawTxAbortGoesOut()
    {
        // Arrange
        var handler = CreateHandler(null);

        // Act
        var replies = await handler.HandleAsync(ReestablishWithNextFunding(), ChannelState.Open,
                                                new FeatureOptions(), s_peer);

        // Assert
        Assert.Single(replies.OfType<TxAbortMessage>());
    }

    private ReestablishService CreateService(FundingSet fundings, IReadOnlyList<ChannelFunding> rows)
    {
        var port = new Mock<ISpliceStatePort>();
        port.Setup(p => p.GetFundings(It.IsAny<ChannelModel>())).Returns(fundings);
        _fundingRows.Setup(r => r.GetByChannelIdAsync(s_channelId)).ReturnsAsync(rows);
        _context.UnitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundingRows.Object);

        var services = new ServiceCollection();
        services.AddSingleton(port.Object);
        services.AddSingleton(_context.UnitOfWork.Object);
        return new ReestablishService(_context.LightningSigner.Object, NullLogger<ReestablishService>.Instance,
                                      _context.MessageFactory, _revocationVerifier.Object,
                                      _context.CreateTransitions(), services.BuildServiceProvider());
    }

    private ChannelReestablishMessageHandler CreateHandler(IInteractiveTxDriver? driver)
    {
        var services = new ServiceCollection();
        if (driver is not null)
            services.AddSingleton(driver);
        var provider = services.BuildServiceProvider();
        var transitions = _context.CreateTransitions();
        var service = new ReestablishService(_context.LightningSigner.Object, NullLogger<ReestablishService>.Instance,
                                             _context.MessageFactory, _revocationVerifier.Object, transitions);
        return new ChannelReestablishMessageHandler(_context.ChannelMemoryRepository.Object,
                                                    _context.LightningSigner.Object,
                                                    NullLogger<ChannelReestablishMessageHandler>.Instance,
                                                    _context.MessageFactory, _context.MessageSerializer.Object,
                                                    service, new ReestablishTracker(), transitions,
                                                    _context.UnitOfWork.Object, serviceProvider: provider);
    }

    private static ChannelReestablishMessage ReestablishWithNextFunding() =>
        new(new ChannelReestablishPayload(s_channelId, NormalOperationTestContext.Point(0x30), 1, 0, new byte[32]),
            new NextFundingTlv(Enumerable.Repeat((byte)0x99, 32).ToArray(), 1));

    private static FeatureOptions SpliceFeatures() => new()
    {
        AllowExperimentalFeatures = true,
        OptionQuiesce = FeatureSupport.Optional,
        OptionSplice = FeatureSupport.Optional
    };

    private static ChannelFunding Funding(TxId txId, ChannelFundingKind kind) =>
        new(txId, 0, NormalOperationTestContext.FundingSatoshis, NormalOperationTestContext.Point(0x01),
            NormalOperationTestContext.Point(0x02), 0, 0, 0, kind, ChannelFundingStatus.Current);

    /// <summary>An Open public channel (L = R = 0) that holds the peer's announcement_signatures half.</summary>
    private static ChannelModel CreatePublicChannel()
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30,
                                     LightningMoney.Satoshis(NormalOperationTestContext.FundingSatoshis), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No)
        {
            AnnounceChannel = true
        };
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(NormalOperationTestContext.FundingSatoshis),
                                                  NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = s_splice,
            Index = 0
        };
        var point = NormalOperationTestContext.Point;
        var keySet = new ChannelKeySetModel(0, point(0x01), point(0x03), point(0x04), point(0x05), point(0x06),
                                            point(0x07));
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(point(0x02), point(0x13), point(0x14), point(0x15),
                                                              point(0x16), point(0x20));
        var channel = new ChannelModel(channelParams, s_channelId,
                                       new CommitmentNumber(point(0x04), point(0x14), new FakeSha256()), fundingOutput,
                                       true, null, null, LightningMoney.Satoshis(800_000), keySet, 0, 0,
                                       LightningMoney.Satoshis(200_000), remoteKeySet, 0, s_peer, 0, ChannelState.Open,
                                       ChannelVersion.V1);
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(NormalOperationTestContext.Signature(5),
                                              NormalOperationTestContext.Signature(6)));
        return channel;
    }
}