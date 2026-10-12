using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Runner;

namespace NLightning.Integration.Tests.Cluster.Live;

[Trait("Category", "Cluster")]
public sealed class NativeWalletAuthorityProcessInClusterRunnerTests
{
    [Fact(Explicit = true)]
    public async Task IntegrationRunnerProvesProductionWalletAuthorityWithVerifiedTlsPostgresAndRealCore()
    {
        var ct = TestContext.Current.CancellationToken;
        void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-wallet-authority-runner")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(20)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_NATIVE_AUTHORITY_CA_PEM"] = await NativeWalletAuthorityTlsPostgres.CreateTlsSecretAsync(run, ct);
        foreach (var arg in new[] { "-explicit", "only", "-class",
            "NLightning.Integration.Tests.Cluster.Live.NativeWalletAuthorityProcessClusterTests", "-showLiveOutput", "-noColor" })
            job.TestArguments.Add(arg);
        var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 2, Errors: 0, Failed: 0, Skipped: 0", result.Log);
    }
}