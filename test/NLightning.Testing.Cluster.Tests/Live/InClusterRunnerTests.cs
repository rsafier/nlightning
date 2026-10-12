using System.Diagnostics;
using k8s;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Run;
using Cluster.Runner;

/// <summary>
/// The in-cluster fallback (plan R5): the host creates the run's namespace, and the test assembly runs inside it as a
/// Job under a namespaced ServiceAccount. Needs the runner image: <c>test/NLightning.Testing.Cluster/Runner/image/build.sh</c>.
/// </summary>
[Trait("Category", "Cluster")]
public class InClusterRunnerTests
{
    private const string SmokeTest =
        "NLightning.Testing.Cluster.Tests.Live.ClusterSmokeTests."
      + nameof(ClusterSmokeTests.Given_ACluster_When_ARunDeploysABusyboxStatefulSet_Then_ItIsReadyAndItsNamespaceIsDeleted);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    [Fact(Explicit = true)]
    public async Task Given_TheRunnerImage_When_TheSmokeTestRunsAsAJob_Then_ItPassesInsideTheRunNamespace()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var run = await TestRun.StartAsync(Options("runner"), ct);
        var job = new TestRunnerJob { Image = RunnerImage.FromEnvironment(), ActiveDeadline = TimeSpan.FromMinutes(10) };
        foreach (var argument in new[] { "-explicit", "only", "-method", SmokeTest, "-showLiveOutput", "-noColor" })
            job.TestArguments.Add(argument);

        // Act
        var result = await InClusterTestRunner.RunAsync(run, job, line => Log($"  | {line}"), ct);

        // Assert
        Log($"{run.Namespace}: runner {result.PodName} running after {result.StartedAfter.TotalSeconds:F1} s, "
          + $"exit {result.ExitCode} after {result.Elapsed.TotalSeconds:F1} s ({watch.Elapsed.TotalSeconds:F1} s "
          + "since the run started)");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"namespace {run.Namespace} adopted (InCluster)", result.Log);
        Assert.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0", result.Log);
        Assert.Contains("node deployed, ready and removed", result.Log);
        var sets = await run.Client.AppsV1.ListNamespacedStatefulSetAsync(run.Namespace, cancellationToken: ct);
        Assert.Empty(sets.Items);
        var namespaceObject = await RunNamespace.TryReadAsync(run.Client, run.Namespace, ct);
        Assert.NotNull(namespaceObject);
    }

    [Fact(Explicit = true)]
    public async Task Given_AnInvalidRunnerArgument_When_TheJobRuns_Then_ItsNonZeroExitCodeComesBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("runner-fail"), ct);
        var job = new TestRunnerJob("runner-fail") { Image = RunnerImage.FromEnvironment() };
        job.TestArguments.Add("-no-such-option");

        // Act
        var result = await InClusterTestRunner.RunAsync(run, job, line => Log($"  | {line}"), ct);

        // Assert
        Log($"{run.Namespace}: exit {result.ExitCode} after {result.Elapsed.TotalSeconds:F1} s");
        Assert.NotEqual(0, result.ExitCode);
        var jobObject = await run.Client.BatchV1.ReadNamespacedJobAsync(job.Name, run.Namespace,
                                                                        cancellationToken: ct);
        await WaitForAsync(async () => (await run.Client.BatchV1.ReadNamespacedJobAsync(
                                            job.Name, run.Namespace, cancellationToken: ct)).Status?.Failed == 1, ct);
        Assert.Equal(0, jobObject.Spec.BackoffLimit);
    }

    [Fact(Explicit = true)]
    public async Task Given_TheRunnerRbac_When_ItsAccessIsReviewed_Then_ItIsConfinedToTheRunNamespace()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("runner-rbac"), ct);
        await RunnerRbac.ApplyAsync(run.Client, RunnerRbac.Build(run.Identity), ct);
        var ns = run.Namespace;

        // Act: SubjectAccessReviews are queries, nothing is stored
        async Task<bool> Allowed(string? inNamespace, string group, string resource, string verb,
                                 string? subresource = null, string? name = null)
        {
            var review = await run.Client.AuthorizationV1.CreateSubjectAccessReviewAsync(
                             new V1SubjectAccessReview
                             {
                                 ApiVersion = "authorization.k8s.io/v1",
                                 Kind = "SubjectAccessReview",
                                 Spec = new V1SubjectAccessReviewSpec
                                 {
                                     User = RunnerRbac.UserName(ns),
                                     Groups = ["system:serviceaccounts", $"system:serviceaccounts:{ns}"],
                                     ResourceAttributes = new V1ResourceAttributes
                                     {
                                         NamespaceProperty = inNamespace,
                                         Group = group,
                                         Resource = resource,
                                         Verb = verb,
                                         Subresource = subresource,
                                         Name = name
                                     }
                                 }
                             }, cancellationToken: ct);
            Log($"{verb} {group}/{resource}{(subresource is null ? "" : "/" + subresource)} "
              + $"{(name is null ? "" : name + " ")}in {inNamespace ?? "<cluster>"}: {review.Status.Allowed}");
            return review.Status.Allowed;
        }

        // Assert
        Assert.True(await Allowed(ns, "apps", "statefulsets", "create"));
        Assert.True(await Allowed(ns, "", "pods", "create", "exec"));
        Assert.True(await Allowed(ns, "", "namespaces", "get", name: ns));
        Assert.False(await Allowed(null, "", "namespaces", "create"));
        Assert.False(await Allowed(null, "", "namespaces", "list"));
        Assert.False(await Allowed(ns, "", "namespaces", "delete", name: ns));
        Assert.False(await Allowed("default", "", "namespaces", "get", name: "default"));
        Assert.False(await Allowed("default", "apps", "statefulsets", "create"));
        Assert.False(await Allowed("kube-system", "", "pods", "list"));
        Assert.False(await Allowed(ns, "rbac.authorization.k8s.io", "roles", "create"));
        Assert.False(await Allowed(ns, "", "resourcequotas", "delete"));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        for (var i = 0; i < 60 && !await condition(); i++)
            await Task.Delay(500, ct);
    }
}