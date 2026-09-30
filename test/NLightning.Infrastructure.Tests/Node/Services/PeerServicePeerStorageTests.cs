using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Node.Services;

/// <summary>
/// The BOLT 1 peer storage arms of <see cref="PeerService"/>: 7 and 9 go to the <see cref="IPeerStorageService"/>,
/// which is told of the init exchange before anyone waiting for it (so a retrieval precedes channel_reestablish).
/// </summary>
public class PeerServicePeerStorageTests
{
    private readonly Mock<IPeerCommunicationService> _communication = new();
    private readonly FeatureOptions _features = new() { ChainHashes = [ChainConstants.Regtest] };
    private readonly Mock<IPeerStorageService> _storage = new();

    public PeerServicePeerStorageTests()
    {
        var pubKey = new byte[33];
        pubKey[0] = 0x02;
        _communication.SetupGet(x => x.PeerCompactPubKey).Returns(new CompactPubKey(pubKey));
        _communication.Setup(x => x.SendMessageAsync(It.IsAny<IMessage>())).Returns(Task.CompletedTask);
    }

    public static TheoryData<IMessage> PeerStorageMessages => new()
    {
        new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1, 2, 3 })),
        new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 4, 5 }))
    };

    [Theory]
    [MemberData(nameof(PeerStorageMessages))]
    public void Given_APeerStorageService_When_APeerStorageMessageArrives_Then_ItIsHandedOver(IMessage message)
    {
        // Arrange
        var peerService = CreatePeerService(_storage.Object);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(message);

        // Assert
        _storage.Verify(s => s.HandleMessage(peerService, message), Times.Once);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(PeerStorageMessages))]
    public void Given_NoPeerStorageService_When_APeerStorageMessageArrives_Then_ItIsDroppedAndThePeerStays(
        IMessage message)
    {
        // Arrange
        CreatePeerService(null);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(message);

        // Assert
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_APeerStorageService_When_InitIsAccepted_Then_ItIsToldBeforeTheInitWaitCompletes()
    {
        // Arrange
        var events = new List<string>();
        _storage.Setup(s => s.OnPeerInitialized(It.IsAny<IPeerService>()))
                .Callback<IPeerService>(p => events.Add(p.WaitForInitAsync().IsCompleted
                                                            ? "storage after init wait"
                                                            : "storage before init wait"));
        var peerService = CreatePeerService(_storage.Object);

        // Act
        RaiseMessage(CreateInitMessage());

        // Assert
        Assert.Equal(["storage before init wait"], events);
        Assert.True(peerService.WaitForInitAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        _storage.Verify(s => s.OnPeerInitialized(peerService), Times.Once);
    }

    [Fact]
    public void Given_PeerInitHandledWhileOursIsBeingSent_When_OursGoesOut_Then_TheStorageServiceIsToldOnce()
    {
        // Arrange: the peer's init arrives while ours is still being written (BOLT 1: init goes first)
        var events = new List<string>();
        _communication.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>()))
                      .Returns(() =>
                       {
                           RaiseMessage(CreateInitMessage());
                           events.Add("our init sent");
                           return Task.CompletedTask;
                       });
        _storage.Setup(s => s.OnPeerInitialized(It.IsAny<IPeerService>())).Callback(() => events.Add("storage"));

        // Act
        CreatePeerService(_storage.Object);

        // Assert
        Assert.Equal(["our init sent", "storage"], events);
    }

    [Fact]
    public void Given_ThePeerOffersStorageAndWeDoNot_When_ItsInitIsAccepted_Then_ItsAdvertisementIsKept()
    {
        // Arrange: our configuration advertises option_provide_storage No, the peer's init offers it (NL-433)
        var ours = new FeatureOptions
        {
            ChainHashes = [ChainConstants.Regtest],
            OptionProvideStorage = FeatureSupport.No
        };
        var peerService = new PeerService(_communication.Object, ours, NullLogger<PeerService>.Instance,
                                          TimeSpan.FromSeconds(1));
        var theirs = new FeatureOptions
        {
            ChainHashes = [ChainConstants.Regtest],
            OptionProvideStorage = FeatureSupport.Optional
        };

        // Act
        RaiseMessage(new InitMessage(new InitPayload(theirs.GetNodeFeatures()),
                                     new NetworksTlv([ChainConstants.Regtest])));

        // Assert: the peer's own advertisement is kept next to the negotiated set, which folds ours in
        Assert.Equal(FeatureSupport.Optional, peerService.PeerFeatures.OptionProvideStorage);
        Assert.Equal(FeatureSupport.No, peerService.Features.OptionProvideStorage);
    }

    [Fact]
    public async Task Given_APeerStorageMessage_When_Sent_Then_ItGoesToTheConnection()
    {
        // Arrange
        var peerService = CreatePeerService(null);
        var message = new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(new byte[] { 9 }));

        // Act
        await peerService.SendPeerStorageMessageAsync(message);

        // Assert
        _communication.Verify(x => x.SendMessageAsync(message), Times.Once);
    }

    [Fact]
    public void Given_APeerStorageService_When_AWarningArrives_Then_ItsTextIsHandedOver()
    {
        // Arrange
        var peerService = CreatePeerService(_storage.Object);
        RaiseMessage(CreateInitMessage());

        // Act: LDK's refusal of our peer_storage blob for its size (NL-559)
        RaiseMessage(new WarningMessage(new ErrorPayload(
                         System.Text.Encoding.UTF8.GetBytes("Supports only data up to 1024 bytes in peer storage."))));

        // Assert: the refusal is adapted to, and the connection stays (NL-532)
        _storage.Verify(s => s.HandleWarning(peerService, "Supports only data up to 1024 bytes in peer storage."),
                        Times.Once);
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public void Given_NoPeerStorageService_When_AWarningArrives_Then_ThePeerStays()
    {
        // Arrange
        CreatePeerService(null);
        RaiseMessage(CreateInitMessage());

        // Act
        RaiseMessage(new WarningMessage(new ErrorPayload("peer storage blob too large")));

        // Assert
        _communication.Verify(x => x.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnotherMessage_When_SentAsPeerStorage_Then_Throws()
    {
        // Arrange
        var peerService = CreatePeerService(null);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => peerService.SendPeerStorageMessageAsync(
                                                        new PongMessage(4)));
    }

    private PeerService CreatePeerService(IPeerStorageService? storage) =>
        new(_communication.Object, _features, NullLogger<PeerService>.Instance, TimeSpan.FromSeconds(1),
            peerStorage: storage);

    private void RaiseMessage(IMessage message) =>
        _communication.Raise(x => x.MessageReceived += null, _communication.Object, message);

    private InitMessage CreateInitMessage() =>
        new(new InitPayload(_features.GetNodeFeatures()), new NetworksTlv([ChainConstants.Regtest]));
}