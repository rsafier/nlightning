using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.DualFunding;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The thin <c>open_channel2</c>/<c>accept_channel2</c> handlers (splicing plan wave DF): they hand the message to
/// <see cref="IDualFundedOpenService"/> with the scope's unit of work, and refuse it with an <c>error</c> for the
/// (temporary) channel when no dual-funding service is registered.
/// </summary>
public class DualFundHandlerTests
{
    private static readonly CompactPubKey s_peer =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private static readonly CompactPubKey s_point =
        Convert.FromHexString("036d6caac248af96f6afa7f904f550253a0f3ef3f5aa2fe6838a95b216691468e2");

    private static readonly ChannelId s_temporaryId = new(Enumerable.Repeat((byte)0x2A, 32).ToArray());

    [Fact]
    public async Task Given_NoDualFundingService_When_OpenChannel2Arrives_Then_AnErrorForTheTemporaryChannel()
    {
        // Arrange
        var handler = new OpenChannel2MessageHandler(new FakeServiceProvider(), new Mock<IUnitOfWork>().Object);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(CreateOpenChannel2(), ChannelState.None, new FeatureOptions(),
                                                      s_peer));

        // Assert
        Assert.Equal(s_temporaryId, exception.ChannelId);
        Assert.Equal("dual-funded channels are not supported", exception.PeerMessage);
    }

    [Fact]
    public async Task Given_AKnownChannelId_When_OpenChannel2Arrives_Then_Refused()
    {
        // Arrange
        var service = new Mock<IDualFundedOpenService>();
        var provider = new FakeServiceProvider();
        provider.AddService(typeof(IDualFundedOpenService), service.Object);
        var handler = new OpenChannel2MessageHandler(provider, new Mock<IUnitOfWork>().Object);

        // Act & Assert
        await Assert.ThrowsAsync<ChannelErrorException>(
            () => handler.HandleAsync(CreateOpenChannel2(), ChannelState.Open, new FeatureOptions(), s_peer));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ADualFundingService_When_OpenChannel2Arrives_Then_ItIsAcceptedThroughTheService()
    {
        // Arrange: a service that is not the node's own gets no contribution
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var reply = new Mock<IChannelMessage>().Object;
        var message = CreateOpenChannel2();
        var features = new FeatureOptions();
        var service = new Mock<IDualFundedOpenService>();
        service.Setup(s => s.AcceptAsync(message, features, s_peer, LightningMoney.Zero, unitOfWork,
                                         It.IsAny<CancellationToken>()))
               .ReturnsAsync([reply]);
        var provider = new FakeServiceProvider();
        provider.AddService(typeof(IDualFundedOpenService), service.Object);
        var handler = new OpenChannel2MessageHandler(provider, unitOfWork);

        // Act
        var replies = await handler.HandleAsync(message, ChannelState.None, features, s_peer);

        // Assert
        Assert.Same(reply, Assert.Single(replies));
    }

    [Fact]
    public async Task Given_ADualFundingService_When_AcceptChannel2Arrives_Then_TheServiceHandlesIt()
    {
        // Arrange
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var message = CreateAcceptChannel2();
        var features = new FeatureOptions();
        var service = new Mock<IDualFundedOpenService>();
        service.Setup(s => s.HandleAcceptChannel2Async(message, features, s_peer, unitOfWork,
                                                       It.IsAny<CancellationToken>()))
               .ReturnsAsync([]);
        var provider = new FakeServiceProvider();
        provider.AddService(typeof(IDualFundedOpenService), service.Object);
        var handler = new AcceptChannel2MessageHandler(provider, unitOfWork);

        // Act
        await handler.HandleAsync(message, ChannelState.None, features, s_peer);

        // Assert
        service.Verify(s => s.HandleAcceptChannel2Async(message, features, s_peer, unitOfWork,
                                                        It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_NoDualFundingService_When_AcceptChannel2Arrives_Then_AnErrorForTheChannel()
    {
        // Arrange
        var handler = new AcceptChannel2MessageHandler(new FakeServiceProvider(), new Mock<IUnitOfWork>().Object);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(CreateAcceptChannel2(), ChannelState.None, new FeatureOptions(),
                                                      s_peer));

        // Assert
        Assert.Equal(s_temporaryId, exception.ChannelId);
    }

    private static OpenChannel2Message CreateOpenChannel2() =>
        new(new OpenChannel2Payload(BitcoinNetwork.Regtest.ChainHash, new ChannelFlags((byte)0), 2_500, s_point,
                                    LightningMoney.Satoshis(546), s_point, LightningMoney.Satoshis(500_000), 2_500,
                                    s_point, s_point, LightningMoney.MilliSatoshis(1_000), 100, 30,
                                    LightningMoney.Satoshis(500_000), s_point, s_point, s_point, 144, s_temporaryId));

    private static AcceptChannel2Message CreateAcceptChannel2() =>
        new(new AcceptChannel2Payload(s_point, LightningMoney.Satoshis(546), s_point, LightningMoney.Zero, s_point,
                                      s_point, LightningMoney.MilliSatoshis(1_000), 30,
                                      LightningMoney.Satoshis(500_000), 3, s_point, s_point, s_temporaryId, 144,
                                      s_point));
}