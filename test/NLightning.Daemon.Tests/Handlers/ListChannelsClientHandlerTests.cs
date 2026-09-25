namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
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
        var commitmentNumber = new CommitmentNumber(CreatePubKey(3), CreatePubKey(4), new Sha256(), 5);
        var channel = CreateChannel(channelId, s_alice, ChannelState.Open, commitmentNumber,
                                    new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), CreatePubKey(3),
                                                          CreatePubKey(4), fundingTxId, 1),
                                    LightningMoney.MilliSatoshis(699_999_001), LightningMoney.MilliSatoshis(300_000_999),
                                    localOfferedHtlcs: [CreateHtlc(channelId, 0, HtlcDirection.Outgoing)],
                                    remoteOfferedHtlcs:
                                    [
                                        CreateHtlc(channelId, 0, HtlcDirection.Incoming),
                                        CreateHtlc(channelId, 1, HtlcDirection.Incoming)
                                    ]);
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
        Assert.Equal(5UL, info.RemoteCommitmentNumber);
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

    private ListChannelsClientHandler CreateHandler() =>
        new(_channelMemoryRepositoryMock.Object, _peerManagerMock.Object, _unitOfWorkMock.Object);

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
                                              ICollection<Htlc>? remoteOfferedHtlcs = null)
    {
        return new ChannelModel(new ChannelParams(), channelId, commitmentNumber, fundingOutput, true, null, null,
                                localBalance ?? LightningMoney.Satoshis(100_000),
                                new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                remoteBalance ?? LightningMoney.Zero, null, 0, peerId, 0, state, ChannelVersion.V1,
                                localOfferedHtlcs, remoteOfferedHtlcs: remoteOfferedHtlcs);
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