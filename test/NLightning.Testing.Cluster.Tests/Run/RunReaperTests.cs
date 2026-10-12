using System.Globalization;
using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunReaperTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private sealed class FakeProbe(OwnerProcessState state) : IProcessProbe
    {
        public List<int> Probed { get; } = [];

        public OwnerProcessState Probe(int pid, long? startUnixMs)
        {
            Probed.Add(pid);
            return state;
        }
    }

    private static ReaperOptions Options(OwnerProcessState state = OwnerProcessState.Alive) =>
        new() { LocalHost = "mac", ProcessProbe = new FakeProbe(state), Now = s_now };

    internal static V1Namespace Namespace(string run, TimeSpan age, RunOwner? owner = null, bool spike = true,
                                          string prefix = "nltg-spike", string? name = null,
                                          bool managed = true, Dictionary<string, string>? annotations = null)
    {
        var labels = new Dictionary<string, string>
        {
            [RunLabels.Run] = run,
            [RunLabels.Suite] = "smoke",
            [RunLabels.Started] = (s_now - age).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
        if (managed)
            labels[RunLabels.ManagedBy] = RunLabels.ManagedByValue;
        if (spike)
            labels[RunLabels.Spike] = "true";

        var allAnnotations = owner?.ToAnnotations() ?? new Dictionary<string, string>();
        foreach (var (key, value) in annotations ?? [])
            allAnnotations[key] = value;

        return new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = name ?? $"{prefix}-{run}",
                Labels = labels,
                Annotations = allAnnotations,
                CreationTimestamp = (s_now - age).UtcDateTime
            }
        };
    }

    [Fact]
    public void Given_ARunWhoseOwnerIsAlive_When_Evaluated_Then_ItIsKept()
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.FromMinutes(5), new RunOwner("mac", 42, 1000));

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(), s_now);

        // Assert
        Assert.NotNull(candidate);
        Assert.Equal(ReapVerdict.Keep, candidate.Verdict);
        Assert.False(candidate.ShouldReap);
        Assert.Equal("r1", candidate.RunId);
        Assert.Equal(TimeSpan.FromMinutes(5), candidate.Age);
        Assert.Equal("smoke", candidate.Suite);
    }

    [Theory]
    [InlineData(OwnerProcessState.Gone, "is gone")]
    [InlineData(OwnerProcessState.PidReused, "pid reused")]
    public void Given_ARunWhoseOwnerIsGone_When_Evaluated_Then_ItIsReaped(OwnerProcessState state, string reason)
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.FromMinutes(5), new RunOwner("MAC", 42, 1000));

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(state), s_now);

        // Assert
        Assert.NotNull(candidate);
        Assert.Equal(ReapVerdict.OwnerGone, candidate.Verdict);
        Assert.True(candidate.ShouldReap);
        Assert.Contains(reason, candidate.Reason);
    }

    [Fact]
    public void Given_AnOwnerOnAnotherHost_When_Evaluated_Then_TheProcessIsNotProbedAndTheRunIsKept()
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.FromMinutes(5), new RunOwner("ci-runner-7", 42, 1000));
        var probe = new FakeProbe(OwnerProcessState.Gone);

        // Act
        var candidate = RunReaper.Evaluate(ns, Options() with { ProcessProbe = probe }, s_now);

        // Assert
        Assert.Equal(ReapVerdict.Keep, candidate!.Verdict);
        Assert.Empty(probe.Probed);
        Assert.Contains("another host", candidate.Reason);
    }

    [Fact]
    public void Given_ARunOlderThanTheTtl_When_Evaluated_Then_ItIsReapedWhateverItsOwner()
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.FromHours(7), new RunOwner("mac", 42, 1000));

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(), s_now);

        // Assert
        Assert.Equal(ReapVerdict.Expired, candidate!.Verdict);
    }

    [Fact]
    public void Given_ARunWithItsOwnTtl_When_Evaluated_Then_ThatTtlApplies()
    {
        // Arrange
        var annotations = new Dictionary<string, string> { [RunAnnotations.TtlSeconds] = "60" };
        var ns = Namespace("r1", TimeSpan.FromMinutes(2), annotations: annotations);

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(), s_now);

        // Assert
        Assert.Equal(ReapVerdict.Expired, candidate!.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(60), candidate.Ttl);
    }

    [Fact]
    public void Given_ARunWithoutAnOwner_When_ItIsYoung_Then_ItIsKept()
    {
        // Act
        var candidate = RunReaper.Evaluate(Namespace("r1", TimeSpan.FromMinutes(1)), Options(OwnerProcessState.Gone),
                                           s_now);

        // Assert
        Assert.Equal(ReapVerdict.Keep, candidate!.Verdict);
        Assert.Null(candidate.Owner);
    }

    [Fact]
    public void Given_AKeptRunWhoseOwnerIsGone_When_Evaluated_Then_ItIsKeptUntilItsTtl()
    {
        // Arrange
        var annotations = new Dictionary<string, string> { [RunAnnotations.Keep] = "true" };
        var young = Namespace("r1", TimeSpan.FromMinutes(1), new RunOwner("mac", 42, 1000), annotations: annotations);
        var old = Namespace("r2", TimeSpan.FromHours(8), new RunOwner("mac", 42, 1000), annotations: annotations);

        // Act
        var youngCandidate = RunReaper.Evaluate(young, Options(OwnerProcessState.Gone), s_now);
        var oldCandidate = RunReaper.Evaluate(old, Options(OwnerProcessState.Gone), s_now);

        // Assert
        Assert.Equal(ReapVerdict.Keep, youngCandidate!.Verdict);
        Assert.True(youngCandidate.Kept);
        Assert.Equal(ReapVerdict.Expired, oldCandidate!.Verdict);
    }

    [Fact]
    public void Given_ATerminatingNamespace_When_Evaluated_Then_ItIsNotReapedAgain()
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.FromHours(8));
        ns.Metadata.DeletionTimestamp = s_now.UtcDateTime;

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(), s_now);

        // Assert
        Assert.Equal(ReapVerdict.Terminating, candidate!.Verdict);
        Assert.False(candidate.ShouldReap);
    }

    [Fact]
    public void Given_ARunFilter_When_Evaluated_Then_OnlyThatRunAndItsDerivedIdsAreSeen()
    {
        // Arrange
        var options = Options() with { RunFilter = "batch-1" };

        // Act & Assert
        Assert.NotNull(RunReaper.Evaluate(Namespace("batch-1", TimeSpan.Zero), options, s_now));
        Assert.NotNull(RunReaper.Evaluate(Namespace("batch-1-2", TimeSpan.Zero), options, s_now));
        Assert.Null(RunReaper.Evaluate(Namespace("batch-10", TimeSpan.Zero), options, s_now));
        Assert.Null(RunReaper.Evaluate(Namespace("other", TimeSpan.Zero), options, s_now));
    }

    [Fact]
    public void Given_AForcedRunFilter_When_TheOwnerIsAlive_Then_TheRunIsReaped()
    {
        // Arrange
        var ns = Namespace("batch-1", TimeSpan.Zero, new RunOwner("mac", 42, 1000));

        // Act
        var candidate = RunReaper.Evaluate(ns, Options() with { RunFilter = "batch-1", Force = true }, s_now);

        // Assert
        Assert.Equal(ReapVerdict.Forced, candidate!.Verdict);
        Assert.True(candidate.ShouldReap);
    }

    [Theory]
    [InlineData("default", "r1", true, true)]
    [InlineData("kube-system", "r1", true, true)]
    [InlineData("nltg-spike-r2", "r1", true, true)]
    [InlineData("nltg-other-r1", "r1", true, true)]
    [InlineData("nltg-spike-r1", "r1", false, true)]
    [InlineData("nltg-spike-r1", "r1", true, false)]
    public void Given_ANamespaceThatIsNotAHarnessRun_When_Evaluated_Then_TheReaperIgnoresIt(string name, string run,
        bool managed, bool spike)
    {
        // Arrange
        var ns = Namespace(run, TimeSpan.FromDays(30), new RunOwner("mac", 42, 1000), spike, name: name,
                           managed: managed);

        // Act
        var candidate = RunReaper.Evaluate(ns, Options(OwnerProcessState.Gone), s_now);

        // Assert
        Assert.Null(candidate);
    }

    [Fact]
    public void Given_AllRunsAndANonSpikeRun_When_EvaluatedWithoutTheSpikeRequirement_Then_ItIsSeen()
    {
        // Arrange
        var ns = Namespace("r1", TimeSpan.Zero, spike: false);

        // Act
        var candidate = RunReaper.Evaluate(ns, Options() with { RequireSpikeLabel = false }, s_now);

        // Assert
        Assert.NotNull(candidate);
    }

    [Fact]
    public void Given_TheOptions_When_TheSelectorIsBuilt_Then_ItSelectsHarnessRuns()
    {
        // Act & Assert
        Assert.Equal("app.kubernetes.io/managed-by=nltg-test-harness,nltg.run,nltg.spike=true",
                     RunReaper.Selector(new ReaperOptions()));
        Assert.Equal("app.kubernetes.io/managed-by=nltg-test-harness,nltg.run",
                     RunReaper.Selector(new ReaperOptions { RequireSpikeLabel = false }));
    }

    [Theory]
    [InlineData("kube", null, false)]
    [InlineData("default", null, false)]
    [InlineData("Nltg", null, false)]
    [InlineData("nltg", null, true)]
    [InlineData("nltg-spike", " ", false)]
    public void Given_ReaperOptions_When_Validated_Then_OnlyNltgPrefixesPass(string prefix, string? filter, bool valid)
    {
        // Arrange
        var options = new ReaperOptions { Prefix = prefix, RunFilter = filter };

        // Act
        var exception = Record.Exception(options.Validate);

        // Assert
        Assert.Equal(valid, exception is null);
    }

    [Fact]
    public void Given_ForceWithoutARunFilter_When_Validated_Then_ItIsRefused()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(new ReaperOptions { Force = true }.Validate);
    }

    [Theory]
    [InlineData(10, "10s")]
    [InlineData(150, "2m")]
    [InlineData(3 * 3600 + 300, "3h05m")]
    [InlineData(2 * 86400 + 4 * 3600, "2d04h")]
    [InlineData(-5, "0s")]
    public void Given_ASpan_When_Formatted_Then_ItIsShort(int seconds, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, RunReaper.Format(TimeSpan.FromSeconds(seconds)));
    }
}