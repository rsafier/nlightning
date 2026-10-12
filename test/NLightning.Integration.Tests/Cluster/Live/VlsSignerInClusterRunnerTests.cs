namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Run;

/// <summary>Runs VLS signer interoperability through the namespaced in-cluster fallback.</summary>
[Trait("Category", "Cluster")]
public class VlsSignerInClusterRunnerTests
{
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    /// <summary>Runs the real peer proof inside Kubernetes when the host cannot route to pod addresses.</summary>
    [Fact(Explicit = true)]
    public async Task Given_TheIntegrationRunnerImage_When_VlsSignerInteropRunsInCluster_Then_TheProofPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("vls-signer-runner"), ct);
        var job = new Testing.Cluster.Runner.TestRunnerJob
        {
            Image = Testing.Cluster.Runner.RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(20)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_VLS_GATEWAY_BINARY"] = "/runner/nlightning-vls-gateway";
        foreach (var argument in new[]
                 {
                     "-explicit", "only", "-class",
                     "NLightning.Integration.Tests.Cluster.Live.VlsSignerLndClusterTests",
                     "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await Testing.Cluster.Runner.InClusterTestRunner.RunAsync(run, job, Log, ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 3, Errors: 0, Failed: 0, Skipped: 0", result.Log);
    }

}