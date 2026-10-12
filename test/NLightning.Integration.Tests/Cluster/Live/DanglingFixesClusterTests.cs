namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Run;
using Testing.Cluster.Runner;

/// <summary>
/// Runs one combined real Core/LND proof inside the cluster, where pod addresses and Service DNS are reachable.
/// Explicit opt-in: regular OrbStack hosts can still run the underlying Wave3 class directly. Set
/// NLTG_RUNNER_IMAGE to a fresh image containing the current Integration.Tests build before running this wrapper.
/// </summary>
[Trait("Category", "Cluster")]
public sealed class DanglingFixesClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_RealCoreAndLnd_When_DanglingFixesRun_Then_TheCombinedProofPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("dangling-fixes"), ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(20)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_CLUSTER_DIAG"] = "failure";
        if (Environment.GetEnvironmentVariable(TestRunOptions.KeepNamespaceVariable) is { } keep)
            job.Env[TestRunOptions.KeepNamespaceVariable] = keep;
        foreach (var argument in new[]
                 {
                     "-explicit", "off", "-class", "NLightning.Integration.Tests.Docker.LndGrpcWave3FlowTests",
                     "-class", "NLightning.Integration.Tests.Docker.PostgresTests",
                     "-method", "NLightning.Integration.Tests.Docker.LndGrpcWave3FlowTests.Given_RealCoreAndLnd_When_DanglingFixesAreUsed_Then_WireAndRecoveryProofsAgree",
                     "-method", "NLightning.Integration.Tests.Docker.PostgresTests.Given_FreshOrLegacyPostgres_When_DanglingSchemaMigrates_Then_CompiledModelRestartsKeepEveryNewFact",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("dangling PostgreSQL schema upgrade: compiled model restart and legacy defaults proved", result.Log);
        Assert.Contains("dangling PostgreSQL schema fresh: compiled model restart and legacy defaults proved", result.Log);
        Assert.Contains("all five real streams observed", result.Log);
        Assert.Contains("dangling fixes: modified forwarding, exact silent-payment input, bounded history restart proved", result.Log);
        Assert.Contains("WalletKit: isolated named account, existing-change template, unconfirmed signing, two-ancestor CPFP replacement and durable label proved", result.Log);
        Assert.Contains("real Core pruned history refusal", result.Log);
        Assert.Contains("real upstream onchain interceptor: existing outgoing claim persisted, downstream fulfilled, one forwarding fact", result.Log);
    }
}