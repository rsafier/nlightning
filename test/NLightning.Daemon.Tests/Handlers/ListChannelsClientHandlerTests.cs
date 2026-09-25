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
using Infrastructure.Crypto.Hashes;

public class ListChannelsClientHandlerTests
{
    private static readonly CompactPubKey s_alice = CreatePubKey(1);
    private static readonly CompactPubKey s_bob = CreatePubKey(2);

    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();
    private readonly Mock<IChannelDbRepository> _channelDbRepositoryMock = new();
    private readonly Mock<IPeerManager> _peerManagerMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
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

    private ListChannelsClientHandler CreateHandler() =>
        new(_channelMemoryRepositoryMock.Object, _peerManagerMock.Object, _unitOfWorkMock.Object,
            Options.Create(_nodeOptions));

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