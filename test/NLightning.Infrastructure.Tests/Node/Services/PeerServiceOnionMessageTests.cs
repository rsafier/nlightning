using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.Services;

/// <summary>
/// The BOLT 4 onion message transport of <see cref="PeerService"/> (wave M6, OM2-T1): a 513 goes to the
/// <see cref="IOnionMessageService"/> and never takes the channel path; without a service, or with it off, it is
/// dropped and the peer stays; the send is gated on the negotiated <c>option_onion_messages</c>.
/// </summary>
public class PeerServiceOnionMessageTests
{
    private readonly Mock<IPeerCommunicationService> _communication = new();
    private readonly Mock<IOnionMessageService> _onionMessages = new();

    public PeerServiceOnionMessageTests()
    {
        var pubKey = new byte[33];
        pubKey[0] = 0x02;
        _communication.SetupGet(x => x.PeerCompactPubKey).Returns(new CompactPubKey(pubKey));
        _communication.Setup(x => x.SendMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        _onionMessages.SetupGet(s => s.IsAvailable).Returns(true);
    }

    [Fact]
    public void Given_AnAvailableService_When_AnOnionMessageArrives_Then_ItIsHandedOverAndNotRaisedAsAChannelMessage()
    {
        // Arrange
        var peerService = CreatePeerService(Features(FeatureSupport.Optional), _onionMessages.Object);
        var channelMessages = 0;
        peerService.OnChannelMessageReceived += (_, _) => channelMessages++;
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));
        var message = CreateOnionMessage();

        // Act
        RaiseMessage(message);

        // Assert
        _onionMessages.Verify(s => s.HandleIncoming(peerService, message), Times.Once);
        Assert.Equal(0, channelMessages);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public void Given_NoService_When_AnOnionMessageArrives_Then_ItIsDroppedAndThePeerStays()
    {
        // Arrange
        var peerService = CreatePeerService(Features(FeatureSupport.Optional), null);
        var channelMessages = 0;
        peerService.OnChannelMessageReceived += (_, _) => channelMessages++;
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));

        // Act
        RaiseMessage(CreateOnionMessage());

        // Assert
        Assert.Equal(0, channelMessages);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
        _communication.Verify(x => x.SendWarningAsync(It.IsAny<Domain.Exceptions.WarningException>(),
                                                      It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Given_AServiceThatIsOff_When_AnOnionMessageArrives_Then_ItIsNotHandedOver()
    {
        // Arrange
        _onionMessages.SetupGet(s => s.IsAvailable).Returns(false);
        CreatePeerService(Features(FeatureSupport.Optional), _onionMessages.Object);
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));

        // Act
        RaiseMessage(CreateOnionMessage());

        // Assert
        _onionMessages.Verify(s => s.HandleIncoming(It.IsAny<IPeerService>(), It.IsAny<OnionMessageMessage>()),
                              Times.Never);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_AServiceThatThrows_When_AnOnionMessageArrives_Then_ThePeerStaysAndTheNextOneIsHandedOver()
    {
        // Arrange
        var calls = 0;
        _onionMessages.Setup(s => s.HandleIncoming(It.IsAny<IPeerService>(), It.IsAny<OnionMessageMessage>()))
                      .Callback(() =>
                       {
                           if (++calls == 1)
                               throw new InvalidOperationException("boom");
                       });
        CreatePeerService(Features(FeatureSupport.Optional), _onionMessages.Object);
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));

        // Act
        RaiseMessage(CreateOnionMessage());
        RaiseMessage(CreateOnionMessage());

        // Assert
        Assert.Equal(2, calls);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_NoInitYet_When_AnOnionMessageArrives_Then_TheConnectionClosesAndNothingIsHandedOver()
    {
        // Arrange: BOLT 1, the first message must be init
        CreatePeerService(Features(FeatureSupport.Optional), _onionMessages.Object);

        // Act
        RaiseMessage(CreateOnionMessage());

        // Assert
        _onionMessages.Verify(s => s.HandleIncoming(It.IsAny<IPeerService>(), It.IsAny<OnionMessageMessage>()),
                              Times.Never);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Once);
    }

    [Fact]
    public async Task Given_NegotiatedOnionMessages_When_SendOnionMessageAsync_Then_ItGoesToTheConnection()
    {
        // Arrange
        var peerService = CreatePeerService(Features(FeatureSupport.Optional), null);
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));
        var message = CreateOnionMessage();
        using var cts = new CancellationTokenSource();

        // Act
        await peerService.SendOnionMessageAsync(message, cts.Token);

        // Assert
        _communication.Verify(x => x.SendMessageAsync(message, cts.Token), Times.Once);
    }

    [Theory]
    [InlineData(FeatureSupport.Optional, FeatureSupport.No)]
    [InlineData(FeatureSupport.No, FeatureSupport.Optional)]
    public async Task Given_OnionMessagesNotNegotiated_When_SendOnionMessageAsync_Then_ThrowsAndSendsNothing(
        FeatureSupport ours, FeatureSupport theirs)
    {
        // Arrange
        var peerService = CreatePeerService(Features(ours), null);
        RaiseMessage(CreateInitMessage(theirs));
        _communication.Invocations.Clear();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => peerService.SendOnionMessageAsync(CreateOnionMessage(), TestContext.Current.CancellationToken));
        _communication.Verify(x => x.SendMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_NoInitYet_When_SendOnionMessageAsync_Then_Throws()
    {
        // Arrange
        var peerService = CreatePeerService(Features(FeatureSupport.Optional), null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => peerService.SendOnionMessageAsync(CreateOnionMessage(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ANullMessage_When_SendOnionMessageAsync_Then_Throws()
    {
        // Arrange
        var peerService = CreatePeerService(Features(FeatureSupport.Optional), null);
        RaiseMessage(CreateInitMessage(FeatureSupport.Optional));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => peerService.SendOnionMessageAsync(null!, TestContext.Current.CancellationToken));
    }

    private static OnionMessageMessage CreateOnionMessage()
    {
        var pathKey = new byte[33];
        pathKey[0] = 0x03;
        pathKey[32] = 0x01;
        return new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(pathKey), new byte[1366]));
    }

    /// <summary>
    /// Our features: <c>option_onion_messages</c> is experimental until wave M6 proves it, so it is advertised only
    /// with <see cref="FeatureOptions.AllowExperimentalFeatures"/>.
    /// </summary>
    private static FeatureOptions Features(FeatureSupport onionMessages) => new()
    {
        ChainHashes = [ChainConstants.Regtest],
        AllowExperimentalFeatures = true,
        OptionOnionMessages = onionMessages
    };

    private PeerService CreatePeerService(FeatureOptions features, IOnionMessageService? onionMessages) =>
        new(_communication.Object, features, NullLogger<PeerService>.Instance, TimeSpan.FromSeconds(1),
            onionMessages: onionMessages);

    private void RaiseMessage(IMessage message) =>
        _communication.Raise(x => x.MessageReceived += null, _communication.Object, message);

    private static InitMessage CreateInitMessage(FeatureSupport onionMessages) =>
        new(new InitPayload(Features(onionMessages).GetNodeFeatures()), new NetworksTlv([ChainConstants.Regtest]));
}