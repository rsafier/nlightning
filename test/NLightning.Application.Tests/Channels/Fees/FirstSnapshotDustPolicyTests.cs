using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Handlers;
using Application.Channels.Services;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;

/// <summary>
/// NL-254 / NL-242 follow-up (BOLT2 plan N9-T3): the node's <c>max_dust_htlc_exposure_msat</c> is stored with a
/// channel's first commitment state, so the engine enforces it on our offers (B2-DUST-03/04) from the start.
/// </summary>
public class FirstSnapshotDustPolicyTests
{
    private static readonly CompactPubKey s_firstPoint = CreatePubKey(0x01);
    private static readonly CompactPubKey s_secondPoint = CreatePubKey(0x02);

    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IChannelStateDbRepository> _channelStateDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private ChannelCommitments? _staged;

    public FirstSnapshotDustPolicyTests()
    {
        _unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        _unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(_channelStateDb.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _channelStateDb.Setup(r => r.InitializeAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelStateExtras?>()))
                       .Callback((ChannelCommitments snapshot, ChannelStateExtras? _) => _staged = snapshot)
                       .Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData(5_000_000UL)]
    [InlineData(null)]
    public async Task Given_NodeDustLimit_When_FirstChannelReady_Then_TheSnapshotCarriesIt(ulong? limit)
    {
        // Arrange
        var channel = CreateChannel();
        SetupChannel(channel);
        var handler = new ChannelReadyMessageHandler(_channels.Object, NullLogger<ChannelReadyMessageHandler>.Instance,
                                                     _unitOfWork.Object,
                                                     Options.Create(new NodeOptions
                                                     {
                                                         MaxDustHtlcExposureMsat = limit
                                                     }));
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));

        // Act
        await handler.HandleAsync(message, ChannelState.V1FundingSigned, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.NotNull(_staged);
        Assert.Equal(limit, _staged.Params.MaxDustHtlcExposureMsat);
        Assert.Same(_staged, channel.Commitments);
    }

    [Fact]
    public async Task Given_HandlerWithoutOptions_When_FirstChannelReady_Then_NoPolicy()
    {
        // Arrange - the constructor used before NL-254 still works
        var channel = CreateChannel();
        SetupChannel(channel);
        var handler = new ChannelReadyMessageHandler(_channels.Object, NullLogger<ChannelReadyMessageHandler>.Instance,
                                                     _unitOfWork.Object);
        var message = new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, s_secondPoint));

        // Act
        await handler.HandleAsync(message, ChannelState.V1FundingSigned, new FeatureOptions(), channel.RemoteNodeId);

        // Assert
        Assert.NotNull(_staged);
        Assert.Null(_staged.Params.MaxDustHtlcExposureMsat);
    }

    [Fact]
    public void Given_FirstSnapshotWithALimit_When_WeOfferDustOverIt_Then_TheEngineRefuses()
    {
        // Arrange - 600,000 sat of our 1,000,000 sat channel at 2,500 sat/kw: a 2,000 sat HTLC is trimmed on both
        // commitments; the limit is 3,000 sat
        var channel = CreateChannel(LightningMoney.Satoshis(1_000_000), LightningMoney.Satoshis(2_500));
        var commitments = ChannelStateTransitionService.CreateInitialCommitments(channel, s_firstPoint,
                                                                                 s_secondPoint, 3_000_000);
        var hash = new Hash(Enumerable.Repeat((byte)0x11, 32).ToArray());
        var first = commitments.SendAdd(2_000_000, hash, 700, new byte[1366], null, 500).Next;

        // Act
        var exception = Assert.Throws<CommitmentRefusedException>(
            () => first.SendAdd(2_000_000, hash, 700, new byte[1366], null, 500));

        // Assert - B2-DUST-03 (the peer's commitment is checked first)
        Assert.Equal("B2-DUST-03", exception.RequirementId);
    }

    private void SetupChannel(ChannelModel channel)
    {
        _channels.Setup(r => r.TryGetChannel(channel.ChannelId, out It.Ref<ChannelModel>.IsAny!))
                 .Callback((ChannelId _, out ChannelModel c) =>
                  {
                      c = channel;
                  })
                 .Returns(true);
        _channelDb.Setup(r => r.GetByIdAsync(channel.ChannelId)).ReturnsAsync(channel);
    }

    private static CompactPubKey CreatePubKey(byte last)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = last;
        return bytes;
    }

    private static ChannelModel CreateChannel(LightningMoney? fundingAmount = null, LightningMoney? feerate = null)
    {
        var emptyPubKey = CreatePubKey(0x00);
        var funding = fundingAmount ?? LightningMoney.Satoshis(10_000);
        var fundingOutput = new FundingOutputInfo(funding, emptyPubKey, emptyPubKey)
        {
            TransactionId = TxId.One,
            Index = 0
        };
        var dustLimit = LightningMoney.Satoshis(546);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(1_000), feerate ?? LightningMoney.Zero,
                                                     LightningMoney.Zero, dustLimit, 30,
                                                     LightningMoney.Satoshis(funding.Satoshi), 3, false, dustLimit,
                                                     144, FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                                 emptyPubKey, emptyPubKey);
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                                              emptyPubKey, s_firstPoint);
        var commitmentNumber = new CommitmentNumber(emptyPubKey, emptyPubKey, new FakeSha256());
        var localBalance = LightningMoney.Satoshis(funding.Satoshi * 6 / 10);

        return new ChannelModel(channelParams, ChannelId.Zero, commitmentNumber, fundingOutput, true, null, null,
                                localBalance, localKeySet, 0, 0, funding - localBalance, remoteKeySet, 0,
                                emptyPubKey, 0, ChannelState.V1FundingSigned, ChannelVersion.V1);
    }
}