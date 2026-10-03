namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class TestRunDisposalTests
{
    private static TestRunOptions Options(string id) =>
        new() { RunId = id, Suite = "disposal", MaxConcurrentRuns = null };

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_TheRunDeletesInTheBackgroundWithAShortPodGrace()
    {
        // Arrange
        var options = new TestRunOptions();

        // Act & Assert
        Assert.False(options.WaitForDeletion);
        Assert.Equal(1, options.TeardownGracePeriodSeconds);
    }

    [Fact]
    public async Task Given_ARun_When_Disposed_Then_ItIssuesTheDeletionShortensThePodGraceAndDoesNotWait()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var api = new FakeKubeApi();
        var run = await TestRun.StartAsync(api.CreateClient(), Options("bg1"), ct);
        var ns = run.Namespace;

        // Act
        await run.DisposeAsync();

        // Assert: the ownership read; the StatefulSets deleted without their pods, the pods with a 1 s grace and
        // listed until finished; then the namespace; no wait for it to go
        var requests = api.Requests.ToList();
        Assert.Equal(1, requests.Count(r => r == $"GET /api/v1/namespaces/{ns}"));
        var statefulSets = requests.FindIndex(r => r.StartsWith($"DELETE /apis/apps/v1/namespaces/{ns}/statefulsets",
                                                                StringComparison.Ordinal)
                                                && r.Contains("propagationPolicy=Orphan", StringComparison.Ordinal));
        var pods = requests.FindIndex(r => r.StartsWith($"DELETE /api/v1/namespaces/{ns}/pods", StringComparison.Ordinal)
                                        && r.Contains("gracePeriodSeconds=1", StringComparison.Ordinal));
        var listed = requests.FindIndex(r => r.StartsWith($"GET /api/v1/namespaces/{ns}/pods", StringComparison.Ordinal));
        var deleted = requests.FindIndex(r => r.StartsWith($"DELETE /api/v1/namespaces/{ns}?", StringComparison.Ordinal)
                                           || r == $"DELETE /api/v1/namespaces/{ns}");
        Assert.True(statefulSets >= 0 && statefulSets < pods && pods < listed && listed < deleted,
                    string.Join(" | ", requests));
        Assert.True(api.Exists(ns));
    }

    [Fact]
    public async Task Given_WaitForDeletion_When_Disposed_Then_ItReadsUntilTheNamespaceIsGone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var api = new FakeKubeApi { ReadsWhileTerminating = 2 };
        var run = await TestRun.StartAsync(api.CreateClient(), Options("bg2") with { WaitForDeletion = true }, ct);
        var ns = run.Namespace;

        // Act
        await run.DisposeAsync();

        // Assert: the ownership read, then reads until it is gone
        Assert.False(api.Exists(ns));
        Assert.True(api.Requests.Count(r => r == $"GET /api/v1/namespaces/{ns}") >= 3);
    }

    [Fact]
    public async Task Given_NoTeardownGrace_When_Disposed_Then_ThePodsKeepTheirOwnGrace()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var api = new FakeKubeApi();
        var run = await TestRun.StartAsync(api.CreateClient(),
                                           Options("bg3") with { TeardownGracePeriodSeconds = null }, ct);
        var ns = run.Namespace;

        // Act
        await run.DisposeAsync();

        // Assert: the namespace deleted at once, the pods left to it
        Assert.DoesNotContain(api.Requests, r => r.StartsWith($"DELETE /api/v1/namespaces/{ns}/pods",
                                                              StringComparison.Ordinal));
        Assert.DoesNotContain(api.Requests, r => r.Contains("/statefulsets", StringComparison.Ordinal));
    }
}