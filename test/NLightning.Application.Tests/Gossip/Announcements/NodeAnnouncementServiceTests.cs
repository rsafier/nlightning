using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Gossip.Announcements;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Gossip.Addresses;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Persistence;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;

/// <summary>
/// BOLT 7 plan G1-T6: our <c>node_announcement</c> with a real signer, a graph row store shared across "restarts"
/// (new service instances) and a settable clock.
/// </summary>
public class NodeAnnouncementServiceTests : IDisposable
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);

    private readonly AnnouncementTestPair _pair = new();
    private readonly SettableTimeProvider _clock = new(s_now);
    private readonly Mock<IGraphDbRepository> _graphDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly GossipOptions _gossipOptions = new();
    private readonly ServiceProvider _provider;
    private GraphNodeRecord? _stored;
    private GraphNodeRecord? _staged;
    private int _saves;

    public NodeAnnouncementServiceTests()
    {
        _graphDb.Setup(g => g.GetNodeAsync(It.IsAny<CompactPubKey>())).ReturnsAsync(() => _stored);
        _graphDb.Setup(g => g.UpsertNodeAsync(It.IsAny<GraphNodeRecord>()))
                .Callback((GraphNodeRecord record) => _staged = record)
                .Returns(Task.CompletedTask);
        _unitOfWork.SetupGet(u => u.GraphDbRepository).Returns(_graphDb.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            _stored = _staged ?? _stored;
            _staged = null;
            _saves++;
            return Task.CompletedTask;
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        _provider = services.BuildServiceProvider();
    }

    private AnnouncementTestNode Alice => _pair.Alice;

    [Fact]
    public async Task Given_NoAnnouncedChannel_When_Announcing_Then_NothingIsMade()
    {
        // Arrange (BOLT 7: others ignore the node_announcement of a node without an announced channel)
        var service = CreateService();

        // Act
        var announcement = await service.AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(announcement);
        Assert.Empty(Alice.Sink.NodeAnnouncements);
        Assert.Empty(Alice.Relay.Queued);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Given_AnAnnouncedChannel_When_Announcing_Then_SignedWithOurFieldsAndSavedFirst()
    {
        // Arrange
        MarkAnnounced();
        Alice.NodeOptions.Alias = "nltg-⚡";
        Alice.NodeOptions.Color = "#102030";
        _gossipOptions.AnnounceAddresses = ["node.example.com:9735", "203.0.113.5:9735"];
        var service = CreateService();

        // Act
        var announcement = await service.AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert: fields
        Assert.NotNull(announcement);
        Assert.Equal(Alice.NodeId, announcement.NodeId);
        Assert.Equal("nltg-⚡", announcement.GetAliasText());
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, announcement.RgbColor.ToArray());
        Assert.Equal((uint)s_now.ToUnixTimeSeconds(), announcement.Timestamp);
        var addresses = AddressDescriptorCodec.DecodeList(announcement.Addresses.Span).Addresses;
        Assert.Equal([AddressDescriptorType.IPv4, AddressDescriptorType.Dns], addresses.Select(a => a.Type));
        Assert.Equal(Alice.NodeOptions.Features.GetNodeFeatures(FeatureContext.NodeAnnouncement).GetWireBytes(),
                     announcement.Features.ToArray());
        Assert.True(Alice.Verifier.Verify(announcement.GetSignatureHash(), announcement.Signature, Alice.NodeId));

        // Saved as our node's graph row, then handed to the graph and the relay
        Assert.Equal(1, _saves);
        Assert.NotNull(_stored);
        Assert.Equal(announcement.Timestamp, _stored.Timestamp);
        Assert.Equal(announcement.GetBytes(), _stored.RawAnnouncement);
        Assert.Same(announcement, Assert.Single(Alice.Sink.NodeAnnouncements));
        Assert.Same(announcement, Assert.Single(Alice.Relay.Queued));
        Assert.Same(announcement, service.Current);
    }

    [Fact]
    public async Task Given_AStoredAnnouncement_When_RestartedInTheSameSecond_Then_TheTimestampStillIncreases()
    {
        // Arrange (BOLT 7: MUST set timestamp greater than any previous node_announcement it created)
        MarkAnnounced();
        var first = await CreateService().AnnounceAsync(TestContext.Current.CancellationToken);

        // Act: a new process on the same database, same clock
        var second = await CreateService().AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Timestamp + 1, second.Timestamp);
        Assert.Equal(second.Timestamp, _stored!.Timestamp);
    }

    [Fact]
    public async Task Given_AStoredTimestampAheadOfTheClock_When_Announcing_Then_ItIsExceeded()
    {
        // Arrange: the clock went back since the last announcement
        MarkAnnounced();
        _stored = new GraphNodeRecord(Alice.NodeId, (uint)s_now.ToUnixTimeSeconds() + 3_600, [],
                                      new byte[GraphNodeRecord.AliasLength], new byte[3], [], [0x00], s_now);

        // Act
        var announcement = await CreateService().AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((uint)s_now.ToUnixTimeSeconds() + 3_601, announcement!.Timestamp);
    }

    [Fact]
    public async Task Given_NothingChanged_When_AnnouncingAgain_Then_TheSameOneIsPublishedWithoutASave()
    {
        // Arrange
        MarkAnnounced();
        var service = CreateService();
        var first = await service.AnnounceAsync(TestContext.Current.CancellationToken);
        _clock.Now = s_now.AddDays(1);

        // Act
        var second = await service.AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task Given_ANewAlias_When_AnnouncingAgain_Then_ANewerOneIsMade()
    {
        // Arrange
        MarkAnnounced();
        var service = CreateService();
        var first = await service.AnnounceAsync(TestContext.Current.CancellationToken);
        Alice.NodeOptions.Alias = "renamed";

        // Act
        var second = await service.AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotSame(first, second);
        Assert.Equal("renamed", second!.GetAliasText());
        Assert.True(second.Timestamp > first!.Timestamp);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public async Task Given_TheRefreshIntervalPassed_When_AnnouncingAgain_Then_ItIsSignedAgain()
    {
        // Arrange (a node_announcement older than two weeks may be pruned by peers)
        MarkAnnounced();
        var service = CreateService();
        var first = await service.AnnounceAsync(TestContext.Current.CancellationToken);
        _clock.Now = s_now + _gossipOptions.NodeAnnouncementRefreshInterval;

        // Act
        var second = await service.AnnounceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((uint)_clock.Now.ToUnixTimeSeconds(), second!.Timestamp);
        Assert.True(second.Timestamp > first!.Timestamp);
    }

    [Fact]
    public async Task Given_TheSaveFails_When_Announcing_Then_NothingIsPublished()
    {
        // Arrange
        MarkAnnounced();
        _unitOfWork.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("disk full"));
        var service = CreateService();

        // Act
        service.RequestAnnouncement();
        await service.LastRequest;

        // Assert
        Assert.Null(service.Current);
        Assert.Empty(Alice.Sink.NodeAnnouncements);
        Assert.Empty(Alice.Relay.Queued);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private NodeAnnouncementService CreateService() =>
        new(Alice.Channels, Alice.Signer, NullLogger<NodeAnnouncementService>.Instance,
            new OwnGossipPublisher(Alice.Sink, Alice.Relay), _provider, Options.Create(Alice.NodeOptions),
            Options.Create(_gossipOptions), _clock);

    /// <summary>Both halves of the channel's announcement_signatures exchanged.</summary>
    private void MarkAnnounced()
    {
        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        Alice.Channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(signature, signature));
        Alice.Channel.MarkAnnouncementSignaturesSent(s_now);
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}