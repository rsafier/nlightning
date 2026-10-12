using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Runner;

namespace NLightning.Integration.Tests.Cluster.Live;

[Trait("Category", "Cluster")]
public sealed class NativePsbtPublicationProcessKillInClusterRunnerTests
{
    [Fact(Explicit = true)]
    public async Task IntegrationRunnerExecutesEveryPsbtPublicationKillpointAndReceiptRejection()
    {
        var ct = TestContext.Current.CancellationToken;
        void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-psbt-kill-runner")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(35)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        foreach (var argument in new[]
        {
            "-explicit", "only", "-method", "*KilledPsbtPublicationReplaysOriginalIntentAndCoreAcceptsIdenticalBytes",
            "-method", "*KilledPsbtPublicationRetainsReservationWhenRecoveryCannotProveItsReceipt", "-showLiveOutput", "-noColor"
        }) job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 12, Errors: 0, Failed: 0, Skipped: 0", result.Log);
    }
}