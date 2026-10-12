using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Runner;

using Images;
using Kube;
using Run;

/// <summary>
/// The Job that runs the test assembly inside a run's namespace (plan R5; PR #10's phase 4, made per run): one pod,
/// no retries, the runner's ServiceAccount (<see cref="RunnerRbac"/>), and the environment that makes the tests adopt
/// the run's namespace instead of creating their own (<see cref="TestRunOptions.AdoptNamespace"/>).
/// </summary>
public sealed class TestRunnerJob
{
    /// <summary>The label that marks the runner's Job and pod (value <see cref="RoleValue"/>).</summary>
    public const string RoleLabel = "nltg.role";

    public const string RoleValue = "runner";

    /// <summary>The test assembly the image runs (an xunit v3 executable assembly).</summary>
    public const string DefaultAssembly = "NLightning.Testing.Cluster.Tests.dll";

    public TestRunnerJob(string name = "runner")
    {
        Name = KubeNames.RequireDns1123Label(name, "job name", KubeNames.MaxWorkloadNameLength);
    }

    /// <summary>The Job's (and container's) name.</summary>
    public string Name { get; }

    public ImageRef Image { get; set; } = RunnerImage.Local;

    /// <summary>The assembly under <see cref="RunnerImage.AssemblyDirectory"/>.</summary>
    public string Assembly { get; set; } = DefaultAssembly;

    /// <summary>The xunit v3 runner arguments, e.g. <c>-explicit only -method ...</c>.</summary>
    public IList<string> TestArguments { get; } = new List<string>();

    /// <summary>Extra environment (the run variables are always set and win).</summary>
    public IDictionary<string, string> Env { get; } = new Dictionary<string, string>();

    /// <summary>The runner's resources: requests within the spike's 1 CPU / 1 GiB per pod.</summary>
    public WorkloadResources Resources { get; set; } = new("500m", "512Mi", "2", "2Gi");

    /// <summary>Kubernetes kills the Job after this long (<c>activeDeadlineSeconds</c>).</summary>
    public TimeSpan ActiveDeadline { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The Job in <paramref name="run"/>'s namespace.</summary>
    public V1Job Build(RunIdentity run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var labels = new Dictionary<string, string>(run.Labels) { [RoleLabel] = RoleValue };
        var env = new Dictionary<string, string>(Env)
        {
            [TestRunId.EnvironmentVariable] = run.Id,
            [TestRunOptions.NamespacePrefixVariable] = run.NamespacePrefix,
            [TestRunOptions.AdoptNamespaceVariable] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };

        var container = new V1Container
        {
            Name = Name,
            Image = Image.Reference,
            ImagePullPolicy = Image.PullPolicyValue,
            WorkingDir = RunnerImage.AssemblyDirectory,
            Command = ["dotnet", $"{RunnerImage.AssemblyDirectory}/{Assembly}"],
            Args = TestArguments.Count == 0 ? null : [.. TestArguments],
            Env = env.OrderBy(e => e.Key, StringComparer.Ordinal)
                     .Select(e => new V1EnvVar { Name = e.Key, Value = e.Value }).ToList(),
            Resources = Resources.ToKubernetes()
        };

        return new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta
            {
                Name = Name,
                NamespaceProperty = run.Namespace,
                Labels = new Dictionary<string, string>(labels)
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                Completions = 1,
                Parallelism = 1,
                ActiveDeadlineSeconds = (long)ActiveDeadline.TotalSeconds,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>(labels),
                        Annotations = new Dictionary<string, string>
                        {
                            ["nltg.runner/started"] =
                                run.StartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                        }
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = [container],
                        RestartPolicy = "Never",
                        ServiceAccountName = RunnerRbac.Name,
                        AutomountServiceAccountToken = true,
                        EnableServiceLinks = false,
                        TerminationGracePeriodSeconds = 10
                    }
                }
            }
        };
    }
}