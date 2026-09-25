using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
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
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
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
}