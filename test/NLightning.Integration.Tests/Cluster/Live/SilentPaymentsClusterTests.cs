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
                     "-class", "NLightning.Integration.Tests.Docker.SilentPaymentsFlowTests",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SOURCE Rest", result.Log);
        Assert.Contains("SP independent interoperability", result.Log);
    }

    [Fact(Explicit = true)]
    public async Task Given_PostgresDatabase_When_SilentPaymentsMigrateAndSpend_Then_InClusterRecoveryMetadataRoundTrips()
    {
        // Arrange: the runner reaches the database pod even on hosts without a route to the pod network.
        const string testClass = "NLightning.Integration.Tests.Docker.PostgresTests";
        const string testMethod = "Given_PostgresWallet_When_SilentPaymentsMigrateAndSpend_Then_RecoveryMetadataRoundTrips";
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("silentpayments-postgres"), ct);
        var job = new TestRunnerJob
        {
            Image = RunnerImage.FromEnvironment(),
            Assembly = "NLightning.Integration.Tests.dll",
            ActiveDeadline = TimeSpan.FromMinutes(10)
        };
        job.Env["NLTG_TEST_BACKEND"] = "cluster";
        job.Env["NLTG_CLUSTER_DIAG"] = "failure";
        if (Environment.GetEnvironmentVariable(TestRunOptions.KeepNamespaceVariable) is { } keep)
            job.Env[TestRunOptions.KeepNamespaceVariable] = keep;
        foreach (var argument in new[]
                 {
                     "-explicit", "on", "-class", testClass, "-method", $"*{testMethod}",
                     "-parallel", "none", "-showLiveOutput", "-noColor"
                 })
            job.TestArguments.Add(argument);

        // Act: run the existing seeded-upgrade, receipt/spend/reorg persistence scenario on PostgreSQL.
        var result = await InClusterTestRunner.RunAsync(run, job,
            line => TestContext.Current.TestOutputHelper?.WriteLine(line), ct);

        // Assert: exit zero alone also permits an empty selection or a dynamically skipped fixture.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0", result.Log);
        TestContext.Current.TestOutputHelper?.WriteLine($"SP PostgreSQL runtime PASS: {testClass}.{testMethod}");
    }
}