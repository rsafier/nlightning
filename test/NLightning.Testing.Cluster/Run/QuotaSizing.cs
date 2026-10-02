using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Run;

using Kube;

/// <summary>
/// Sizes a run's <see cref="NamespaceQuota"/> from its topology (plan R4): the sum of what its pods request and are
/// limited to, so the quota admits exactly the topology (plus the stated extra pods) and nothing that leaks into it.
/// A pod's share is the larger of its containers' sum and its largest init container, as the scheduler counts it.
/// </summary>
public static class QuotaSizing
{
    private const decimal Mebibyte = 1024m * 1024m;

    // The workloads are built against a throwaway identity: only their pod specs are read.
    private static readonly RunIdentity s_sizingIdentity =
        RunIdentity.Create(new TestRunOptions { RunId = "sizing", Suite = "sizing" }, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The quota of a run that deploys <paramref name="workloads"/> plus <paramref name="extraPods"/> pods of
    /// <paramref name="extraPodResources"/> each (helper pods, an in-cluster test Job; default
    /// <see cref="WorkloadResources.Tiny"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A container declares no CPU or memory request or limit: the quota would
    /// refuse its pod.</exception>
    public static NamespaceQuota ForWorkloads(IEnumerable<NodeWorkload> workloads, int extraPods = 0,
                                              WorkloadResources? extraPodResources = null)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentOutOfRangeException.ThrowIfNegative(extraPods);

        var total = new PodShare(0, 0, 0, 0);
        var pods = 0;
        var claims = 0;
        foreach (var workload in workloads)
        {
            var statefulSet = workload.Build(s_sizingIdentity).StatefulSet;
            var replicas = statefulSet.Spec.Replicas ?? 1;
            var share = ForPod(statefulSet.Spec.Template.Spec, workload.Name);
            total += share * replicas;
            pods += replicas;
            claims += (statefulSet.Spec.VolumeClaimTemplates?.Count ?? 0) * replicas;
        }

        if (extraPods > 0)
        {
            var extra = extraPodResources ?? WorkloadResources.Tiny;
            total += new PodShare(Amount(extra.CpuRequest), Amount(extra.MemoryRequest), Amount(extra.CpuLimit),
                                  Amount(extra.MemoryLimit)) * extraPods;
            pods += extraPods;
        }

        return new NamespaceQuota(FormatCpu(total.RequestsCpu), FormatMemory(total.RequestsMemory),
                                  FormatCpu(total.LimitsCpu), FormatMemory(total.LimitsMemory), pods, claims);
    }

    /// <summary>
    /// The share of one pod: per resource, the larger of the containers' sum and the largest init container.
    /// </summary>
    internal static PodShare ForPod(V1PodSpec spec, string owner)
    {
        var main = new PodShare(0, 0, 0, 0);
        foreach (var container in spec.Containers)
            main += Of(container, owner);

        var init = new PodShare(0, 0, 0, 0);
        foreach (var container in spec.InitContainers ?? [])
            init = PodShare.Max(init, Of(container, owner));

        return PodShare.Max(main, init);
    }

    private static PodShare Of(V1Container container, string owner)
    {
        var requests = container.Resources?.Requests;
        var limits = container.Resources?.Limits;
        if (requests is null || limits is null
         || !requests.TryGetValue("cpu", out var requestCpu) || !requests.TryGetValue("memory", out var requestMemory)
         || !limits.TryGetValue("cpu", out var limitCpu) || !limits.TryGetValue("memory", out var limitMemory))
            throw new ArgumentException(
                $"Container {container.Name} of {owner} declares no CPU/memory requests and limits; a run quota "
              + "refuses such a pod");

        return new PodShare(requestCpu.ToDecimal(), requestMemory.ToDecimal(), limitCpu.ToDecimal(),
                            limitMemory.ToDecimal());
    }

    private static decimal Amount(string quantity) => new ResourceQuantity(quantity).ToDecimal();

    /// <summary>CPUs as whole millicores, rounded up (<c>1250m</c>).</summary>
    internal static string FormatCpu(decimal cpus) =>
        decimal.Ceiling(cpus * 1000m).ToString(CultureInfo.InvariantCulture) + "m";

    /// <summary>Bytes as whole mebibytes, rounded up (<c>1536Mi</c>).</summary>
    internal static string FormatMemory(decimal bytes) =>
        decimal.Ceiling(bytes / Mebibyte).ToString(CultureInfo.InvariantCulture) + "Mi";

    /// <summary>CPU (cores) and memory (bytes) requests and limits.</summary>
    internal readonly record struct PodShare(decimal RequestsCpu, decimal RequestsMemory, decimal LimitsCpu,
                                             decimal LimitsMemory)
    {
        public static PodShare operator +(PodShare a, PodShare b) =>
            new(a.RequestsCpu + b.RequestsCpu, a.RequestsMemory + b.RequestsMemory, a.LimitsCpu + b.LimitsCpu,
                a.LimitsMemory + b.LimitsMemory);

        public static PodShare operator *(PodShare a, int n) =>
            new(a.RequestsCpu * n, a.RequestsMemory * n, a.LimitsCpu * n, a.LimitsMemory * n);

        public static PodShare Max(PodShare a, PodShare b) =>
            new(Math.Max(a.RequestsCpu, b.RequestsCpu), Math.Max(a.RequestsMemory, b.RequestsMemory),
                Math.Max(a.LimitsCpu, b.LimitsCpu), Math.Max(a.LimitsMemory, b.LimitsMemory));
    }
}