using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using TestDoubles;

/// <summary>
/// Splicing plan IT4-T2: the nine interactive-tx handlers and their <c>ChannelManager</c> cases. With no negotiation in
/// progress, 66-73 are answered with <c>tx_abort</c> (and <c>tx_abort</c> is echoed), never with a warning or a
/// channel failure.
/// </summary>
public class TxMessageHandlerTests
{
    private static readonly ChannelId s_channelId = InteractiveTxHarness.ChannelId;
    private static readonly CompactPubKey s_peer = new NBitcoin.Key(Enumerable.Repeat((byte)0x22, 32).ToArray())
                                                  .PubKey.ToBytes();

    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public static TheoryData<MessageTypes> NegotiationTypes => InteractiveTxMessages.NegotiationTypes;

    [Theory]
    [MemberData(nameof(NegotiationTypes))]
    public async Task Given_NoDriverRegistered_When_Handled_Then_TxAbort(MessageTypes type)
    {
        // Arrange
        var message = InteractiveTxMessages.Create(type, s_channelId);

        // Act
        var replies = await HandleAsync(message, new FakeServiceProvider());

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Equal(s_channelId, abort.Payload.ChannelId);
    }

    [Fact]
    public async Task Given_NoDriverRegistered_When_TxAbortHandled_Then_Echoed()
    {
        // Act
        var replies = await HandleAsync(InteractiveTxMessages.Create(MessageTypes.TxAbort, s_channelId),
                                        new FakeServiceProvider());

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
    }

    [Theory]
    [MemberData(nameof(NegotiationTypes))]
    [InlineData(MessageTypes.TxAbort)]
    public async Task Given_ADriver_When_Handled_Then_TheMessageGoesToItWithThisUnitOfWork(MessageTypes type)
    {
        // Arrange
        var message = InteractiveTxMessages.Create(type, s_channelId);
        IChannelMessage reply = InteractiveTxDriver.CreateTxAbort(s_channelId, "driver");
        var driver = new Mock<IInteractiveTxDriver>();
        driver.Setup(d => d.ReceiveAsync(message, s_peer, _unitOfWork.Object, It.IsAny<CancellationToken>()))
              .ReturnsAsync([reply]);
        var services = new FakeServiceProvider();
        services.AddService(typeof(IInteractiveTxDriver), driver.Object);

        // Act
        var replies = await HandleAsync(message, services);

        // Assert
        Assert.Same(reply, Assert.Single(replies));
    }

    [Theory]
    [MemberData(nameof(NegotiationTypes))]
    public async Task Given_AKnownChannelWithoutNegotiation_When_TheChannelManagerHandlesIt_Then_TxAbortIsSent(
        MessageTypes type)
    {
        // Arrange
        var driver = new InteractiveTxDriver(new ReferenceInteractiveTxEngine(), new FakeInteractiveTxBuilder(),
                                             new FakeInteractiveTxContributor(), new FakePrevTxInspector(),
                                             new Mock<ILogger<InteractiveTxDriver>>().Object);
        var channelManager = CreateChannelManager(driver);
        _memory.Setup(r => r.TryGetChannelState(s_channelId, out It.Ref<ChannelState>.IsAny))
               .Returns(new TryGetStateDelegate((ChannelId _, out ChannelState state) =>
                {
                    state = ChannelState.Open;
                    return true;
                }));
        var raised = new List<IChannelMessage>();
        channelManager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);

        // Act
        await channelManager.HandleChannelMessageAsync(InteractiveTxMessages.Create(type, s_channelId),
                                                       new FeatureOptions(), s_peer);

        // Assert
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(raised));
        Assert.Equal(s_channelId, abort.Payload.ChannelId);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_TheChannelManagerHandlesATxMessage_Then_ErrorForThatChannel()
    {
        // Arrange (BOLT 1: an unknown channel_id gets an error, as for every other channel message)
        var channelManager = CreateChannelManager(null);
        var channelDb = new Mock<IChannelDbRepository>();
        _unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => channelManager.HandleChannelMessageAsync(
                                InteractiveTxMessages.Create(MessageTypes.TxAddInput, s_channelId),
                                new FeatureOptions(), s_peer));

        // Assert
        Assert.Equal(s_channelId, exception.ChannelId);
    }

    private delegate bool TryGetStateDelegate(ChannelId channelId, out ChannelState state);

    private async Task<IReadOnlyList<IChannelMessage>> HandleAsync(IChannelMessage message, IServiceProvider services)
    {
        var unitOfWork = _unitOfWork.Object;
        return message switch
        {
            TxAddInputMessage m => await new TxAddInputMessageHandler(services, unitOfWork)
                                      .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxAddOutputMessage m => await new TxAddOutputMessageHandler(services, unitOfWork)
                                       .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxRemoveInputMessage m => await new TxRemoveInputMessageHandler(services, unitOfWork)
                                         .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxRemoveOutputMessage m => await new TxRemoveOutputMessageHandler(services, unitOfWork)
                                          .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxCompleteMessage m => await new TxCompleteMessageHandler(services, unitOfWork)
                                      .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxSignaturesMessage m => await new TxSignaturesMessageHandler(services, unitOfWork)
                                        .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxInitRbfMessage m => await new TxInitRbfMessageHandler(services, unitOfWork)
                                     .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxAckRbfMessage m => await new TxAckRbfMessageHandler(services, unitOfWork)
                                    .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            TxAbortMessage m => await new TxAbortMessageHandler(services, unitOfWork)
                                   .HandleAsync(m, ChannelState.Open, new FeatureOptions(), s_peer),
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
    }

    private ChannelManager CreateChannelManager(IInteractiveTxDriver? driver)
    {
        var services = new FakeServiceProvider();
        services.AddService(typeof(IUnitOfWork), _unitOfWork.Object);
        services.AddService(typeof(ChannelDomainEventQueue), new ChannelDomainEventQueue());
        if (driver is not null)
            services.AddService(typeof(IInteractiveTxDriver), driver);

        var unitOfWork = _unitOfWork.Object;
        services.AddService(typeof(IChannelMessageHandler<TxAddInputMessage>),
                            new TxAddInputMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxAddOutputMessage>),
                            new TxAddOutputMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxRemoveInputMessage>),
                            new TxRemoveInputMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxRemoveOutputMessage>),
                            new TxRemoveOutputMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxCompleteMessage>),
                            new TxCompleteMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxSignaturesMessage>),
                            new TxSignaturesMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxInitRbfMessage>),
                            new TxInitRbfMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxAckRbfMessage>),
                            new TxAckRbfMessageHandler(services, unitOfWork));
        services.AddService(typeof(IChannelMessageHandler<TxAbortMessage>),
                            new TxAbortMessageHandler(services, unitOfWork));

        return new ChannelManager(new Mock<IBlockchainMonitor>().Object, new ChannelLockProvider(), _memory.Object,
                                  new Mock<ILogger<ChannelManager>>().Object, new Mock<ILightningSigner>().Object,
                                  services);
    }
}