using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Application.Tests.Payments;
using Domain.Channels.Interfaces;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Harness;
using Infrastructure.Bitcoin;

/// <summary>
/// Options (OM3-T1), handler dispatch, the message-path factory, feature bits and the registration.
/// </summary>
public class OnionMessageComponentsTests
{
    [Fact]
    public void Given_DefaultOptions_When_Validated_Then_NoErrors()
    {
        // Act
        var errors = new OnionMessageOptions().GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(nameof(OnionMessageOptions.MaxOutboxPerPeer))]
    [InlineData(nameof(OnionMessageOptions.MaxQueuedMessages))]
    [InlineData(nameof(OnionMessageOptions.MaxQueuedHandlerWork))]
    [InlineData(nameof(OnionMessageOptions.MaxPendingReplies))]
    [InlineData(nameof(OnionMessageOptions.ReplyTimeout))]
    [InlineData(nameof(OnionMessageOptions.MaxPathHops))]
    [InlineData(nameof(OnionMessageOptions.PeerBurstBytes))]
    [InlineData(nameof(OnionMessageOptions.ConnectToReply))]
    public void Given_AnInvalidValue_When_Validated_Then_ItIsNamed(string property)
    {
        // Arrange
        var options = new OnionMessageOptions();
        switch (property)
        {
            case nameof(OnionMessageOptions.MaxOutboxPerPeer): options.MaxOutboxPerPeer = 0; break;
            case nameof(OnionMessageOptions.MaxQueuedMessages): options.MaxQueuedMessages = 0; break;
            case nameof(OnionMessageOptions.MaxQueuedHandlerWork): options.MaxQueuedHandlerWork = 0; break;
            case nameof(OnionMessageOptions.MaxPendingReplies): options.MaxPendingReplies = 0; break;
            case nameof(OnionMessageOptions.ReplyTimeout): options.ReplyTimeout = TimeSpan.Zero; break;
            case nameof(OnionMessageOptions.MaxPathHops): options.MaxPathHops = 17; break;
            case nameof(OnionMessageOptions.PeerBurstBytes): options.PeerBurstBytes = 1; break;
            default: options.ConnectToReply = true; break;
        }

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains(property));
    }

    [Fact]
    public void Given_TwoHandlersForOneType_When_Dispatching_Then_Refused()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new OnionMessageDispatcher([new RecordingHandler(64),
                                                                          new RecordingHandler(66, 64)]));
        Assert.Throws<ArgumentException>(() => new OnionMessageDispatcher([new RecordingHandler(4)]));
    }

    [Fact]
    public void Given_Handlers_When_Dispatching_Then_ByPayloadType()
    {
        // Arrange
        var invoiceRequests = new RecordingHandler(64);
        var invoices = new RecordingHandler(66, 68);
        var dispatcher = new OnionMessageDispatcher([invoiceRequests, invoices]);

        // Act / Assert
        Assert.Same(invoiceRequests, dispatcher.GetHandler(64));
        Assert.Same(invoices, dispatcher.GetHandler(68));
        Assert.Null(dispatcher.GetHandler(65));
        Assert.Equal(66UL, OnionMessageDispatcher.GetPayloadType(
                         new OnionMessageContents([new OnionMessageTlvRecord(33, new byte[] { 1 }),
                                                   new OnionMessageTlvRecord(66, new byte[] { 1 })])));
        Assert.Null(OnionMessageDispatcher.GetPayloadType(new OnionMessageContents([])));
    }

    [Fact]
    public void Given_HopsWithDifferentData_When_CreatingAMessagePath_Then_EveryEncryptedDataHasTheSameLength()
    {
        // Arrange
        using var node = new OnionMessageTestNode("carol", 3);
        var ids = new byte[] { 1, 2, 4 }.Select(s => new TestNodeKeyManager(s).NodeId).Append(node.NodeId).ToList();

        // Act
        var path = node.PathFactory.Create(ids, new byte[32]);

        // Assert
        Assert.Equal(ids[0], path.FirstNodeId);
        Assert.Equal(4, path.Hops.Count);
        Assert.Single(path.Hops.Select(h => h.EncryptedRecipientData.Length).Distinct());
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0, 0, 0, 0 }, true)] // bit 39
    [InlineData(new byte[] { 0x40, 0, 0, 0, 0 }, true)] // bit 38
    [InlineData(new byte[] { 0x01, 0x80, 0, 0, 0, 0 }, true)] // bit 39 with a longer bitmap
    [InlineData(new byte[] { 0x3f, 0xff, 0xff, 0xff, 0xff }, false)]
    [InlineData(new byte[] { 0xff, 0xff, 0xff, 0xff }, false)]
    [InlineData(new byte[0], false)]
    public void Given_WireFeatures_When_Checked_Then_Bit38Or39(byte[] features, bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, OnionMessagePathFinder.AdvertisesOnionMessages(features));
    }

    [Fact]
    public void Given_NoPacketBuilder_When_Registered_Then_TheServiceIsOff()
    {
        // Arrange
        var nodeOptions = new NodeOptions();
        nodeOptions.Features.AllowExperimentalFeatures = true;
        nodeOptions.Features.OptionOnionMessages = FeatureSupport.Optional;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(new TestNodeKeyManager(1));
        services.AddSingleton(Options.Create(nodeOptions));
        services.AddSingleton(new Mock<IPeerManager>().Object);
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();

        // Act
        services.AddOnionMessageServices();
        services.AddOnionMessageServices();
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IOnionMessageService>();

        // Assert
        Assert.Same(provider.GetRequiredService<OnionMessageService>(), service);
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task Given_TheFeatureAndABuilder_When_Registered_Then_TheServiceIsOn()
    {
        // Arrange
        using var node = new OnionMessageTestNode("alice", 1);

        // Act
        var result = await node.Service.SendAsync(OnionMessageDestination.ToNode(new TestNodeKeyManager(2).NodeId),
                                                  OnionMessageContents.Single(65, new byte[] { 1 }), null,
                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.True(node.Service.IsAvailable);
        Assert.Equal(OnionMessageSendStatus.NoPath, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_InvalidOptions_When_TheServiceIsResolved_Then_ItDoesNotThrowAndStaysOff(bool advertised)
    {
        // Arrange: the peer services resolve the service while they build every connection, so a bad section of the
        // off-by-default feature must never throw there (it would stop every peer from connecting)
        var ct = TestContext.Current.CancellationToken;
        var invalid = new OnionMessageOptions { ConnectToReply = true, MaxPathHops = 17 };

        // Act
        using var node = new OnionMessageTestNode("alice", 1, options: invalid, advertiseOnionMessages: advertised);
        var result = await node.Service.SendAsync(OnionMessageDestination.ToNode(new TestNodeKeyManager(2).NodeId),
                                                  OnionMessageContents.Single(65, new byte[] { 1 }), null, ct);

        // Assert
        Assert.False(node.Service.IsAvailable);
        Assert.Equal(OnionMessageSendStatus.NotAvailable, result.Status);
    }

    [Fact]
    public void Given_NoOnionMessageOutbox_When_TheServiceIsResolved_Then_ItStaysOff()
    {
        // Act: without the capped send path (IPeerOnionMessageOutbox) the service never sends at all
        using var node = new OnionMessageTestNode("alice", 1, registerOutbox: false);

        // Assert
        Assert.False(node.Service.IsAvailable);
    }

    [Fact]
    public void Given_ReplyTimeoutOption_When_TheServiceIsResolved_Then_ItIsTheDefaultReplyTimeout()
    {
        // Act
        using var node = new OnionMessageTestNode("alice", 1,
                                                  options: new OnionMessageOptions
                                                  {
                                                      ReplyTimeout = TimeSpan.FromSeconds(7)
                                                  });

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(7), node.Service.DefaultReplyTimeout);
    }

    [Fact]
    public async Task Given_ContentsWithEncryptedRecipientData_When_Sending_Then_Throws()
    {
        // Arrange
        using var node = new OnionMessageTestNode("alice", 1);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => node.Service.SendAsync(OnionMessageDestination.ToNode(node.NodeId),
                                         OnionMessageContents.Single(4, new byte[] { 1 }), null,
                                         TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_NoPeers_When_CreatingAReplyPath_Then_ItIsOneHopToUs()
    {
        // Arrange
        using var node = new OnionMessageTestNode("alice", 1);
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(m => m.ListPeers()).Returns([]);
        var finder = new OnionMessagePathFinder(peerManager.Object, new Mock<IChannelMemoryRepository>().Object,
                                                node.NodeId, 3);
        var factory = new ReplyPathFactory(node.PathFactory, finder, node.NodeId);
        var pathId = new byte[32];

        // Act
        var path = factory.Create(pathId);
        var unblinded = node.RouteBlinding.UnblindAsLocalNode(path.FirstPathKey, path.Hops[0].EncryptedRecipientData);

        // Assert
        Assert.Equal(node.NodeId, path.FirstNodeId);
        Assert.Single(path.Hops);
        Assert.Equal(pathId, unblinded.RecipientData.PathId!.Value.ToArray());
    }
}