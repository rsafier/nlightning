using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using k8s;

namespace NLightning.Testing.Cluster.Tests.Live.Faults;

using Cluster.Faults;
using Cluster.Kube;
using Cluster.Run;
using static FaultClusterTestSupport;

/// <summary>
/// The fault injector against a real cluster with busybox nodes (plan R11): restart, kill and crash keep the PVC data
/// and the DNS name, pause stops the node in its pod, and NetworkPolicy partitions cut new connections. Explicit and
/// <c>Category=Cluster</c>: the normal test run never needs a cluster. Each test owns one <c>nltg-spike-&lt;id&gt;</c>
/// namespace and deletes it.
/// </summary>
[Trait("Category", "Cluster")]
public class FaultInjectorClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_ANodeWithData_When_RestartedKilledAndCrashed_Then_ItKeepsItsDataAndItsDnsNameFollowsThePod()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("faults-lifecycle"), ct);
        await using var faults = run.CreateFaultInjector(Log);
        var keeperDeploy = run.DeployAsync(BusyboxNode("keeper").WithProcessFaults(), ReadyTimeout, ct);
        var watcher = await run.DeployAsync(BusyboxNode("watcher"), ReadyTimeout, ct);
        var keeper = await keeperDeploy;
        byte[] marker = [0x00, 0x01, 0x7f, 0x80, 0xfe, 0xff, (byte)'\n', 0x00];
        await keeper.WriteFileAsync("/data/sub/marker.bin", marker, ct);

        // Act / Assert: graceful restart
        var restart = await faults.RestartAsync(keeper, ReadyTimeout, ct);
        Assert.Equal(marker, await keeper.ReadFileAsync("/data/sub/marker.bin", ct));
        Assert.True(restart.NewPod);
        var restartDns = await EventuallyAsync(
                             async () => (await LookupAsync(watcher, keeper.PodDnsName, ct)).Contains(restart.PodIpAfter!),
                             TimeSpan.FromSeconds(60), "the pod DNS name resolving to the new pod", ct);

        // Act / Assert: kill (1 s grace)
        var kill = await faults.KillAsync(keeper, ReadyTimeout, ct);
        Assert.Equal(marker, await keeper.ReadFileAsync("/data/sub/marker.bin", ct));
        Assert.True(kill.NewPod);
        Assert.Equal(0, kill.RestartCountAfter);
        var killDns = await EventuallyAsync(
                          async () => (await LookupAsync(watcher, keeper.ServiceDnsName, ct)).Contains(kill.PodIpAfter!),
                          TimeSpan.FromSeconds(60), "the service DNS name resolving to the new pod", ct);

        // Act / Assert: crash (SIGKILL in place)
        var crash = await faults.CrashAsync(keeper, ReadyTimeout, ct);
        Assert.Equal(marker, await keeper.ReadFileAsync("/data/sub/marker.bin", ct));
        Assert.False(crash.NewPod);
        Assert.Equal(crash.PodIpBefore, crash.PodIpAfter);
        Assert.Equal(crash.RestartCountBefore + 1, crash.RestartCountAfter);
        Assert.Equal("keeper-0", await ShAsync(keeper, "hostname", ct));

        // Assert: the node saw SIGTERM on the restart and on the kill, each before its replacement started (the kill's
        // 1 s grace keeps the old pod until its container is gone: never two processes on the PVC), but nothing on the
        // crash
        var eventLines = (await ShAsync(keeper, "cat /data/events", ct)).Split('\n');
        Log($"{run.Namespace}: /data/events: {string.Join(" | ", eventLines)}");
        var events = eventLines.Select(l => l.Split(' ')[0]).ToList();
        Assert.Equal(6, events.Count);
        Assert.Equal(["START", "TERM", "START"], events[..3]);
        Assert.Equal(["TERM", "START"], events[3..5]);
        Assert.Equal("START", events[5]);
        Assert.Equal([FaultKind.Restart, FaultKind.Kill, FaultKind.Crash], faults.Events.Select(e => e.Kind));
        Log($"{run.Namespace}: restart {restart.Duration.TotalSeconds:F1} s (DNS +{restartDns.TotalSeconds:F1} s), "
          + $"kill {kill.Duration.TotalSeconds:F1} s (DNS +{killDns.TotalSeconds:F1} s), "
          + $"crash {crash.Duration.TotalSeconds:F1} s; IPs {restart.PodIpBefore} -> {restart.PodIpAfter} -> "
          + $"{kill.PodIpAfter} -> {crash.PodIpAfter}");
    }

    [Fact(Explicit = true)]
    public async Task Given_ANodeDeployedWithProcessFaults_When_PausedAndResumed_Then_ItStopsAndContinuesInTheSamePod()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("faults-pause"), ct);
        await using var faults = run.CreateFaultInjector(Log);
        var pinnedDeploy = run.DeployAsync(BusyboxNode("pinned"), ReadyTimeout, ct);
        var sleeper = await run.DeployAsync(BusyboxNode("sleeper").WithProcessFaults(), ReadyTimeout, ct);
        var pinned = await pinnedDeploy;
        var uid = sleeper.PodUid;
        var running = await faults.ListProcessesAsync(sleeper, ct);
        Assert.False(running.MainProcessIsPid1);
        Assert.DoesNotContain(running.Processes, p => p.IsStopped);

        // Act
        var paused = await faults.PauseAsync(sleeper, ct);
        var first = await CounterAsync(sleeper, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var second = await CounterAsync(sleeper, ct);
        var whilePaused = await faults.ListProcessesAsync(sleeper, ct);
        await faults.ResumeAsync(sleeper, ct);
        var resumedAfter = await EventuallyAsync(async () => await CounterAsync(sleeper, ct) > second,
                                                 TimeSpan.FromSeconds(10), "the counter moving again", ct);

        // Assert
        Assert.Equal(first, second);
        Assert.All(paused.StoppedPids, pid => Assert.True(whilePaused.Find(pid) is null or { IsStopped: true }
                                                                                        or { HasExited: true }));
        var pod = await run.Client.TryReadPodAsync(run.Namespace, sleeper.PodName, ct);
        Assert.Equal(uid, pod!.Metadata.Uid);
        Assert.Equal(0, pod.Status.ContainerStatuses[0].RestartCount);
        Assert.DoesNotContain((await faults.ListProcessesAsync(sleeper, ct)).Processes, p => p.IsStopped);

        // Assert: a node whose process is PID 1 is refused, and keeps running
        var pinnedList = await faults.ListProcessesAsync(pinned, ct);
        Assert.True(pinnedList.MainProcessIsPid1);
        await Assert.ThrowsAsync<FaultNotSupportedException>(() => faults.PauseAsync(pinned, ct));
        await Assert.ThrowsAsync<FaultNotSupportedException>(() => faults.CrashAsync(pinned, ReadyTimeout, ct));
        var pinnedBefore = await CounterAsync(pinned, ct);
        await EventuallyAsync(async () => await CounterAsync(pinned, ct) > pinnedBefore, TimeSpan.FromSeconds(5),
                              "the PID 1 node still counting", ct);
        Assert.DoesNotContain((await faults.ListProcessesAsync(pinned, ct)).Processes, p => p.IsStopped);
        Log($"{run.Namespace}: paused pids {string.Join(',', paused.StoppedPids)} in "
          + $"{paused.Duration.TotalSeconds:F1} s, counter held at {second} for 2 s, moving again "
          + $"{resumedAfter.TotalSeconds:F1} s after the resume; processes while paused: "
          + string.Join("; ", whilePaused.Processes.Select(p => $"{p.Pid} {p.State} {p.Name}")));
    }

    [Fact(Explicit = true)]
    public async Task Given_ThreeNodes_When_PartitionedAndHealed_Then_NewConnectionsFailUntilTheHeal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("faults-partition"), ct);
        await using var faults = run.CreateFaultInjector(Log);
        var deploys = new[] { "a", "b", "c" }.Select(n => run.DeployAsync(BusyboxNode(n), ReadyTimeout, ct)).ToList();
        var nodes = await Task.WhenAll(deploys);
        var (a, b, c) = (nodes[0], nodes[1], nodes[2]);
        Assert.True(await CanConnectAsync(c, "a", "baseline", ct));

        // An established connection from c to a, streaming a line every 0.2 s
        await ShAsync(c, $"nohup sh -c 'i=0; while true; do i=$((i + 1)); echo s$i; sleep 0.2; done | nc a {StreamPort}' "
                       + ">/dev/null 2>&1 &", ct);
        await EventuallyAsync(async () => await StreamLinesAsync(a, ct) >= 3, TimeSpan.FromSeconds(20),
                              "the stream flowing", ct);

        // Act: split {a, b} | {c}
        var split = await faults.PartitionAsync([a, b], [c], null, ct);
        var checkWatch = Stopwatch.StartNew();
        var aToB = await CanConnectAsync(a, "b", "a-to-b", ct);
        var aToC = await CanConnectAsync(a, "c", "a-to-c", ct);
        var cToA = await CanConnectAsync(c, "a", "c-to-a", ct);
        var bToC = await CanConnectAsync(b, "c", "b-to-c", ct);
        var cToB = await CanConnectAsync(c, "b", "c-to-b", ct);
        var checks = checkWatch.Elapsed;
        var aResolvesC = await LookupAsync(a, "c", ct);
        var streamBefore = await StreamLinesAsync(a, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var streamAfter = await StreamLinesAsync(a, ct);
        await faults.HealAsync(split, ct, TimeSpan.Zero);
        var healedAfter = await EventuallyAsync(() => CanConnectAsync(c, "a", "healed", ct), TimeSpan.FromSeconds(30),
                                                "c reaching a after the heal", ct);

        // Assert
        Assert.True(aToB, "a and b are on the same side");
        Assert.False(aToC, "a -> c crosses the partition");
        Assert.False(cToA, "c -> a crosses the partition");
        Assert.False(bToC, "b -> c crosses the partition");
        Assert.False(cToB, "c -> b crosses the partition");
        Assert.Contains(c.PodIp!, aResolvesC);
        // Documented OrbStack behavior: the established connection survives the policy (conntrack)
        Assert.True(streamAfter > streamBefore, $"established stream: {streamBefore} -> {streamAfter} lines");

        // Act: isolate a from every pod
        var isolation = await faults.IsolateAsync(a, null, ct);
        var bToA = await CanConnectAsync(b, "a", "b-to-a", ct);
        var aToBIsolated = await CanConnectAsync(a, "b", "a-to-b-isolated", ct);
        var aResolvesB = await LookupAsync(a, "b", ct);
        await faults.HealAsync(isolation, ct);
        var bToAHealed = await CanConnectAsync(b, "a", "b-to-a-healed", ct);

        // Assert
        Assert.False(bToA);
        Assert.False(aToBIsolated);
        Assert.Contains(b.PodIp!, aResolvesB);
        Assert.True(bToAHealed);
        Assert.Empty(faults.Partitions);
        var policies = await run.Client.NetworkingV1.ListNamespacedNetworkPolicyAsync(run.Namespace,
                           cancellationToken: ct);
        Assert.Empty(policies.Items);
        Assert.Equal([FaultKind.Partition, FaultKind.Heal, FaultKind.Partition, FaultKind.Heal],
                     faults.Events.Select(e => e.Kind));
        Log($"{run.Namespace}: 5 cross checks in {checks.TotalSeconds:F1} s, established stream {streamBefore} -> "
          + $"{streamAfter} lines in 2 s while split, c reached a {healedAfter.TotalSeconds:F1} s after the heal; "
          + $"received by a: {(await ShAsync(a, "tr '\\n' ' ' < /data/recv", ct))}");
    }

    [Fact(Explicit = true)]
    public async Task Given_TheRunnersAddress_When_ANodeIsIsolatedWithItAllowed_Then_TheRunnerStillReachesIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(Options("faults-runner"), ct);
        await using var faults = run.CreateFaultInjector(Log);
        var node = await run.DeployAsync(BusyboxNode("target"), ReadyTimeout, ct);
        string runnerIp;
        TcpClient? first = null;
        for (var attempt = 0; attempt < 10 && first is null; attempt++)
        {
            first = await TryConnectAsync(node.PodIp!, ct);
            if (first is null)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        using (var held = first)
        {
            if (held is null)
                Assert.Skip($"this test process cannot reach pod IPs ({node.PodIp}) directly");

            // The runner's address as the pod sees it (OrbStack: the pod network's first address). busybox nc closes
            // its side at once (its stdin is empty), so the connection may already be past ESTABLISHED
            var netstat = await ShAsync(node, "netstat -tn", ct);
            runnerIp = netstat.Split('\n')
                              .Where(l => l.StartsWith("tcp", StringComparison.Ordinal)
                                       && l.Contains($":{ProbePort} ", StringComparison.Ordinal))
                              .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[4])
                              .Select(remote => remote[..remote.LastIndexOf(':')].Replace("::ffff:", "",
                                                                                       StringComparison.Ordinal))
                              .First();
        }

        // Act
        var blocked = await faults.IsolateAsync(node, null, ct);
        using var refused = await TryConnectAsync(node.PodIp!, ct);
        await faults.HealAsync(blocked, ct);
        var allowed = await faults.IsolateAsync(
                          node, new PartitionOptions { AllowedIngressCidrs = [$"{runnerIp}/32"] }, ct);
        using var reached = await TryConnectAsync(node.PodIp!, ct);
        await faults.HealAsync(allowed, ct);

        // Assert
        Assert.Null(refused);
        Assert.NotNull(reached);
        Log($"{run.Namespace}: runner address {runnerIp}; isolated: refused, with {runnerIp}/32 allowed: reached");
    }

    private static async Task<int> StreamLinesAsync(Cluster.Nodes.INodeHandle node, CancellationToken ct) =>
        int.Parse(await ShAsync(node, "touch /data/stream; wc -l < /data/stream", ct), CultureInfo.InvariantCulture);

    private static async Task<TcpClient?> TryConnectAsync(string ip, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(ip, ProbePort, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return client;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Log($"connect {ip}:{ProbePort}: {e.GetType().Name} {e.Message}");
            client.Dispose();
            return null;
        }
    }
}