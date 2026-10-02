using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Runner;

using Cluster.Images;
using Cluster.Run;
using Cluster.Runner;

public class RunnerManifestTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "runner" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_ARun_When_TheRunnerRbacIsBuilt_Then_EveryObjectIsNamespacedToTheRun()
    {
        // Act
        var rbac = RunnerRbac.Build(s_run);

        // Assert
        Assert.All(new V1ObjectMeta[] { rbac.ServiceAccount.Metadata, rbac.Role.Metadata, rbac.RoleBinding.Metadata },
                   m =>
                   {
                       Assert.Equal(RunnerRbac.Name, m.Name);
                       Assert.Equal("nltg-spike-r1", m.NamespaceProperty);
                       Assert.Equal("r1", m.Labels[RunLabels.Run]);
                   });
        Assert.Equal("Role", rbac.RoleBinding.RoleRef.Kind);
        var subject = Assert.Single(rbac.RoleBinding.Subjects);
        Assert.Equal("ServiceAccount", subject.Kind);
        Assert.Equal("nltg-spike-r1", subject.NamespaceProperty);
        Assert.Equal("system:serviceaccount:nltg-spike-r1:nltg-test-runner", RunnerRbac.UserName("nltg-spike-r1"));
    }

    [Fact]
    public void Given_TheRunnerRole_When_ItsRulesAreRead_Then_ItCanDriveNodesButOnlyReadItsNamespace()
    {
        // Act
        var rules = RunnerRbac.Build(s_run).Role.Rules;

        // Assert
        bool Allows(string group, string resource, string verb) =>
            rules.Any(r => r.ApiGroups.Contains(group) && r.Resources.Contains(resource) && r.Verbs.Contains(verb));

        Assert.True(Allows("apps", "statefulsets", "create"));
        Assert.True(Allows("", "pods/exec", "create"));
        Assert.True(Allows("", "pods/log", "get"));
        Assert.True(Allows("", "persistentvolumeclaims", "delete"));
        Assert.True(Allows("", "namespaces", "get"));
        Assert.False(Allows("", "namespaces", "create"));
        Assert.False(Allows("", "namespaces", "delete"));
        Assert.False(Allows("", "namespaces", "list"));
        Assert.False(Allows("", "resourcequotas", "delete"));
        Assert.False(Allows("rbac.authorization.k8s.io", "roles", "create"));
        Assert.DoesNotContain(rules, r => r.Verbs.Contains("*") || r.Resources.Contains("*"));
    }

    [Fact]
    public void Given_ARunnerJob_When_Built_Then_ItRunsTheAssemblyOnceAsTheRunnerInTheRunNamespace()
    {
        // Arrange
        var job = new TestRunnerJob { ActiveDeadline = TimeSpan.FromMinutes(5) };
        job.TestArguments.Add("-explicit");
        job.TestArguments.Add("only");
        job.Env["EXTRA"] = "x";
        job.Env[TestRunId.EnvironmentVariable] = "not-this-one";

        // Act
        var manifest = job.Build(s_run);

        // Assert
        Assert.Equal("runner", manifest.Metadata.Name);
        Assert.Equal("nltg-spike-r1", manifest.Metadata.NamespaceProperty);
        Assert.Equal(TestRunnerJob.RoleValue, manifest.Metadata.Labels[TestRunnerJob.RoleLabel]);
        Assert.Equal("r1", manifest.Metadata.Labels[RunLabels.Run]);
        Assert.Equal(0, manifest.Spec.BackoffLimit);
        Assert.Equal(300, manifest.Spec.ActiveDeadlineSeconds);
        var pod = manifest.Spec.Template.Spec;
        Assert.Equal("Never", pod.RestartPolicy);
        Assert.Equal(RunnerRbac.Name, pod.ServiceAccountName);
        Assert.True(pod.AutomountServiceAccountToken);
        Assert.False(pod.EnableServiceLinks);
        var container = Assert.Single(pod.Containers);
        Assert.Equal("runner", container.Name);
        Assert.Equal("nltg-spike-runner:latest", container.Image);
        Assert.Equal("Never", container.ImagePullPolicy);
        Assert.Equal(["dotnet", "/runner/NLightning.Testing.Cluster.Tests.dll"], container.Command);
        Assert.Equal(["-explicit", "only"], container.Args);
        var env = container.Env.ToDictionary(e => e.Name, e => e.Value);
        Assert.Equal("r1", env[TestRunId.EnvironmentVariable]);
        Assert.Equal("nltg-spike", env[TestRunOptions.NamespacePrefixVariable]);
        Assert.Equal("1", env[TestRunOptions.AdoptNamespaceVariable]);
        Assert.Equal("x", env["EXTRA"]);
        Assert.Equal(container.Env.Select(e => e.Name).Order(StringComparer.Ordinal), container.Env.Select(e => e.Name));
    }

    [Fact]
    public void Given_TheDefaultRunnerResources_When_Built_Then_TheRequestsStayWithinTheSpikeLimits()
    {
        // Act
        var resources = new TestRunnerJob().Build(s_run).Spec.Template.Spec.Containers[0].Resources;

        // Assert
        Assert.Equal("500m", resources.Requests["cpu"].ToString());
        Assert.Equal("512Mi", resources.Requests["memory"].ToString());
        Assert.NotNull(resources.Limits["cpu"]);
    }

    [Fact]
    public void Given_ARunnerJob_When_TheJobEnvironmentIsReadByTheTests_Then_TheyAdoptTheSameNamespace()
    {
        // Arrange
        var env = new TestRunnerJob().Build(s_run).Spec.Template.Spec.Containers[0].Env
                                     .ToDictionary(e => e.Name, e => e.Value);

        // Act
        var options = TestRunOptions.FromEnvironment("inside", k => env.GetValueOrDefault(k));
        var identity = RunIdentity.Create(options, DateTimeOffset.UnixEpoch);

        // Assert
        Assert.True(options.AdoptNamespace);
        Assert.Equal(s_run.Namespace, identity.Namespace);
        Assert.Equal(s_run.Id, identity.Id);
    }

    [Theory]
    [InlineData(null, "nltg-spike-runner", "latest", null, ImagePullPolicy.Never)]
    [InlineData("registry.local:5000/nltg-spike-runner:abc", "registry.local:5000/nltg-spike-runner", "abc", null,
                ImagePullPolicy.IfNotPresent)]
    [InlineData("registry.local:5000/runner", "registry.local:5000/runner", "latest", null,
                ImagePullPolicy.IfNotPresent)]
    [InlineData("runner:v1@sha256:00", "runner", "v1", "sha256:00", ImagePullPolicy.IfNotPresent)]
    public void Given_TheImageVariable_When_TheRunnerImageIsRead_Then_ItIsParsed(
        string? value, string repository, string tag, string? digest, ImagePullPolicy policy)
    {
        // Act
        var image = RunnerImage.FromEnvironment(k => k == RunnerImage.ImageVariable ? value : null);

        // Assert
        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Equal(digest, image.Digest);
        Assert.Equal(policy, image.PullPolicy);
    }

    [Fact]
    public void Given_AJobPod_When_ItsExitCodeIsRead_Then_OnlyATerminatedRunnerHasOne()
    {
        // Arrange
        V1Pod Pod(V1ContainerState state) => new()
        {
            Status = new V1PodStatus
            {
                ContainerStatuses = [new V1ContainerStatus { Name = "runner", State = state }]
            }
        };

        // Act / Assert
        Assert.Equal(3, InClusterTestRunner.GetExitCode(
                            Pod(new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 3 } }),
                            "runner"));
        Assert.Null(InClusterTestRunner.GetExitCode(
                        Pod(new V1ContainerState { Running = new V1ContainerStateRunning() }), "runner"));
        Assert.Null(InClusterTestRunner.GetExitCode(new V1Pod(), "runner"));
        Assert.Equal("batch.kubernetes.io/job-name=runner", InClusterTestRunner.PodSelector("runner"));
    }

    [Fact]
    public void Given_AnInvalidJobName_When_Created_Then_ItThrows() =>
        Assert.ThrowsAny<ArgumentException>(() => new TestRunnerJob("Not_A_Name"));
}