using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Interfaces;
using Application.Channels.Services;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Handlers;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="CommitScheduler"/> (BOLT2 plan N6-T2): signs our pending updates after the save, one outstanding
/// <c>commitment_signed</c> per direction (D7), only for a live peer, debounced.
/// </summary>
public class CommitSchedulerTests
{
    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IChannelMessagePublisher> _publisher = new();
    private readonly Mock<IPeerLivenessProbe> _probe = new();
    private readonly List<IChannelMessage> _published = [];

    public CommitSchedulerTests()
    {
        _publisher.Setup(p => p.Publish(It.IsAny<CompactPubKey>(), It.IsAny<IReadOnlyList<IChannelMessage>>()))
                  .Callback((CompactPubKey _, IReadOnlyList<IChannelMessage> messages) =>
                   {
                       lock (_published)
                       {
                           _context.Calls.Add("publish");
                           _published.AddRange(messages);
                       }
                   });
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
    }

    [Fact]
    public async Task Given_PendingOffer_When_SigningNow_Then_TheCommitmentIsPersistedWithItsDiffBeforeItIsSent()
    {
        // Arrange
        AddPendingOffer();
        var scheduler = CreateScheduler(TimeSpan.Zero);

        // Act
        var signed = await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(signed);
        Assert.Equal(["apply", "save", "publish"], _context.Calls);
        Assert.IsType<CommitmentSignedMessage>(Assert.Single(_published));
        var extras = _context.Applied.Single().Extras!;
        Assert.NotNull(extras.SentCommitDiff);
        Assert.Equal(LastSentCommitmentMessage.CommitmentSigned, extras.LastSent);
        Assert.NotNull(_context.State.RemoteNextCommit);
    }

    [Fact]
    public async Task Given_UnacknowledgedCommitment_When_SigningAgain_Then_NothingIsSigned()
    {
        // Arrange - D7: never a second commitment_signed before the revoke_and_ack
        AddPendingOffer();
        var scheduler = CreateScheduler(TimeSpan.Zero);
        await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);
        AddPendingOffer();
        _context.Calls.Clear();

        // Act
        var signed = await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(signed);
        Assert.Empty(_context.Calls);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Given_PeerNotAlive_When_SigningNow_Then_NothingIsSigned()
    {
        // Arrange - ping before commit
        AddPendingOffer();
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        var scheduler = CreateScheduler(TimeSpan.Zero);

        // Act
        var signed = await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(signed);
        Assert.Empty(_context.Calls);
        Assert.Null(_context.State.RemoteNextCommit);
    }

    [Fact]
    public async Task Given_LinkDropsBeforeTheLock_When_SigningNow_Then_NothingIsSigned()
    {
        // Arrange - the connection changes between the cheap check and the lock (a reconnection: no
        // commitment_signed may go to the new connection before channel_reestablish)
        AddPendingOffer();
        _probe.SetupSequence(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                                  It.IsAny<CancellationToken>()))
              .ReturnsAsync(true)
              .ReturnsAsync(false);
        var scheduler = CreateScheduler(TimeSpan.Zero);

        // Act
        var signed = await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(signed);
        Assert.Empty(_context.Calls);
        Assert.Empty(_published);
        Assert.Null(_context.State.RemoteNextCommit);
    }

    [Fact]
    public async Task Given_NothingPending_When_SigningNow_Then_NothingIsSigned()
    {
        // Arrange
        var scheduler = CreateScheduler(TimeSpan.Zero);

        // Act
        var signed = await scheduler.SignNowAsync(TestChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(signed);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_SeveralRequestsWithinTheDebounce_When_Scheduled_Then_OneCommitmentCoversThemAll()
    {
        // Arrange
        AddPendingOffer();
        var scheduler = CreateScheduler(TimeSpan.FromMilliseconds(100));

        // Act
        scheduler.Schedule(TestChannelId);
        AddPendingOffer();
        scheduler.Schedule(TestChannelId);
        scheduler.Schedule(TestChannelId);
        await scheduler.WhenIdleAsync();

        // Assert - one signature with both HTLCs in the peer's next commitment
        Assert.IsType<CommitmentSignedMessage>(Assert.Single(_published));
        Assert.Equal(2, _context.State.RemoteNextCommit!.Commit.Spec.Htlcs.Count);
    }

    [Fact]
    public async Task Given_SaveFails_When_Scheduled_Then_TheFailureIsLoggedAndNothingIsSent()
    {
        // Arrange
        AddPendingOffer();
        _context.FailSaves();
        var scheduler = CreateScheduler(TimeSpan.Zero);

        // Act
        scheduler.Schedule(TestChannelId);
        await scheduler.WhenIdleAsync();

        // Assert
        Assert.Empty(_published);
        Assert.Null(_context.State.RemoteNextCommit);
        Assert.True(_context.State.HasPendingChangesForRemote);
    }

    /// <summary>An HTLC we offered and persisted but did not sign yet (state 10).</summary>
    private void AddPendingOffer()
    {
        var id = _context.State.LocalNextHtlcId;
        _context.SetState(_context.State.SendAdd(20_000_000, HashOf(SecretOf((byte)(id + 1))), 600, Onion).Next);
    }

    private CommitScheduler CreateScheduler(TimeSpan debounce)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.CreateTransitions());
        var provider = services.BuildServiceProvider();

        return new CommitScheduler(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object,
                                   _publisher.Object, NullLogger<CommitScheduler>.Instance, _probe.Object,
                                   provider.GetRequiredService<IServiceScopeFactory>(),
                                   Options.Create(new CommitSchedulerOptions { Debounce = debounce }));
    }
}