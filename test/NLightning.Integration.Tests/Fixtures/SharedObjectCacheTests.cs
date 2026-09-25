namespace NLightning.Integration.Tests.Fixtures;

public class SharedObjectCacheTests
{
    [Fact]
    public async Task Given_KeyUsedForAnotherType_When_GettingIt_Then_ThrowsAClearErrorAndKeepsTheStoredObject()
    {
        // Arrange
        var cache = new SharedObjectCache();
        var stored = new List<int> { 1 };
        await cache.GetOrCreateAsync("topology", () => Task.FromResult(stored));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrCreateAsync("topology", () => Task.FromResult("other")));

        // Assert
        Assert.Contains("topology", exception.Message);
        Assert.Contains(nameof(String), exception.Message);
        Assert.Same(stored, await cache.GetOrCreateAsync("topology", () => Task.FromResult(new List<int>())));
    }

    [Fact]
    public async Task Given_CachedObject_When_GettingItAgain_Then_TheFactoryRunsOnce()
    {
        // Arrange
        var cache = new SharedObjectCache();
        var calls = 0;

        // Act
        var first = await cache.GetOrCreateAsync("key", () => Task.FromResult(new object[] { ++calls }));
        var second = await cache.GetOrCreateAsync("key", () => Task.FromResult(new object[] { ++calls }));

        // Assert
        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Given_FailedCreation_When_GettingItAgain_Then_TheFactoryRunsAgain()
    {
        // Arrange
        var cache = new SharedObjectCache();
        await Assert.ThrowsAsync<TimeoutException>(
            () => cache.GetOrCreateAsync<string>("key", () => throw new TimeoutException()));

        // Act
        var value = await cache.GetOrCreateAsync("key", () => Task.FromResult("created"));

        // Assert
        Assert.Equal("created", value);
    }

    [Fact]
    public async Task Given_DisposableObjects_When_DisposingAll_Then_EachIsDisposedAndTheCacheIsEmpty()
    {
        // Arrange
        var cache = new SharedObjectCache();
        var disposable = new Mock<IDisposable>();
        var asyncDisposable = new Mock<IAsyncDisposable>();
        asyncDisposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
        await cache.GetOrCreateAsync("sync", () => Task.FromResult(disposable.Object));
        await cache.GetOrCreateAsync("async", () => Task.FromResult(asyncDisposable.Object));

        // Act
        cache.DisposeAll();

        // Assert
        disposable.Verify(d => d.Dispose(), Times.Once);
        asyncDisposable.Verify(d => d.DisposeAsync(), Times.Once);
        Assert.Equal("new", await cache.GetOrCreateAsync("sync", () => Task.FromResult("new")));
    }
}