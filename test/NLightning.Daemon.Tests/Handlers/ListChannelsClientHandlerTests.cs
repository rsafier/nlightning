using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;

public class ListChannelsClientHandlerTests
{
    private static readonly CompactPubKey s_alice = CreatePubKey(1);
    private static readonly CompactPubKey s_bob = CreatePubKey(2);

    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();
    private readonly Mock<IChannelDbRepository> _channelDbRepositoryMock = new();
    private readonly Mock<IPeerManager> _peerManagerMock = new();
    private readonly Mock<IReestablishTracker> _reestablishTrackerMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IChannelFundingDbRepository> _channelFundingDbRepositoryMock = new();
    private readonly Mock<IBlockchainMonitor> _blockchainMonitorMock = new();
    private readonly Mock<IRetiredScidMap> _retiredScidMapMock = new();
    private readonly NodeOptions _nodeOptions = new();

    public ListChannelsClientHandlerTests()
    {
        _unitOfWorkMock.SetupGet(x => x.ChannelDbRepository).Returns(_channelDbRepositoryMock.Object);
        _channelDbRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync([]);
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns([]);
    }

    [Fact]
    public async Task Given_OpenChannel_When_HandleAsync_Then_EveryFieldIsMapped()
    {
        // Arrange
        var channelId = CreateChannelId(7);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)9, 32).ToArray());
        var commitmentNumber = new CommitmentNumber(CreatePubKey(3), CreatePubKey(4), new Sha256());
        var channel = CreateChannel(channelId, s_alice, ChannelState.Open, commitmentNumber,
                                    new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), CreatePubKey(3),
                                                          CreatePubKey(4), fundingTxId, 1),
                                    LightningMoney.MilliSatoshis(699_999_001), LightningMoney.MilliSatoshis(300_000_999),
                                    localOfferedHtlcs: [CreateHtlc(channelId, 0, HtlcDirection.Outgoing)],
                                    remoteOfferedHtlcs:
                                    [
                                        CreateHtlc(channelId, 0, HtlcDirection.Incoming),
                                        CreateHtlc(channelId, 1, HtlcDirection.Incoming)
                                    ], localCommitmentNumber: 5, remoteCommitmentNumber: 6);
        channel.ShortChannelId = new ShortChannelId(120, 3, 1);
        SetupMemory(channel);
        _peerManagerMock.Setup(x => x.GetPeer(s_alice)).Returns(new PeerModel(s_alice, "127.0.0.1", 9735, "tcp"));

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Equal(channelId, info.ChannelId);
        Assert.Equal(s_alice, info.PeerId);
        Assert.Equal(ChannelState.Open, info.State);
        Assert.True(info.IsInitiator);
        Assert.True(info.IsPeerConnected);
        Assert.Equal(new ShortChannelId(120, 3, 1), info.ShortChannelId);
        Assert.Equal(fundingTxId, info.FundingTxId);
        Assert.Equal((ushort)1, info.FundingOutputIndex);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), info.Capacity);
        Assert.Equal(699_999_001UL, info.LocalBalance.MilliSatoshi);
        Assert.Equal(300_000_999UL, info.RemoteBalance.MilliSatoshi);
        Assert.Equal(5UL, info.LocalCommitmentNumber);
        Assert.Equal(6UL, info.RemoteCommitmentNumber);
        Assert.Equal(1, info.OfferedHtlcCount);
        Assert.Equal(2, info.ReceivedHtlcCount);
        Assert.False(info.DataLossDetected);
    }

    [Fact]
    public async Task Given_UnconfirmedChannelAndPeerOffline_When_HandleAsync_Then_NoScidAndDisconnected()
    {
        // Arrange
        SetupMemory(CreateChannel(CreateChannelId(1), s_alice, ChannelState.V1FundingSigned));

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Null(info.ShortChannelId);
        Assert.Null(info.FundingTxId);
        Assert.False(info.IsPeerConnected);
        Assert.Equal(LightningMoney.Zero, info.Capacity);
        Assert.Equal(0UL, info.LocalCommitmentNumber);
    }

    [Fact]
    public async Task Given_ChannelInMemoryAndInDb_When_HandleAsync_Then_MemoryStateWinsAndDbOnlyChannelsFollow()
    {
        // Arrange
        var liveId = CreateChannelId(1);
        var closedId = CreateChannelId(2);
        SetupMemory(CreateChannel(liveId, s_alice, ChannelState.Open));
        _channelDbRepositoryMock.Setup(x => x.GetAllAsync())
                                .ReturnsAsync([
                                     CreateChannel(liveId, s_alice, ChannelState.ReadyForUs),
                                     CreateChannel(closedId, s_bob, ChannelState.Closed)
                                 ]);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(response.Channels,
                          live =>
                          {
                              Assert.Equal(liveId, live.ChannelId);
                              Assert.Equal(ChannelState.Open, live.State);
                          },
                          closed =>
                          {
                              Assert.Equal(closedId, closed.ChannelId);
                              Assert.Equal(ChannelState.Closed, closed.State);
                          });
    }

    [Fact]
    public async Task Given_PeerFilter_When_HandleAsync_Then_OnlyThatPeersChannelsAreListed()
    {
        // Arrange
        var aliceChannel = CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open);
        var bobChannel = CreateChannel(CreateChannelId(2), s_bob, ChannelState.Open);
        SetupMemory(aliceChannel, bobChannel);
        _channelDbRepositoryMock.Setup(x => x.GetAllAsync())
                                .ReturnsAsync([CreateChannel(CreateChannelId(3), s_bob, ChannelState.Closed)]);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest { PeerId = s_bob },
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, response.Channels.Count);
        Assert.All(response.Channels, c => Assert.Equal(s_bob, c.PeerId));
    }

    [Fact]
    public async Task Given_SnapshotWithPendingAndSettledHtlcs_When_HandleAsync_Then_OnlyPendingHtlcsAreCounted()
    {
        // Arrange: NL-241. After a reload the legacy collections are empty; the snapshot is the truth
        var channelId = CreateChannelId(7);
        var channel = CreateChannel(channelId, s_alice, ChannelState.Open);
        channel.UpdateCommitments(CreateSnapshot(channelId,
        [
            Record(HtlcDirection.Outgoing, 0, HtlcState.SentAddAckRevocation),
            Record(HtlcDirection.Outgoing, 1, HtlcState.RcvdRemoveAckRevocation,
                 HtlcRemoval.Fulfill(new Secret(new byte[32]))),
            Record(HtlcDirection.Outgoing, 2, HtlcState.SentAddHtlc),
            Record(HtlcDirection.Incoming, 0, HtlcState.RcvdAddAckRevocation),
            Record(HtlcDirection.Incoming, 1, HtlcState.SentRemoveAckRevocation, HtlcRemoval.Fail(new byte[10])),
            Record(HtlcDirection.Incoming, 2, HtlcState.SentRemoveHtlc, HtlcRemoval.Fail(new byte[10]))
        ], localCommitmentNumber: 5, remoteCommitmentNumber: 6));
        SetupMemory(channel);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Equal(2, info.OfferedHtlcCount);
        Assert.Equal(2, info.ReceivedHtlcCount);
        Assert.Equal(5UL, info.LocalCommitmentNumber);
        Assert.Equal(6UL, info.RemoteCommitmentNumber);
    }

    [Fact]
    public async Task Given_ReloadedChannelWithSnapshotAndStaleLegacyHtlcs_When_HandleAsync_Then_SnapshotWins()
    {
        // Arrange: the legacy collections must not be counted once a snapshot exists (NL-241)
        var channelId = CreateChannelId(8);
        var channel = CreateChannel(channelId, s_alice, ChannelState.Open,
                                    localOfferedHtlcs: [CreateHtlc(channelId, 0, HtlcDirection.Outgoing)],
                                    remoteOfferedHtlcs: [CreateHtlc(channelId, 0, HtlcDirection.Incoming)]);
        channel.UpdateCommitments(CreateSnapshot(channelId, []));
        _channelDbRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync([channel]);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Equal(0, info.OfferedHtlcCount);
        Assert.Equal(0, info.ReceivedHtlcCount);
    }

    [Fact]
    public async Task Given_RoutingOptions_When_HandleAsync_Then_FeePolicyIsReportedAndNotReestablished()
    {
        // Arrange
        _nodeOptions.Routing.FeeBaseMsat = 2_000;
        _nodeOptions.Routing.FeeProportionalMillionths = 500;
        SetupMemory(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open));

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Equal(2_000U, info.FeeBaseMsat);
        Assert.Equal(500U, info.FeePpm);
        Assert.False(info.IsReestablished);
    }

    [Fact]
    public async Task Given_TrackerReportsOneChannelReestablished_When_HandleAsync_Then_OnlyThatChannelIsReestablished()
    {
        // Arrange: the flag comes from the N7 reestablish tracker (true only on the peer's current connection)
        var reestablished = CreateChannelId(1);
        var notReestablished = CreateChannelId(2);
        SetupMemory(CreateChannel(reestablished, s_alice, ChannelState.Open),
                    CreateChannel(notReestablished, s_bob, ChannelState.Open));
        _reestablishTrackerMock.Setup(x => x.IsReestablished(reestablished)).Returns(true);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(response.Channels, c => c.ChannelId == reestablished).IsReestablished);
        Assert.False(Assert.Single(response.Channels, c => c.ChannelId == notReestablished).IsReestablished);
    }

    [Fact]
    public async Task Given_ChannelMarkedDataLoss_When_HandleAsync_Then_DataLossIsReported()
    {
        // Arrange
        var channel = CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open);
        channel.MarkDataLossDetected();
        SetupMemory(channel);

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(response.Channels).DataLossDetected);
    }

    [Fact]
    public async Task Given_ChannelWithoutFundingRows_When_HandleAsync_Then_FundingOutputIsTheCurrentFunding()
    {
        // Arrange: a channel funded before the splice schema (no ChannelFundings rows) lists its funding output
        var fundingTxId = CreateTxId(9);
        var channel = CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open,
                                    fundingOutput: new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
                                                                         CreatePubKey(3), CreatePubKey(4), fundingTxId,
                                                                         1));
        channel.ShortChannelId = new ShortChannelId(120, 3, 1);
        SetupMemory(channel);
        SetupFundings(channel.ChannelId);
        _blockchainMonitorMock.SetupGet(x => x.LastProcessedBlockHeight).Returns(125);

        // Act
        var response = await CreateHandler(true).HandleAsync(new ListChannelsClientRequest(),
                                                             TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        var funding = Assert.Single(info.Fundings);
        Assert.Equal(fundingTxId, funding.FundingTxId);
        Assert.Equal((ushort)1, funding.OutputIndex);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), funding.Capacity);
        Assert.Equal(ChannelFundingStatus.Current, funding.Status);
        Assert.Equal(ChannelFundingKind.Initial, funding.Kind);
        Assert.Equal(new ShortChannelId(120, 3, 1), funding.ShortChannelId);
        Assert.Equal(6U, funding.Depth);
        Assert.Empty(info.RetiredShortChannelIds);
    }

    [Fact]
    public async Task Given_SplicedChannel_When_HandleAsync_Then_CurrentPendingAndRetiredFundingsAreListedInOrder()
    {
        // Arrange: the first splice locked (initial funding replaced, its scid retired; an older replaced funding
        // whose scid expired), a splice pending and unconfirmed, an RBF attempt confirmed but not locked, and a
        // discarded sibling
        var channelId = CreateChannelId(7);
        var initialScid = new ShortChannelId(100, 1, 0);
        var currentScid = new ShortChannelId(200, 2, 1);
        var channel = CreateChannel(channelId, s_alice, ChannelState.Open,
                                    fundingOutput: new FundingOutputInfo(LightningMoney.Satoshis(1_500_000),
                                                                         CreatePubKey(3), CreatePubKey(4),
                                                                         CreateTxId(2), 1));
        channel.ShortChannelId = currentScid;
        SetupMemory(channel);
        SetupFundings(channelId,
                      Funding(CreateTxId(5), ChannelFundingKind.Initial, ChannelFundingStatus.Replaced, 900_000,
                              confirmedHeight: 50, scid: new ShortChannelId(50, 1, 0)),
                      Funding(CreateTxId(1), ChannelFundingKind.Splice, ChannelFundingStatus.Replaced, 1_000_000,
                              confirmedHeight: 100, scid: initialScid),
                      Funding(CreateTxId(2), ChannelFundingKind.Splice, ChannelFundingStatus.Current, 1_500_000,
                              confirmedHeight: 200, scid: currentScid, lockedSent: true, lockedReceived: true),
                      Funding(CreateTxId(3), ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_000_000),
                      Funding(CreateTxId(4), ChannelFundingKind.Splice, ChannelFundingStatus.Discarded, 1_900_000),
                      Funding(CreateTxId(6), ChannelFundingKind.SpliceRbf, ChannelFundingStatus.Pending, 2_100_000,
                              confirmedHeight: 205, lockedSent: true));
        _blockchainMonitorMock.SetupGet(x => x.LastProcessedBlockHeight).Returns(207);
        _retiredScidMapMock.Setup(x => x.GetByChannel(channelId))
                           .Returns([
                                new RetiredShortChannelId(initialScid, channelId, 205,
                                                          205 + RetiredShortChannelId.RetentionBlocks)
                            ]);

        // Act
        var response = await CreateHandler(true).HandleAsync(new ListChannelsClientRequest(),
                                                             TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        Assert.Collection(info.Fundings,
                          current =>
                          {
                              Assert.Equal(CreateTxId(2), current.FundingTxId);
                              Assert.Equal(ChannelFundingStatus.Current, current.Status);
                              Assert.Equal(ChannelFundingKind.Splice, current.Kind);
                              Assert.Equal(LightningMoney.Satoshis(1_500_000), current.Capacity);
                              Assert.Equal(8U, current.Depth);
                              Assert.Equal(currentScid, current.ShortChannelId);
                              Assert.True(current.SpliceLockedSent);
                              Assert.True(current.SpliceLockedReceived);
                          },
                          pending =>
                          {
                              Assert.Equal(CreateTxId(3), pending.FundingTxId);
                              Assert.Equal(ChannelFundingStatus.Pending, pending.Status);
                              Assert.Null(pending.Depth);
                              Assert.Null(pending.ShortChannelId);
                              Assert.False(pending.SpliceLockedSent);
                          },
                          confirmedPending =>
                          {
                              Assert.Equal(CreateTxId(6), confirmedPending.FundingTxId);
                              Assert.Equal(ChannelFundingKind.SpliceRbf, confirmedPending.Kind);
                              Assert.Equal(3U, confirmedPending.Depth);
                              Assert.True(confirmedPending.SpliceLockedSent);
                              Assert.False(confirmedPending.SpliceLockedReceived);
                          },
                          replaced =>
                          {
                              Assert.Equal(CreateTxId(1), replaced.FundingTxId);
                              Assert.Equal(ChannelFundingStatus.Replaced, replaced.Status);
                              Assert.Equal(initialScid, replaced.ShortChannelId);
                              Assert.Equal(108U, replaced.Depth);
                          });
        var retired = Assert.Single(info.RetiredShortChannelIds);
        Assert.Equal(initialScid, retired.ShortChannelId);
        Assert.Equal(205U, retired.RetiredAtHeight);
        Assert.Equal(277U, retired.ExpiresAtHeight);
    }

    [Fact]
    public async Task Given_NoChainMonitorAndNoRetiredMap_When_HandleAsync_Then_DepthUnknownAndNoReplacedFunding()
    {
        // Arrange: without the optional services nothing is guessed
        var channelId = CreateChannelId(7);
        SetupMemory(CreateChannel(channelId, s_alice, ChannelState.Open));
        SetupFundings(channelId,
                      Funding(CreateTxId(1), ChannelFundingKind.Initial, ChannelFundingStatus.Replaced, 1_000_000,
                              confirmedHeight: 100, scid: new ShortChannelId(100, 1, 0)),
                      Funding(CreateTxId(2), ChannelFundingKind.Splice, ChannelFundingStatus.Current, 1_500_000,
                              confirmedHeight: 200, scid: new ShortChannelId(200, 2, 1)));

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var info = Assert.Single(response.Channels);
        var current = Assert.Single(info.Fundings);
        Assert.Equal(CreateTxId(2), current.FundingTxId);
        Assert.Null(current.Depth);
        Assert.Empty(info.RetiredShortChannelIds);
    }

    [Fact]
    public async Task Given_ClosedAndStaleChannelsInDb_When_HandleAsync_Then_OnlyTheLiveChannelsFundingsAreRead()
    {
        // Arrange: one live channel and two persisted finished ones; only the live one's rows are queried
        var liveId = CreateChannelId(1);
        var closedId = CreateChannelId(2);
        var staleId = CreateChannelId(3);
        var closedTxId = CreateTxId(8);
        SetupMemory(CreateChannel(liveId, s_alice, ChannelState.Open));
        _channelDbRepositoryMock.Setup(x => x.GetAllAsync())
                                .ReturnsAsync([
                                     CreateChannel(closedId, s_bob, ChannelState.Closed,
                                                   fundingOutput: new FundingOutputInfo(
                                                       LightningMoney.Satoshis(700_000), CreatePubKey(3),
                                                       CreatePubKey(4), closedTxId, 0)),
                                     CreateChannel(staleId, s_bob, ChannelState.Stale)
                                 ]);
        SetupFundings(liveId,
                      Funding(CreateTxId(2), ChannelFundingKind.Splice, ChannelFundingStatus.Current, 1_500_000));

        // Act
        var response = await CreateHandler(true).HandleAsync(new ListChannelsClientRequest(),
                                                             TestContext.Current.CancellationToken);

        // Assert
        _channelFundingDbRepositoryMock.Verify(x => x.GetByChannelIdAsync(liveId), Times.Once);
        _channelFundingDbRepositoryMock.Verify(x => x.GetByChannelIdAsync(closedId), Times.Never);
        _channelFundingDbRepositoryMock.Verify(x => x.GetByChannelIdAsync(staleId), Times.Never);
        Assert.Equal(3, response.Channels.Count);
        Assert.Equal(CreateTxId(2), Assert.Single(response.Channels[0].Fundings).FundingTxId);
        var closedFunding = Assert.Single(response.Channels.Single(c => c.ChannelId == closedId).Fundings);
        Assert.Equal(closedTxId, closedFunding.FundingTxId);
        Assert.Equal(ChannelFundingStatus.Current, closedFunding.Status);
        Assert.Empty(response.Channels.Single(c => c.ChannelId == staleId).Fundings);
    }

    [Fact]
    public async Task Given_UnitOfWorkWithoutFundingStore_When_HandleAsync_Then_FundingOutputIsListed()
    {
        // Arrange: a unit of work that stores no fundings throws NotSupportedException from the default member
        var fundingTxId = CreateTxId(9);
        SetupMemory(CreateChannel(CreateChannelId(1), s_alice, ChannelState.Open,
                                  fundingOutput: new FundingOutputInfo(LightningMoney.Satoshis(500_000),
                                                                       CreatePubKey(3), CreatePubKey(4), fundingTxId,
                                                                       0)));
        _unitOfWorkMock.SetupGet(x => x.ChannelFundingDbRepository).Throws(new NotSupportedException());

        // Act
        var response = await CreateHandler().HandleAsync(new ListChannelsClientRequest(),
                                                         TestContext.Current.CancellationToken);

        // Assert
        var funding = Assert.Single(Assert.Single(response.Channels).Fundings);
        Assert.Equal(fundingTxId, funding.FundingTxId);
        Assert.Null(funding.ShortChannelId);
        Assert.Null(funding.Depth);
    }

    private ListChannelsClientHandler CreateHandler(bool withSpliceServices = false) =>
        new(_channelMemoryRepositoryMock.Object, _peerManagerMock.Object, _reestablishTrackerMock.Object,
            _unitOfWorkMock.Object, Options.Create(_nodeOptions), null,
            withSpliceServices ? _blockchainMonitorMock.Object : null,
            withSpliceServices ? _retiredScidMapMock.Object : null);

    private void SetupFundings(ChannelId channelId, params ChannelFunding[] fundings)
    {
        _unitOfWorkMock.SetupGet(x => x.ChannelFundingDbRepository).Returns(_channelFundingDbRepositoryMock.Object);
        _channelFundingDbRepositoryMock.Setup(x => x.GetByChannelIdAsync(channelId)).ReturnsAsync(fundings);
    }

    private static ChannelFunding Funding(TxId txId, ChannelFundingKind kind, ChannelFundingStatus status,
                                          ulong capacitySat, uint? confirmedHeight = null, ShortChannelId? scid = null,
                                          bool lockedSent = false, bool lockedReceived = false) =>
        new(txId, 1, capacitySat, CreatePubKey(3), CreatePubKey(4), 0, 0, 0, kind, status,
            ConfirmedHeight: confirmedHeight, ShortChannelId: scid, SpliceLockedSent: lockedSent,
            SpliceLockedReceived: lockedReceived);

    private static TxId CreateTxId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static HtlcRecord Record(HtlcDirection direction, ulong id, HtlcState state, HtlcRemoval? removal = null) =>
        new(direction, id, 10_000, new Hash(Enumerable.Repeat((byte)(id + 1), 32).ToArray()), 500, state, removal);

    private static ChannelCommitments CreateSnapshot(ChannelId channelId, IReadOnlyList<HtlcRecord> htlcs,
                                                     ulong localCommitmentNumber = 1,
                                                     ulong remoteCommitmentNumber = 1)
    {
        var party = new CommitmentParty(354, 10_000, 1_000, 30, 1_000_000_000);
        var @params = new CommitmentParams(true, 1_000_000, false, party, party);
        const ulong localMsat = 700_000_000;
        const ulong remoteMsat = 300_000_000;
        var nextOutgoing = htlcs.Where(h => h.Direction == HtlcDirection.Outgoing).Select(h => h.Id + 1).DefaultIfEmpty()
                                .Max();
        var nextIncoming = htlcs.Where(h => h.Direction == HtlcDirection.Incoming).Select(h => h.Id + 1).DefaultIfEmpty()
                                .Max();
        return ChannelCommitments.Restore(channelId, @params, localMsat, remoteMsat, htlcs,
                                          [new FeeUpdate(0, 1_000, HtlcState.SentAddAckRevocation)], nextOutgoing,
                                          nextIncoming,
                                          new LocalCommit(localCommitmentNumber,
                                                          new CommitmentSpec(CommitmentSide.Local, 1_000, localMsat,
                                                                             remoteMsat, []), null),
                                          new RemoteCommit(remoteCommitmentNumber,
                                                           new CommitmentSpec(CommitmentSide.Remote, 1_000, localMsat,
                                                                              remoteMsat, []), CreatePubKey(9)),
                                          null, CreatePubKey(10));
    }

    private void SetupMemory(params ChannelModel[] channels)
    {
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns((Func<ChannelModel, bool> predicate) =>
                                                 channels.Where(predicate).ToList());
    }

    private static ChannelModel CreateChannel(ChannelId channelId, CompactPubKey peerId, ChannelState state,
                                              CommitmentNumber? commitmentNumber = null,
                                              FundingOutputInfo? fundingOutput = null,
                                              LightningMoney? localBalance = null,
                                              LightningMoney? remoteBalance = null,
                                              ICollection<Htlc>? localOfferedHtlcs = null,
                                              ICollection<Htlc>? remoteOfferedHtlcs = null,
                                              ulong localCommitmentNumber = 0, ulong remoteCommitmentNumber = 0)
    {
        return new ChannelModel(new ChannelParams(), channelId, commitmentNumber, fundingOutput, true, null, null,
                                localBalance ?? LightningMoney.Satoshis(100_000),
                                new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                remoteBalance ?? LightningMoney.Zero, null, 0, peerId, 0, state, ChannelVersion.V1,
                                localOfferedHtlcs, remoteOfferedHtlcs: remoteOfferedHtlcs,
                                localCommitmentNumber: localCommitmentNumber,
                                remoteCommitmentNumber: remoteCommitmentNumber);
    }

    private static Htlc CreateHtlc(ChannelId channelId, ulong id, HtlcDirection direction)
    {
        var paymentHash = new byte[32];
        paymentHash[0] = (byte)(id + 1);
        var amount = LightningMoney.MilliSatoshis(10_000);
        var payload = new UpdateAddHtlcPayload(amount, channelId, 500, id, paymentHash, new byte[1366]);
        return new Htlc(amount, new UpdateAddHtlcMessage(payload), direction, 500, id, 0, paymentHash,
                        HtlcState.Offered);
    }

    private static ChannelId CreateChannelId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}