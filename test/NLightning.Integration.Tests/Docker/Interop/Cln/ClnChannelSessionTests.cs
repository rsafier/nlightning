namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Fixtures;

/// <summary>
/// Container-free tests of the shared-build helper of <see cref="ClnChannelSession"/>.
/// </summary>
public class ClnChannelSessionTests
{
    [Fact]
    public async Task Given_FirstCallerCancelledMidBuild_When_RequestedAgain_Then_BuildCompletesAndIsReturned()
    {
        // Arrange: a build that is still running when the first caller gives up
        var cache = new SharedObjectCache();
        var release = new TaskCompletionSource();
        var builds = 0;
        CancellationToken buildToken = default;

        async Task<Built> BuildAsync(CancellationToken ct)
        {
            builds++;
            buildToken = ct;
            await release.Task.WaitAsync(ct);
            return new Built();
        }

        Task<Built> GetAsync(CancellationToken ct) =>
            ClnChannelSession.GetOrBuildDetachedAsync<Built>(factory => cache.GetOrCreateAsync("cln", factory),
                                                             BuildAsync, TimeSpan.FromMinutes(1), "test channel", ct);

        using var firstCaller = new CancellationTokenSource();

        // Act
        var first = GetAsync(firstCaller.Token);
        await firstCaller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = GetAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        var built = await second;

        // Assert
        Assert.NotNull(built);
        Assert.Equal(1, builds);
        Assert.False(buildToken.IsCancellationRequested);
        cache.DisposeAll();
    }

    [Fact]
    public async Task Given_BuildExceedsItsTimeout_When_Requested_Then_FailsWithBuildTimeout()
    {
        // Arrange
        var cache = new SharedObjectCache();

        static async Task<Built> NeverAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Built();
        }

        // Act
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ClnChannelSession.GetOrBuildDetachedAsync<Built>(
                        factory => cache.GetOrCreateAsync("cln", factory), NeverAsync,
                        TimeSpan.FromMilliseconds(50), "test channel", TestContext.Current.CancellationToken));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(e.InnerException);
    }

    private sealed class Built : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}