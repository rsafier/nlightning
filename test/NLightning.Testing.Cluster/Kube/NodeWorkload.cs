using k8s.Models;

namespace NLightning.Testing.Cluster.Kube;

using Images;
using Nodes;
using Run;

/// <summary>
/// One node of a topology as Kubernetes objects (plan §4 "R2 stable aliases"): a StatefulSet with one replica, a
/// headless Service of the same name and, with <see cref="Data"/>, a PVC from the claim template. The alias
/// (<see cref="Name"/>) resolves inside the run's namespace, the pod is always <c>&lt;name&gt;-0</c> and keeps its DNS
/// name and data across restarts, so configs never contain the run id.
/// </summary>
/// <remarks>
/// Differences from LNUnit PR #10's bare pods: data on a PVC instead of <c>emptyDir</c> (a restart keeps it), a
/// StatefulSet that recreates the pod under the same name, declared resources, a readiness probe, service links off
/// (no <c>ALICE_PORT</c>-style variables leaking into the nodes' environment), no service-account token, and the
/// Service publishes not-ready addresses so nodes can reach each other while they start.
/// </remarks>
public sealed class NodeWorkload
{
    public NodeWorkload(string name, NodeKind kind, ImageRef image)
    {
        Name = KubeNames.RequireDns1123Label(name, "node name", KubeNames.MaxWorkloadNameLength);
        Kind = kind;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <summary>The node's alias: StatefulSet, Service and container name.</summary>
    public string Name { get; }

    public NodeKind Kind { get; }

    public ImageRef Image { get; set; }

    /// <summary>The container's entrypoint override, or null for the image's.</summary>
    public IList<string>? Command { get; set; }

    /// <summary>The container's arguments.</summary>
    public IList<string> Args { get; } = new List<string>();

    /// <summary>Environment variables.</summary>
    public IDictionary<string, string> Env { get; } = new Dictionary<string, string>();

    /// <summary>Extra labels on the StatefulSet, Service and pod (the run and node labels are always set).</summary>
    public IDictionary<string, string> Labels { get; } = new Dictionary<string, string>();

    /// <summary>Annotations on the pod.</summary>
    public IDictionary<string, string> Annotations { get; } = new Dictionary<string, string>();

    /// <summary>Container ports, also published on the Service.</summary>
    public IList<WorkloadPort> Ports { get; } = new List<WorkloadPort>();

    public WorkloadResources Resources { get; set; } = WorkloadResources.Default;

    /// <summary>The persistent data volume, or null for none.</summary>
    public DataVolume? Data { get; set; }

    /// <summary><c>emptyDir</c> volumes (name to mount path) for data that need not survive a restart.</summary>
    public IDictionary<string, string> ScratchVolumes { get; } = new Dictionary<string, string>();

    /// <summary>The readiness probe (see <see cref="Probes"/>), or null to be ready once the container runs.</summary>
    public V1Probe? ReadinessProbe { get; set; }

    /// <summary>How long a graceful restart waits for the process to stop.</summary>
    public int TerminationGracePeriodSeconds { get; set; } = 10;

    /// <summary>The pod's security context, or null for the image's user.</summary>
    public V1PodSecurityContext? SecurityContext { get; set; }

    /// <summary>
    /// A last hook on the built pod spec (init containers, sidecars, ConfigMap volumes) for what the builder does not
    /// cover. Runs after everything else.
    /// </summary>
    public Action<V1PodSpec>? CustomizePod { get; set; }

    /// <summary>The pod name of the single replica.</summary>
    public string PodName => $"{Name}-0";

    /// <summary>The StatefulSet and Service of this node in <paramref name="run"/>'s namespace.</summary>
    public NodeWorkloadManifests Build(RunIdentity run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var selector = RunLabels.NodeSelector(run.Id, Name);
        var labels = RunLabels.ForNode(run.Labels, Name, Kind);
        foreach (var (key, value) in Labels)
            labels.TryAdd(key, value);

        var container = new V1Container
        {
            Name = Name,
            Image = Image.Reference,
            ImagePullPolicy = Image.PullPolicyValue,
            Command = Command is null ? null : [.. Command],
            Args = Args.Count == 0 ? null : [.. Args],
            Env = Env.Count == 0
                      ? null
                      : Env.OrderBy(e => e.Key, StringComparer.Ordinal)
                           .Select(e => new V1EnvVar { Name = e.Key, Value = e.Value }).ToList(),
            Ports = Ports.Count == 0
                        ? null
                        : Ports.Select(p => new V1ContainerPort
                        {
                            Name = p.Name,
                            ContainerPort = p.Port,
                            Protocol = p.Protocol
                        }).ToList(),
            Resources = Resources.ToKubernetes(),
            ReadinessProbe = ReadinessProbe,
            VolumeMounts = BuildMounts()
        };

        var podSpec = new V1PodSpec
        {
            Containers = [container],
            Volumes = ScratchVolumes.Count == 0
                          ? null
                          : ScratchVolumes.Keys.Select(v => new V1Volume
                          {
                              Name = v,
                              EmptyDir = new V1EmptyDirVolumeSource()
                          }).ToList(),
            RestartPolicy = "Always",
            TerminationGracePeriodSeconds = TerminationGracePeriodSeconds,
            EnableServiceLinks = false,
            AutomountServiceAccountToken = false,
            SecurityContext = SecurityContext
        };
        CustomizePod?.Invoke(podSpec);

        var statefulSet = new V1StatefulSet
        {
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
            Metadata = new V1ObjectMeta
            {
                Name = Name,
                NamespaceProperty = run.Namespace,
                Labels = new Dictionary<string, string>(labels)
            },
            Spec = new V1StatefulSetSpec
            {
                Replicas = 1,
                ServiceName = Name,
                PodManagementPolicy = "Parallel",
                Selector = new V1LabelSelector { MatchLabels = selector },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>(labels),
                        Annotations = Annotations.Count == 0 ? null : new Dictionary<string, string>(Annotations)
                    },
                    Spec = podSpec
                },
                VolumeClaimTemplates = Data is null ? null : [BuildClaimTemplate(Data, labels)],
                PersistentVolumeClaimRetentionPolicy = Data is null
                                                           ? null
                                                           : new V1StatefulSetPersistentVolumeClaimRetentionPolicy
                                                           {
                                                               WhenDeleted = "Delete",
                                                               WhenScaled = "Retain"
                                                           }
            }
        };

        var service = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = Name,
                NamespaceProperty = run.Namespace,
                Labels = new Dictionary<string, string>(labels)
            },
            Spec = new V1ServiceSpec
            {
                ClusterIP = "None",
                Selector = new Dictionary<string, string>(selector),
                PublishNotReadyAddresses = true,
                Ports = Ports.Count == 0
                            ? null
                            : Ports.Select(p => new V1ServicePort
                            {
                                Name = p.Name,
                                Port = p.Port,
                                TargetPort = p.Port,
                                Protocol = p.Protocol
                            }).ToList()
            }
        };

        return new NodeWorkloadManifests(statefulSet, service);
    }

    private List<V1VolumeMount>? BuildMounts()
    {
        var mounts = new List<V1VolumeMount>();
        if (Data is not null)
            mounts.Add(new V1VolumeMount { Name = DataVolume.VolumeName, MountPath = Data.MountPath });
        mounts.AddRange(ScratchVolumes.Select(v => new V1VolumeMount { Name = v.Key, MountPath = v.Value }));
        return mounts.Count == 0 ? null : mounts;
    }

    private static V1PersistentVolumeClaim BuildClaimTemplate(DataVolume data,
                                                              IReadOnlyDictionary<string, string> labels) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = DataVolume.VolumeName,
                Labels = new Dictionary<string, string>(labels)
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                StorageClassName = data.StorageClassName,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(data.Size) }
                }
            }
        };
}