using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;

public class FundingConfirmedMessageHandlerTests
{
    private static readonly CompactPubKey s_pubKey = new byte[]
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00
    };

    private sealed class ScriptedAliasHandler : FundingConfirmedMessageHandler
    {
        private readonly Queue<ShortChannelId> _candidates;

        public ScriptedAliasHandler(IEnumerable<ShortChannelId> candidates, IChannelMemoryRepository memoryRepository,
                                    ILightningSigner signer, IMessageFactory messageFactory, IUnitOfWork uow)
            : base(memoryRepository, signer, new Mock<ILogger<FundingConfirmedMessageHandler>>().Object,
                   messageFactory, uow)
        {
            _candidates = new Queue<ShortChannelId>(candidates);
        }

        protected override ShortChannelId GenerateRandomScidAlias() => _candidates.Dequeue();
    }

    private static ChannelModel CreateChannel(byte channelIdByte, FeatureSupport useScidAlias)
    {
        byte[] channelIdBytes = ChannelId.Zero;
        channelIdBytes[0] = channelIdByte;
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, useScidAlias);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());
        var fundingOutputInfo = new FundingOutputInfo(fundingAmount, s_pubKey, s_pubKey) { Index = 0 };

        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), commitmentNumber, fundingOutputInfo,
                                false, null, null, LightningMoney.Zero, keySet, 1, 0, fundingAmount, keySet, 1,
                                s_pubKey, 0, ChannelState.V1FundingSigned, ChannelVersion.V1);
    }

    [Fact]
    public async Task Given_CandidatesCollideWithUsedScids_When_GeneratingAliases_Then_CollisionsAreSkipped()
    {
        // Arrange
        var otherRealScid = new ShortChannelId(700_000, 12, 1);
        var otherAlias = new ShortChannelId(16_000_000, 1, 0);
        var ownRealScid = new ShortChannelId(800_000, 5, 0);

        var otherChannel = CreateChannel(2, FeatureSupport.Optional);
        otherChannel.ShortChannelId = otherRealScid;
        otherChannel.LocalAliases = [otherAlias];

        var channel = CreateChannel(1, FeatureSupport.Optional);
        channel.ShortChannelId = ownRealScid;

        var fresh = Enumerable.Range(1, 5).Select(i => new ShortChannelId(16_100_000, (uint)i, 0)).ToList();
        var candidates = new List<ShortChannelId>
        {
            otherAlias, otherRealScid, ownRealScid, fresh[0], fresh[0], new(0UL), fresh[1], fresh[1], fresh[2],
            fresh[3], fresh[4]
        };

        var memoryRepository = new Mock<IChannelMemoryRepository>();
        memoryRepository.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                        .Returns((Func<ChannelModel, bool> predicate) =>
                                     new[] { channel, otherChannel }.Where(predicate).ToList());

        var signer = new Mock<ILightningSigner>();
        signer.Setup(x => x.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>())).Returns(s_pubKey);

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(x => x.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);

        var handler = new ScriptedAliasHandler(candidates, memoryRepository.Object, signer.Object,
                                               new Mock<IMessageFactory>().Object, uow.Object);

        // Act
        await handler.HandleAsync(channel);

        // Assert
        Assert.NotNull(channel.LocalAliases);
        var aliases = channel.LocalAliases.ToList();
        Assert.InRange(aliases.Count, 2, 5);
        Assert.Equal(fresh.Take(aliases.Count), aliases);
    }

    [Fact]
    public async Task Given_ChannelAlreadyHasAliases_When_FundingConfirmedAgain_Then_AliasesAreReused()
    {
        // Arrange
        var existingAliases = new List<ShortChannelId>
        {
            new(16_000_000, 1, 0),
            new(16_000_000, 2, 0)
        };
        var channel = CreateChannel(1, FeatureSupport.Optional);
        channel.ShortChannelId = new ShortChannelId(800_000, 5, 0);
        channel.LocalAliases = existingAliases;

        var memoryRepository = new Mock<IChannelMemoryRepository>();
        memoryRepository.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);

        var signer = new Mock<ILightningSigner>();
        signer.Setup(x => x.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>())).Returns(s_pubKey);

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(x => x.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);

        var messageFactory = new Mock<IMessageFactory>();
        var sentAliases = new List<ShortChannelId>();
        messageFactory.Setup(x => x.CreateChannelReadyMessage(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                                              It.IsAny<ShortChannelId?>()))
                      .Callback<ChannelId, CompactPubKey, ShortChannelId?>((_, _, alias) =>
                                                                               sentAliases.Add(alias!.Value));

        // Any fresh candidate would differ from the existing aliases
        var handler = new ScriptedAliasHandler(Enumerable.Range(1, 10).Select(i => new ShortChannelId(16_200_000,
                                                   (uint)i, 0)), memoryRepository.Object, signer.Object,
                                               messageFactory.Object, uow.Object);

        // Act
        await handler.HandleAsync(channel);

        // Assert
        Assert.Same(existingAliases, channel.LocalAliases);
        Assert.Equal(existingAliases, sentAliases);
    }

    private readonly Mock<ILightningSigner> _mockLightningSigner = new();
    private readonly Mock<IMessageFactory> _mockMessageFactory = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly FundingConfirmedMessageHandler _handler;

    public FundingConfirmedMessageHandlerTests()
    {
        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(new Mock<IChannelDbRepository>().Object);
        _handler = new FundingConfirmedMessageHandler(new Mock<IChannelMemoryRepository>().Object,
                                                      _mockLightningSigner.Object,
                                                      new Mock<ILogger<FundingConfirmedMessageHandler>>().Object,
                                                      _mockMessageFactory.Object, _mockUnitOfWork.Object);
    }

    [Theory]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.Open)]
    public async Task Given_ChannelAlreadyConfirmedForUs_When_HandleAsync_Then_NothingChangesAndNoMessageIsSent(
        ChannelState state)
    {
        // Arrange
        var channel = CreateChannel(state);
        var messagesSent = 0;
        _handler.OnMessageReady += (_, _) => messagesSent++;

        // Act
        await _handler.HandleAsync(channel);

        // Assert
        Assert.Equal(state, channel.State);
        Assert.Equal(0UL, channel.LocalCommitmentNumber);
        Assert.Equal(0UL, channel.RemoteCommitmentNumber);
        Assert.Equal(0, messagesSent);
        _mockLightningSigner.Verify(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()),
                                    Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Given_FundingSignedChannel_When_HandleAsync_Then_ChannelIsReadyForUsAndChannelReadyIsSent()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        var messagesSent = 0;
        _handler.OnMessageReady += (_, _) => messagesSent++;

        // Act
        await _handler.HandleAsync(channel);

        // Assert
        Assert.Equal(ChannelState.ReadyForUs, channel.State);
        // BOLT 2: the commitment numbers never move at funding confirmation (NL-188)
        Assert.Equal(0UL, channel.LocalCommitmentNumber);
        Assert.Equal(0UL, channel.RemoteCommitmentNumber);
        Assert.Equal(1, messagesSent);
    }

    [Fact]
    public async Task Given_FundingSignedChannel_When_HandleAsync_Then_ChannelReadyCarriesPointOfCommitmentNumber1()
    {
        // Arrange - regression (NL-187): channel_ready.second_per_commitment_point is the point of commitment
        // number 1 (index 2^48-2); the signer takes numbers, so the handler must ask for number 1
        var channel = CreateChannel(ChannelState.V1FundingSigned);
        var localPointBefore = channel.LocalKeySet.CurrentPerCommitmentCompactPoint;
        var localIndexBefore = channel.LocalKeySet.CurrentPerCommitmentIndex;
        CompactPubKey secondPoint = Convert.FromHexString(
            "025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");
        _mockLightningSigner.Setup(s => s.GetPerCommitmentPoint(channel.ChannelId, 1UL)).Returns(secondPoint);
        CompactPubKey? sentPoint = null;
        _mockMessageFactory
           .Setup(f => f.CreateChannelReadyMessage(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                                   It.IsAny<ShortChannelId?>()))
           .Callback<ChannelId, CompactPubKey, ShortChannelId?>((_, point, _) => sentPoint = point);

        // Act
        await _handler.HandleAsync(channel);

        // Assert
        Assert.Equal(secondPoint, sentPoint);
        _mockLightningSigner.Verify(s => s.GetPerCommitmentPoint(channel.ChannelId, 1UL), Times.Once);
        _mockLightningSigner.Verify(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.Is<ulong>(n => n != 1)),
                                    Times.Never);
        // Our current commitment is still number 0, so its point and index stay put
        Assert.Equal(localPointBefore, channel.LocalKeySet.CurrentPerCommitmentCompactPoint);
        Assert.Equal(localIndexBefore, channel.LocalKeySet.CurrentPerCommitmentIndex);
    }

    private static ChannelModel CreateChannel(ChannelState state)
    {
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(fundingAmount, s_pubKey, s_pubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey,
                                            s_pubKey, s_pubKey);
        var commitmentNumber = new CommitmentNumber(s_pubKey, s_pubKey, new FakeSha256());

        return new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutput, false, null, null,
                                LightningMoney.Zero, keySet, 0, 0, fundingAmount, keySet, 0, s_pubKey, 0, state,
                                ChannelVersion.V1);
    }
}