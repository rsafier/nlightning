using System.Diagnostics;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

/// <summary>
/// The run lifecycle against a real cluster: the reaper, the quota sized from a topology and the cap on concurrent
/// runs. Explicit, like <see cref="ClusterSmokeTests"/>; every namespace is a <c>nltg-spike-*</c> one the test deletes.
/// </summary>
[Trait("Category", "Cluster")]
public class RunLifecycleTests
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(3);

    private static TestRunOptions Options(string suite, string runId) =>
        TestRunOptions.FromEnvironment(suite) with { RunId = runId, Log = Log };

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static NodeWorkload Busybox(string name) =>
        new(name, NodeKind.Other, ImageVersions.Busybox)
        {
            Command = ["sh", "-c", "trap 'exit 0' TERM; while true; do sleep 1; done"],
            Resources = WorkloadResources.Tiny,
            TerminationGracePeriodSeconds = 2
        };

    /// <summary>The owner record of a process that has exited (the run of a test process that crashed).</summary>
    private static async Task<RunOwner> DeadOwnerAsync(CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 0.3\"")
        {
            UseShellExecute = false
        })!;
        var owner = new RunOwner(Environment.MachineName, process.Id, RunOwner.TryGetStartUnixMs(process));
        await process.WaitForExitAsync(cancellationToken);
        return owner;
    }

    [Fact(Explicit = true)]
    public async Task Given_AnOrphanedRunAndALiveRun_When_TheReaperRuns_Then_OnlyTheOrphanIsDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var batch = "reap-" + TestRunId.Generate();
        var deadOwner = await DeadOwnerAsync(ct);
        // The live run first: while a run waits for a slot its admission reaps orphans every 30 s, which removed the
        // orphan before the reaper under test saw it whenever the cap was full (seen in the full Category=Cluster run)
        await using var live = await TestRun.StartAsync(Options("reaper-live", $"{batch}-live"), ct);
        var orphan = await TestRun.StartAsync(Options("reaper-orphan", $"{batch}-orphan") with { Owner = deadOwner },
                                              ct);
        using var client = KubeClientFactory.Create();
        var options = new ReaperOptions { RunFilter = batch, WaitForDeletion = true, Log = Log };

        try
        {
            // Act
            var listed = await RunReaper.ListAsync(client, options, ct);
            var watch = Stopwatch.StartNew();
            var outcomes = await RunReaper.ReapAsync(client, options, ct);
            var reapTime = watch.Elapsed;

            // Assert
            Assert.Equal(2, listed.Count);
            var orphanCandidate = Assert.Single(listed, c => c.Namespace == orphan.Namespace);
            Assert.Equal(ReapVerdict.OwnerGone, orphanCandidate.Verdict);
            Assert.Equal(deadOwner, orphanCandidate.Owner);
            var liveCandidate = Assert.Single(listed, c => c.Namespace == live.Namespace);
            Assert.Equal(ReapVerdict.Keep, liveCandidate.Verdict);
            Assert.Equal(Environment.ProcessId, liveCandidate.Owner!.Pid);

            var outcome = Assert.Single(outcomes);
            Assert.True(outcome.Deleted);
            Assert.Equal(orphan.Namespace, outcome.Candidate.Namespace);
            Assert.Null(await RunNamespace.TryReadAsync(client, orphan.Namespace, ct));
            Assert.NotNull(await RunNamespace.TryReadAsync(client, live.Namespace, ct));
            Log(ClusterCli.FormatTable(listed, DateTimeOffset.UtcNow, null));
            Log($"{orphan.Namespace} (owner {deadOwner}, exited) reaped in {reapTime.TotalSeconds:F1} s; "
              + $"{live.Namespace} kept");
        }
        finally
        {
            await orphan.DisposeAsync();
        }
    }

    [Fact(Explicit = true)]
    public async Task Given_AQuotaSizedFromTheTopology_When_ItIsDeployed_Then_ItFitsAndAPodMoreIsRefused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = Busybox("probe");
        var quota = QuotaSizing.ForWorkloads([node]);
        await using var run = await TestRun.StartAsync(
                                  Options("quota", "quota-" + TestRunId.Generate()) with { Quota = quota }, ct);

        // Act
        await run.DeployAsync(node, s_readyTimeout, ct);
        var extra = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "extra", Labels = new Dictionary<string, string>(run.Identity.Labels) },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "extra",
                        Image = ImageVersions.Busybox.Reference,
                        Command = ["sleep", "60"],
                        Resources = WorkloadResources.Tiny.ToKubernetes()
                    }
                ]
            }
        };
        var refused = await Assert.ThrowsAsync<HttpOperationException>(
                          () => run.Client.CoreV1.CreateNamespacedPodAsync(extra, run.Namespace, cancellationToken: ct));
        var used = await run.Client.CoreV1.ReadNamespacedResourceQuotaAsync(NamespaceQuota.ObjectName, run.Namespace,
                                                                            cancellationToken: ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, refused.Response.StatusCode);
        Assert.Contains("exceeded quota", refused.Response.Content);
        Assert.Equal("1", used.Status.Used["pods"].ToString());
        Assert.Equal(quota.RequestsCpu, used.Spec.Hard["requests.cpu"].ToString());
        var message = refused.Response.Content;
        Log($"{run.Namespace}: quota {quota}; a pod more was refused: {message[..Math.Min(message.Length, 400)]}");
    }

    [Fact(Explicit = true)]
    public async Task Given_ACapOfOneRun_When_TwoRunsStart_Then_TheSecondWaitsUntilTheFirstIsGone()
    {
        // Arrange: a private prefix, so the cap counts this test's runs only
        var ct = TestContext.Current.CancellationToken;
        var id = TestRunId.Generate();
        TestRunOptions CapOptions(string suffix) =>
            Options("cap", $"{id}-{suffix}") with { NamespacePrefix = "nltg-spike-cap", MaxConcurrentRuns = 1 };

        var first = await TestRun.StartAsync(CapOptions("a"), ct);
        var firstNamespace = first.Namespace;
        Task<TestRun>? secondTask = null;
        try
        {
            // Act
            var watch = Stopwatch.StartNew();
            secondTask = TestRun.StartAsync(CapOptions("b"), ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            var waitedWhileFull = !secondTask.IsCompleted;
            await first.DisposeAsync();
            var firstGoneAfter = watch.Elapsed;
            var second = await secondTask.WaitAsync(TimeSpan.FromMinutes(2), ct);
            var secondAdmittedAfter = watch.Elapsed;

            // Assert
            Assert.True(waitedWhileFull);
            using var client = KubeClientFactory.Create();
            Assert.Null(await RunNamespace.TryReadAsync(client, firstNamespace, ct));
            Assert.StartsWith("nltg-spike-cap-", second.Namespace);
            Assert.True(secondAdmittedAfter >= firstGoneAfter);
            Log($"{second.Namespace} waited {secondAdmittedAfter.TotalSeconds:F1} s for {firstNamespace} "
              + $"(gone after {firstGoneAfter.TotalSeconds:F1} s)");
        }
        finally
        {
            await first.DisposeAsync();
            if (secondTask is not null)
                await (await secondTask.WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None)).DisposeAsync();
        }
    }
}