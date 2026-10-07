namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Run;
using Testing.Cluster.Runner;

/// <summary>Silent payments use the current build in a fresh runner; launch only with scripts/run-cluster.sh.</summary>
[Trait("Category", "Cluster")]
public sealed class SilentPaymentsClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_CoreAndIndependentWallet_When_SilentPaymentsAreExercised_Then_ReceiveSpendAndRecoveryAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("silentpayments"), ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(35)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_CLUSTER_DIAG"] = "failure";
        if (Environment.GetEnvironmentVariable(TestRunOptions.KeepNamespaceVariable) is { } keep)
            job.Env[TestRunOptions.KeepNamespaceVariable] = keep;
        foreach (var argument in new[]
                 {
                     "-explicit", "on", "-class", "NLightning.Integration.Tests.Cluster.Live.SilentPaymentPrevoutClusterTests",
                     "-class", "NLightning.Integration.Tests.Cluster.Live.SilentPaymentRbitcoinPrevoutClusterTests",
                     "-class", "NLightning.Integration.Tests.Docker.SilentPaymentsFlowTests",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SOURCE Rest", result.Log);
        Assert.Contains("SOURCE rbitcoin Auto", result.Log);
        Assert.Contains("SP independent interoperability", result.Log);
    }
}