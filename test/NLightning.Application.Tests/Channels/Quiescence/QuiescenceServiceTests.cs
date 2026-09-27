using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Quiescence;

using Application.Channels.Quiescence;
using Application.Channels.Services;
using Application.Protocol.Factories;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// <see cref="QuiescenceService"/> rules (BOLT 2 "Channel Quiescence", splicing plan Q1-T3): Q-S-01..03, Q-R-01, Q-R-02,
/// Q-R-04, Q-R-05, the "our updates pending" rule and the connection binding.
/// </summary>
public class QuiescenceServiceTests
{
    [Theory]
    [InlineData(true, true, true, QuiescenceInitiator.Local)]
    [InlineData(true, true, false, QuiescenceInitiator.Remote)]
    [InlineData(true, false, false, QuiescenceInitiator.Local)]
    [InlineData(true, false, true, QuiescenceInitiator.Local)]
    [InlineData(false, true, true, QuiescenceInitiator.Remote)]
    public void Given_TheStfuFlags_When_ResolvingTheInitiator_Then_FirstInitiatorOrTheFunderWins(
        bool sent, bool received, bool weAreFunder, QuiescenceInitiator expected)
    {
        // Act
        var initiator = QuiescenceRules.ResolveInitiator(sent, received, weAreFunder);

        // Assert (Q-R-05 when both are 1)
        Assert.Equal(expected, initiator);
    }

    [Fact]
    public void Given_BothFlagsZero_When_ResolvingTheInitiator_Then_Throws()
    {
        // Act / Assert: nobody initiated
        Assert.Throws<ArgumentException>(() => QuiescenceRules.ResolveInitiator(false, false, true));
    }

    [Fact]
    public void Given_WeSentNoStfu_When_ThePeerSendsStfuWithInitiatorZero_Then_WarningAndCloseAndNoQuiescence()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var service = CreateStandalone(pair.Alice.Channel, peerManager: null);

        // Act
        var warning = Assert.Throws<ChannelWarningException>(
            () => service.OnStfuReceived(pair.Alice.Channel, new StfuPayload(TwoNodeHarness.ChannelId, false),
                                         QuiescenceTestPair.QuiesceFeatures));

        // Assert (Q-S-03: initiator = 0 only replies to our stfu); nothing left for the timeout monitor
        Assert.True(warning.CloseConnection);
        Assert.Contains("Q-S-03", warning.Message);
        Assert.Equal(QuiescenceState.None, service.GetState(TwoNodeHarness.ChannelId));
        Assert.Empty(service.GetActive());
    }

    [Fact]
    public async Task Given_OurRequestNotSentYet_When_ThePeerSendsStfuWithInitiatorZero_Then_WarningAndTheRequestIsKept()
    {
        // Arrange: our request waits for its drain (our add is pending)
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        pair.Alice.PeerAlive = false;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.WhenIdleAsync();
        Assert.False(pair.State(pair.Alice).StfuSent);

        // Act
        var warning = Assert.Throws<ChannelWarningException>(
            () => pair.Quiescence(pair.Alice).OnStfuReceived(pair.Alice.Channel,
                                                             new StfuPayload(TwoNodeHarness.ChannelId, false),
                                                             QuiescenceTestPair.QuiesceFeatures));

        // Assert: refused without touching our request (the disconnection ends it)
        Assert.True(warning.CloseConnection);
        Assert.False(pair.State(pair.Alice).StfuReceived);
        Assert.False(request.IsCompleted);
        await pair.DisconnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task Given_OurAddAndThePeersAdd_When_Pending_Then_OnlyOursBlocksOurStfu()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        pair.HoldFulfills["Alice"] = true;
        pair.HoldFulfills["Bob"] = true;

        // Act 1: Alice's add is pending (10) at Alice; at Bob it is the peer's (30)
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        Assert.True(await pair.Alice.DeliverNextAsync());

        // Assert 1
        Assert.True(QuiescenceRules.HasPendingLocalUpdates(pair.Alice.State));
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Bob.State));

        // Act 2: all committed; then Bob removes it (35 at Bob, 15 at Alice)
        await pair.PumpAsync();
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Alice.State));
        await pair.Bob.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, 0, new byte[292],
                                                TestContext.Current.CancellationToken);

        // Assert 2
        Assert.True(QuiescenceRules.HasPendingLocalUpdates(pair.Bob.State));
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Alice.State));
        await pair.PumpAsync();
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Bob.State));
    }

    [Fact]
    public async Task Given_OurFeeUpdate_When_NotRevokedYet_Then_ItBlocksOurStfu()
    {
        // Arrange: Alice funds the channel
        using var pair = new QuiescenceTestPair();

        // Act
        await pair.Alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, 3_000,
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.True(QuiescenceRules.HasPendingLocalUpdates(pair.Alice.State));
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Bob.State));
        await pair.PumpAsync();
        Assert.False(QuiescenceRules.HasPendingLocalUpdates(pair.Alice.State));
    }

    [Fact]
    public async Task Given_OurRequestWaitingForItsDrain_When_ThePeersStfuArrives_Then_WeReplyWithZeroAndThePeerIsInitiator()
    {
        // Arrange: Alice's add is pending, so her request can't send stfu(1) yet; Bob asks meanwhile
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        var aliceRequest = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        var bobRequest = pair.Quiescence(pair.Bob).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);

        // Act
        await pair.PumpAsync();

        // Assert: Bob's stfu(1) went first; Alice's is a reply (0), so Bob is the initiator on both sides
        Assert.Equal(QuiescenceInitiator.Remote, await aliceRequest.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(QuiescenceInitiator.Local, await bobRequest.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal([("Bob", true, false), ("Alice", false, false)], pair.StfuSent.ToArray());
    }

    [Fact]
    public async Task Given_ARequestNotSentYet_When_TheCallerCancels_Then_ItIsWithdrawn()
    {
        // Arrange: Alice's add keeps her stfu back
        using var pair = new QuiescenceTestPair();
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        using var cts = new CancellationTokenSource();
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe,
                                                                 cts.Token);
        await pair.WhenIdleAsync();

        // Act
        await cts.CancelAsync();
        await pair.WhenIdleAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10),
                                                                       TestContext.Current.CancellationToken));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        await pair.PumpAsync();
        Assert.Empty(pair.StfuSent);
    }

    [Fact]
    public async Task Given_ARequestInProgress_When_RequestingAgain_Then_Refused()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        _ = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Splice, ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => pair.Quiescence(pair.Alice).RequestAsync(new ChannelId(new byte[32]), QuiescencePurpose.Probe, ct));
    }

    [Fact]
    public async Task Given_QuiesceNotAdvertised_When_Requesting_Then_RefusedWithQs01()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var service = CreateStandalone(pair.Alice.Channel, peerManager: null, quiesce: FeatureSupport.No);

        // Act
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe,
                                               TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("Q-S-01", e.Message);
    }

    [Fact]
    public void Given_AStfuWithoutOptionQuiesce_When_Received_Then_WarningAndClose()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var service = CreateStandalone(pair.Alice.Channel, peerManager: null);

        // Act
        var warning = Assert.Throws<ChannelWarningException>(
            () => service.OnStfuReceived(pair.Alice.Channel, new StfuPayload(TwoNodeHarness.ChannelId, true),
                                         new FeatureSet()));

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Contains("Q-S-01", warning.Message);
        Assert.Equal(QuiescenceState.None, service.GetState(TwoNodeHarness.ChannelId));
    }

    [Fact]
    public void Given_NothingPending_When_ThePeersStfuArrives_Then_WeReplyWithZeroAndAreQuiescent()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var service = CreateStandalone(pair.Alice.Channel, peerManager: null);

        // Act
        var reply = service.OnStfuReceived(pair.Alice.Channel, new StfuPayload(TwoNodeHarness.ChannelId, true),
                                           QuiescenceTestPair.QuiesceFeatures);

        // Assert (Q-R-02 reply at once, initiator 0; the peer is the initiator)
        Assert.NotNull(reply);
        Assert.False(reply.Payload.Initiator);
        var state = service.GetState(TwoNodeHarness.ChannelId);
        Assert.True(state.IsQuiescent);
        Assert.Equal(QuiescenceInitiator.Remote, state.Initiator);
        Assert.NotNull(state.QuiescentSince);
        Assert.Null(service.TryReleaseStfu(pair.Alice.Channel));
    }

    [Fact]
    public void Given_AQuiescenceOfAnOldConnection_When_ThePeerReconnected_Then_ItIsGone()
    {
        // Arrange: the peer's stfu arrives on connection A
        using var pair = new QuiescenceTestPair();
        var peerId = pair.Alice.Channel.RemoteNodeId;
        var connectionA = new PeerModel(peerId, "127.0.0.1", 9735, "IPv4");
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(p => p.GetPeer(peerId)).Returns(connectionA);
        var service = CreateStandalone(pair.Alice.Channel, peerManager.Object);
        service.OnStfuReceived(pair.Alice.Channel, new StfuPayload(TwoNodeHarness.ChannelId, true),
                               QuiescenceTestPair.QuiesceFeatures);
        Assert.True(service.GetState(TwoNodeHarness.ChannelId).IsQuiescent);

        // Act: connection B replaced it without our disconnect hook running
        peerManager.Setup(p => p.GetPeer(peerId)).Returns(new PeerModel(peerId, "127.0.0.1", 9735, "IPv4"));

        // Assert (Q-R-04)
        Assert.Equal(QuiescenceState.None, service.GetState(TwoNodeHarness.ChannelId));
        Assert.Empty(service.GetActive());
    }

    [Fact]
    public void Given_AQuiescentChannel_When_ItsConnectionCloses_Then_TheQuiescenceEnds()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var peerId = pair.Alice.Channel.RemoteNodeId;
        var connection = new PeerModel(peerId, "127.0.0.1", 9735, "IPv4");
        var peerService = new Mock<IPeerService>();
        peerService.SetupGet(p => p.Features).Returns(new FeatureOptions { OptionQuiesce = FeatureSupport.Optional });
        connection.SetPeerService(peerService.Object);
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(p => p.GetPeer(peerId)).Returns(connection);
        var service = CreateStandalone(pair.Alice.Channel, peerManager.Object);
        service.OnStfuReceived(pair.Alice.Channel, new StfuPayload(TwoNodeHarness.ChannelId, true),
                               QuiescenceTestPair.QuiesceFeatures);
        Assert.True(service.GetState(TwoNodeHarness.ChannelId).IsQuiescent);

        // Act
        peerService.Raise(p => p.OnDisconnect += null, new PeerDisconnectedEventArgs(peerId));

        // Assert (Q-R-04), even while the peer manager still lists the connection
        Assert.Equal(QuiescenceState.None, service.GetState(TwoNodeHarness.ChannelId));
    }

    [Fact]
    public void Given_ANonOpenChannel_When_AStfuArrives_Then_WarningAndClose()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var channel = pair.Alice.Channel;
        var service = CreateStandalone(channel, peerManager: null);
        channel.UpdateState(Domain.Channels.Enums.ChannelState.ShuttingDown);

        // Act / Assert
        var warning = Assert.Throws<ChannelWarningException>(
            () => service.OnStfuReceived(channel, new StfuPayload(TwoNodeHarness.ChannelId, true),
                                         QuiescenceTestPair.QuiesceFeatures));
        Assert.True(warning.CloseConnection);
    }

    private static QuiescenceService CreateStandalone(ChannelModel channel, IPeerManager? peerManager,
                                                      FeatureSupport quiesce = FeatureSupport.Optional)
    {
        var options = new NodeOptions();
        options.Features.OptionQuiesce = quiesce;
        var memory = new InMemoryChannelRepository();
        memory.AddChannel(channel);
        var services = new ServiceCollection();
        if (peerManager is not null)
            services.AddSingleton(peerManager);
        var provider = services.BuildServiceProvider();
        return new QuiescenceService(new ChannelLockProvider(), memory, NullLogger<QuiescenceService>.Instance,
                                     new MessageFactory(Options.Create(options)), provider, Options.Create(options),
                                     new ManualTimeProvider());
    }
}