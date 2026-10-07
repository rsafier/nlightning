namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Run;
using Testing.Cluster.Runner;

/// <summary>
/// Runs the real subscription and on-chain restart proofs inside the cluster.
/// Explicit opt-in: regular OrbStack hosts can still run the underlying Wave3 class directly. Set
/// NLTG_RUNNER_IMAGE to a fresh image containing the current Integration.Tests build before running this wrapper.
/// </summary>
[Trait("Category", "Cluster")]
public sealed class WavesOneAndTwoClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_RealNodeOperations_When_ObservedAcrossRestartAndReorg_Then_SubscriptionsAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("fixes-waves1and2"), ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(30)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_CLUSTER_DIAG"] = "failure";
        if (Environment.GetEnvironmentVariable(TestRunOptions.KeepNamespaceVariable) is { } keep)
            job.Env[TestRunOptions.KeepNamespaceVariable] = keep;
        foreach (var argument in new[]
                 {
                     "-explicit", "off", "-class", "NLightning.Integration.Tests.Docker.LndGrpcWave3FlowTests",
                     "-class", "NLightning.Integration.Tests.Docker.Onchain.OnchainFinalHopTests",
                     "-class", "NLightning.Integration.Tests.Docker.PostgresTests",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("all five real streams observed", result.Log);
        Assert.Contains("WAVES_ONCHAIN_RESTART_OK", result.Log);
        Assert.Contains("WAVES_IMPORTED_REORG_OK", result.Log);
    }
}