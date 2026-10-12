namespace NLightning.Integration.Tests.Cluster.Live;

using Testing.Cluster.Run;
using Testing.Cluster.Runner;

/// <summary>Real payment accounting and PostgreSQL recovery proofs using the current build in a fresh runner.</summary>
[Trait("Category", "Cluster")]
public sealed class AccountingReviewClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_RealPaymentsAndPostgres_When_AccountingIsReviewed_Then_BooksBalanceAndRecoveryIsAtomic()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("accounting-review"), ct);
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
                     "-explicit", "off", "-class", "NLightning.Integration.Tests.Docker.Abcd.AbcdAccountingTests",
                     "-class", "NLightning.Integration.Tests.Docker.PostgresTests",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("financial balance sheet", result.Log);
    }
}