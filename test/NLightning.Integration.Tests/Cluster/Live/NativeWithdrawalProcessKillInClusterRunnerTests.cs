using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Runner;

namespace NLightning.Integration.Tests.Cluster.Live;

[Trait("Category", "Cluster")]
public sealed class NativeWithdrawalProcessKillInClusterRunnerTests
{
    [Fact(Explicit = true)]
    public async Task IntegrationRunnerExecutesEveryWithdrawalKillpointAndReceiptRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-withdrawal-kill-runner")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(25)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        foreach (var argument in new[]
        {
            "-explicit", "only", "-method", "*KilledWithdrawalReplaysOriginalIntentAndCoreAcceptsIdenticalBytes",
            "-method", "*KilledWithdrawalRetainsReservationWhenRecoveryCannotProveItsReceipt", "-showLiveOutput", "-noColor"
        }) job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 7, Errors: 0, Failed: 0, Skipped: 0", result.Log);
    }
}