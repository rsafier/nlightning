namespace NLightning.Integration.Tests.Docker.Abcd;

using Fixtures;

public class OnceOnlyBuildTests
{
    [Fact]
    public async Task Given_FailedBuildInCache_When_RequestedAgain_Then_FailsWithOriginalErrorWithoutRebuilding()
    {
        // Arrange: the ABCD network pattern, a build that leaves state behind when it fails
        var cache = new SharedObjectCache();
        var builds = 0;
        var original = new TimeoutException("channels never became usable");

        async Task<Built> GetAsync()
        {
            var build = await cache.GetOrCreateAsync("abcd", () => OnceOnlyBuild<Built>.RunAsync(() =>
            {
                builds++;
                return Task.FromException<Built>(original);
            }));
            return build.GetOrThrow("test network");
        }

        // Act
        var first = await Assert.ThrowsAsync<InvalidOperationException>(GetAsync);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(GetAsync);

        // Assert
        Assert.Equal(1, builds);
        Assert.Same(original, first.InnerException);
        Assert.Same(original, second.InnerException);
        Assert.Contains("channels never became usable", second.Message);
    }

    [Fact]
    public async Task Given_SuccessfulBuild_When_CacheDisposed_Then_BuiltObjectDisposedOnce()
    {
        // Arrange
        var cache = new SharedObjectCache();
        var built = new Built();
        var build = await cache.GetOrCreateAsync("abcd", () => OnceOnlyBuild<Built>.RunAsync(() => Task.FromResult(built)));

        // Act
        var value = build.GetOrThrow("test network");
        cache.DisposeAll();

        // Assert
        Assert.Same(built, value);
        Assert.Null(build.Failure);
        Assert.Equal(1, built.Disposals);
    }

    private sealed class Built : IAsyncDisposable
    {
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}