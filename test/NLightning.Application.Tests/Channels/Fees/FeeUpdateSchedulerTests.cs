using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Fees;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Handlers;

/// <summary>
/// BOLT2 plan N9-T1: <see cref="FeeUpdateScheduler"/> sends the policy's <c>update_fee</c> through the channel
/// operations for the channels we fund, and nothing otherwise. The context channel is funded with 800,000 sat on our
/// side at 2,500 sat/kw.
/// </summary>
public class FeeUpdateSchedulerTests
{
    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IFeeService> _feeService = new();
    private readonly NodeOptions _nodeOptions = new()
    {
        EnableHtlcs = true,
        FeeUpdates = new FeeUpdateOptions { NonAnchorFeerateMarginPercent = 100 }
    };

    [Fact]
    public async Task Given_EstimateUp20Pct_When_RoundRuns_Then_UpdateFeeQueued()
    {
        // Arrange
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(3_000);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.Sent);
        Assert.Equal(3_000U, outcome.Decision.FeeratePerKw);
        _operations.Verify(o => o.UpdateFeeAsync(NormalOperationTestContext.TestChannelId, 3_000,
                                                 It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_EstimateWithinThreshold_When_RoundRuns_Then_NothingSent()
    {
        // Arrange
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(2_900);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(Assert.Single(outcomes).Sent);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ChannelWeDoNotFund_When_RoundRuns_Then_NeverSent()
    {
        // Arrange - BOLT 2: the non-funder MUST NOT send update_fee
        var context = new NormalOperationTestContext(localIsFunder: false);
        SetupChannels(context.Channel);
        SetupEstimate(10_000);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outcomes);
        _operations.VerifyNoOtherCalls();
        _feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_ChannelNotOpen_When_RoundRuns_Then_Skipped(ChannelState state)
    {
        // Arrange
        var context = new NormalOperationTestContext(state: state);
        SetupChannels(context.Channel);
        SetupEstimate(10_000);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outcomes);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_OperationRefused_When_RoundRuns_Then_OutcomeNotSentAndRoundCompletes()
    {
        // Arrange - e.g. the peer is away: nothing was persisted, the next round tries again
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(3_000);
        _operations.Setup(o => o.UpdateFeeAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new CommitmentRefusedException("B2-NO-02", "peer away"));

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Sent);
        Assert.Contains("B2-NO-02", outcome.Reason);
    }

    [Fact]
    public async Task Given_FeeServiceFails_When_RoundRuns_Then_NothingSent()
    {
        // Arrange
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("offline"));

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outcomes);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoEstimateYet_When_RoundRuns_Then_NothingSent()
    {
        // Arrange - FeeService reports 0 until its first successful fetch
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(0);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outcomes);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NodeDustLimit_When_IncreaseWouldTrimTooMuch_Then_TheCappedFeerateIsSent()
    {
        // Arrange - two locked-in 7,000 sat HTLCs; the node's 10,000 sat limit applies (the snapshot has none)
        var context = new NormalOperationTestContext();
        context.LockIn(HtlcDirection.Incoming, 7_000_000, NormalOperationTestContext.SecretOf(1));
        context.LockIn(HtlcDirection.Incoming, 7_000_000, NormalOperationTestContext.SecretOf(2));
        _nodeOptions.MaxDustHtlcExposureMsat = 10_000_000;
        SetupChannels(context.Channel);
        SetupEstimate(12_000);

        // Act
        var outcomes = await CreateScheduler().RunOnceAsync(TestContext.Current.CancellationToken);

        // Assert - capped below the 9,183 sat/kw from which both HTLCs are trimmed on our commitment
        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.Sent);
        Assert.Equal(9_182U, outcome.Decision.FeeratePerKw);
        _operations.Verify(o => o.UpdateFeeAsync(NormalOperationTestContext.TestChannelId, 9_182,
                                                 It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_Started_When_IntervalElapses_Then_RoundsRunUntilStopped()
    {
        // Arrange
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(3_000);
        _nodeOptions.FeeUpdates.Interval = TimeSpan.FromMilliseconds(20);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Setup(o => o.UpdateFeeAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                   .Callback(() => sent.TrySetResult())
                   .Returns(Task.CompletedTask);
        await using var scheduler = CreateScheduler();

        // Act
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await scheduler.StopAsync();
        var callsAfterStop = _operations.Invocations.Count;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(callsAfterStop, _operations.Invocations.Count);
    }

    [Fact]
    public async Task Given_Disabled_When_Started_Then_NoRoundRuns()
    {
        // Arrange
        var context = new NormalOperationTestContext();
        SetupChannels(context.Channel);
        SetupEstimate(3_000);
        _nodeOptions.FeeUpdates.Enabled = false;
        _nodeOptions.FeeUpdates.Interval = TimeSpan.FromMilliseconds(10);
        await using var scheduler = CreateScheduler();

        // Act
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await scheduler.StopAsync();

        // Assert
        _operations.VerifyNoOtherCalls();
        _channels.Verify(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()), Times.Never);
    }

    private FeeUpdateScheduler CreateScheduler() =>
        new(_channels.Object, _operations.Object, _feeService.Object, NullLogger<FeeUpdateScheduler>.Instance,
            Options.Create(_nodeOptions));

    private void SetupChannels(params ChannelModel[] channels) =>
        _channels.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => channels.Where(predicate).ToList());

    private void SetupEstimate(long satPerKw) =>
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(LightningMoney.Satoshis(satPerKw));
}