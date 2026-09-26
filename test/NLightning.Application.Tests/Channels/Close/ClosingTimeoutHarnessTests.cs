using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Handlers.Interfaces;
using Application.Channels.Safety.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// NL-284 / B2-CLS-03 on two real channel managers: the funder's <c>closing_signed</c> goes unanswered (the peer
/// swallows it) and, after <see cref="ChannelCloseOptions.ClosingSignedReplyTimeout"/>, the funder fails the channel
/// through the fail-the-channel service; a peer that answers is never failed.
/// </summary>
public class ClosingTimeoutHarnessTests
{
    [Fact]
    public async Task Given_PeerNeverAnswersClosingSigned_When_ReplyTimeoutPasses_Then_FunderFailsTheChannel()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var failures = new List<ChannelFailureRequest>();
        using var close = new CloseHarness(configure: (name, services) =>
        {
            if (name == "Alice")
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(RecordingFailureService(failures));
            }
            else
            {
                services.AddScoped<IChannelMessageHandler<ClosingSignedMessage>, SilentClosingSignedHandler>();
            }
        });
        var ct = TestContext.Current.CancellationToken;
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                ct);
        await close.Harness.PumpAsync();
        Assert.Equal(ChannelState.Negotiating, close.Alice.Channel.State);
        Assert.Single(close.Bob.Received.OfType<ClosingSignedMessage>());
        var monitor = close.Alice.Services.GetRequiredService<ClosingTimeoutMonitor>();

        // Act
        clock.Advance(new ChannelCloseOptions().ClosingSignedReplyTimeout);
        await monitor.WhenIdleAsync();

        // Assert
        var request = Assert.Single(failures);
        Assert.Equal("B2-CLS-03", request.RequirementId);
        Assert.True(request.StillApplies!(close.Alice.Channel));
    }

    [Fact]
    public async Task Given_PeerAnswers_When_ReplyTimeoutPasses_Then_Closed()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var failures = new List<ChannelFailureRequest>();
        using var close = new CloseHarness(configure: (name, services) =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(RecordingFailureService(failures));
        });
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                TestContext.Current.CancellationToken);
        await close.Harness.PumpAsync();

        // Act
        clock.Advance(TimeSpan.FromHours(1));
        await close.Alice.Services.GetRequiredService<ClosingTimeoutMonitor>().WhenIdleAsync();
        await close.Bob.Services.GetRequiredService<ClosingTimeoutMonitor>().WhenIdleAsync();

        // Assert
        close.AssertClosedTogether();
        Assert.Empty(failures);
    }

    private static IChannelFailureService RecordingFailureService(List<ChannelFailureRequest> failures)
    {
        var failureService = new Mock<IChannelFailureService>();
        failureService.Setup(f => f.FailChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFailureRequest>(),
                                                     It.IsAny<CancellationToken>()))
                      .Callback((ChannelId _, ChannelFailureRequest request, CancellationToken _) =>
                       {
                           lock (failures)
                               failures.Add(request);
                       })
                      .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, null));
        return failureService.Object;
    }

    /// <summary>A peer that takes <c>closing_signed</c> and never answers.</summary>
    private sealed class SilentClosingSignedHandler : IChannelMessageHandler<ClosingSignedMessage>
    {
        public Task<IReadOnlyList<IChannelMessage>> HandleAsync(ClosingSignedMessage message,
                                                               ChannelState currentState,
                                                               FeatureOptions negotiatedFeatures,
                                                               CompactPubKey peerPubKey) =>
            Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
    }
}