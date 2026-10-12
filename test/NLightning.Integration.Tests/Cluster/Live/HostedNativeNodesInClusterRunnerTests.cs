using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Runner;

namespace NLightning.Integration.Tests.Cluster.Live;

[Trait("Category", "Cluster")]
public sealed class HostedNativeNodesInClusterRunnerTests
{
    [Fact(Explicit = true)]
    public async Task Given_TheIntegrationRunnerImage_When_TwoNativeHostedNodesRun_Then_TheLiveProofExecutes()
    {
        var ct = TestContext.Current.CancellationToken;
        void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("hosted-native-runner")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(20)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        foreach (var argument in new[]
                 {
                     "-explicit", "only", "-class",
                     "NLightning.Integration.Tests.Cluster.Live.HostedNativeNodesLndClusterTests",
                     "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0", result.Log);
    }
}