using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Domain.Bitcoin.Interfaces;
using Domain.Money;

/// <summary>
/// W4-E review F3: <see cref="ClosingFeeEstimator"/> fetches one estimate at a time, keeps it for
/// <see cref="ChannelCloseOptions.FeeEstimateMaxAge"/>, does not retry a failed fetch before
/// <see cref="ChannelCloseOptions.FeeEstimateRetryAfter"/>, and never waits longer than
/// <see cref="ChannelCloseOptions.FeeEstimateWaitUnderLock"/> under a channel lock.
/// </summary>
public class ClosingFeeEstimatorTests
{
    [Fact]
    public async Task Given_FreshEstimate_When_AskedAgain_Then_NotFetchedAgain()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var feeService = FeeService(LightningMoney.Satoshis(2_500));
        var estimator = Create(feeService.Object, clock);
        await estimator.PrefetchAsync(TestContext.Current.CancellationToken);

        // Act
        clock.Advance(TimeSpan.FromMinutes(9));
        var estimate = await estimator.GetUnderLockAsync();

        // Assert
        Assert.Equal(2_500UL, estimate);
        feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OldEstimate_When_Asked_Then_FetchedAgain()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var feeService = FeeService(LightningMoney.Satoshis(2_500));
        var estimator = Create(feeService.Object, clock);
        await estimator.PrefetchAsync(TestContext.Current.CancellationToken);
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(3_000));

        // Act
        clock.Advance(new ChannelCloseOptions().FeeEstimateMaxAge);
        await estimator.PrefetchAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3_000UL, estimator.Latest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_FetchFailed_When_AskedBeforeRetryAfter_Then_NotFetchedAgain(bool throws)
    {
        // Arrange: the fee service logs and swallows its own errors (then answers 0), or throws
        var clock = new ManualTimeProvider();
        var feeService = new Mock<IFeeService>();
        if (throws)
            feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new HttpRequestException("unreachable"));
        else
            feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(LightningMoney.Zero);
        var estimator = Create(feeService.Object, clock);
        await estimator.PrefetchAsync(TestContext.Current.CancellationToken);

        // Act
        clock.Advance(TimeSpan.FromSeconds(30));
        var estimate = await estimator.GetUnderLockAsync();
        var afterWait = estimator.StartFetchIfDue();
        clock.Advance(new ChannelCloseOptions().FeeEstimateRetryAfter);
        var afterRetryDelay = estimator.StartFetchIfDue();
        if (afterRetryDelay is not null)
            await afterRetryDelay;

        // Assert: no value, no second fetch within the back-off, a new one after it
        Assert.Null(estimate);
        Assert.Null(afterWait);
        Assert.NotNull(afterRetryDelay);
        feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_HangingFetch_When_ManyAskUnderLock_Then_OneFetchAndBoundedWaits()
    {
        // Arrange
        var hanging = new TaskCompletionSource<LightningMoney>();
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>())).Returns(hanging.Task);
        var estimator = new ClosingFeeEstimator(feeService.Object,
                                                Options.Create(new ChannelCloseOptions
                                                {
                                                    FeeEstimateWaitUnderLock = TimeSpan.FromMilliseconds(20)
                                                }),
                                                NullLogger<ClosingFeeEstimator>.Instance);

        // Act
        var estimates = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => estimator.GetUnderLockAsync()));
        hanging.SetResult(LightningMoney.Satoshis(4_000));
        for (var i = 0; i < 200 && estimator.Latest is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        // Assert: nobody got a value in time, one fetch served everyone, and its late answer is kept
        Assert.All(estimates, Assert.Null);
        feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(4_000UL, estimator.Latest);
    }

    private static Mock<IFeeService> FeeService(LightningMoney feerate)
    {
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feerate);
        return feeService;
    }

    private static ClosingFeeEstimator Create(IFeeService feeService, TimeProvider clock) =>
        new(feeService, Options.Create(new ChannelCloseOptions()), NullLogger<ClosingFeeEstimator>.Instance, clock);
}