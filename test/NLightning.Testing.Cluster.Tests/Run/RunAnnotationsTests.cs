using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunAnnotationsTests
{
    [Fact]
    public void Given_KeepAndTtlOptions_When_TheAnnotationsAreBuilt_Then_TheyRecordOwnerKeepAndTtl()
    {
        // Arrange
        var options = new TestRunOptions { KeepNamespace = true, Ttl = TimeSpan.FromSeconds(90.2) };

        // Act
        var annotations = RunAnnotations.ForRun(options, new RunOwner("mac", 9, 5));

        // Assert
        Assert.Equal("true", annotations[RunAnnotations.Keep]);
        Assert.Equal("91", annotations[RunAnnotations.TtlSeconds]);
        Assert.Equal("9", annotations[RunOwner.PidAnnotation]);
        Assert.Equal("mac", annotations[RunOwner.HostAnnotation]);
        Assert.Equal("5", annotations[RunOwner.StartAnnotation]);
    }

    [Fact]
    public void Given_DefaultOptionsAndNoOwner_When_TheAnnotationsAreBuilt_Then_ThereAreNone()
    {
        // Act & Assert
        Assert.Empty(RunAnnotations.ForRun(new TestRunOptions(), null));
    }

    [Fact]
    public void Given_ARunWithAnnotations_When_ItsNamespaceIsBuilt_Then_ItCarriesThem()
    {
        // Arrange
        var run = RunIdentity.Create(new TestRunOptions { RunId = "r1" }, DateTimeOffset.UnixEpoch);
        var annotations = RunAnnotations.ForRun(new TestRunOptions { KeepNamespace = true }, new RunOwner("mac", 9, 5));

        // Act
        var ns = RunNamespace.Build(run, annotations);

        // Assert
        Assert.True(RunAnnotations.IsKept(ns));
        Assert.Equal(new RunOwner("mac", 9, 5), RunOwner.FromNamespace(ns));
        Assert.Null(RunNamespace.Build(run).Metadata.Annotations);
    }

    [Fact]
    public void Given_AStartedLabel_When_TheStartIsRead_Then_TheLabelWinsOverTheCreationTime()
    {
        // Arrange
        var ns = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { [RunLabels.Started] = "1800000000" },
                CreationTimestamp = DateTime.UnixEpoch
            }
        };
        var unlabelled = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                CreationTimestamp = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Unspecified)
            }
        };

        // Act & Assert
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), RunAnnotations.GetStartedAt(ns));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), RunAnnotations.GetStartedAt(unlabelled));
        Assert.Null(RunAnnotations.GetStartedAt(new V1Namespace { Metadata = new V1ObjectMeta() }));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("x", null)]
    [InlineData("3600", 3600)]
    public void Given_ATtlAnnotation_When_Read_Then_OnlyAPositiveNumberCounts(string value, int? seconds)
    {
        // Arrange
        var ns = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Annotations = new Dictionary<string, string> { [RunAnnotations.TtlSeconds] = value }
            }
        };

        // Act & Assert
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, RunAnnotations.GetTtl(ns));
    }

    [Theory]
    [InlineData("lane", 1, "lane")]
    [InlineData("lane", 2, "lane-2")]
    [InlineData("Lane_B", 12, "lane-b-12")]
    public void Given_ARunId_When_Suffixed_Then_TheDerivedIdIsDistinct(string id, int n, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, TestRunId.WithSuffix(id, n));
    }

    [Fact]
    public void Given_AMaximalRunId_When_Suffixed_Then_TheSuffixSurvives()
    {
        // Arrange
        var id = new string('a', 37) + "-bc";

        // Act
        var derived = TestRunId.WithSuffix(id, 3);

        // Assert
        Assert.Equal(TestRunId.MaxLength - 1, derived.Length);
        Assert.EndsWith("a-3", derived);
        Assert.Throws<ArgumentOutOfRangeException>(() => TestRunId.WithSuffix(id, 0));
    }

    [Fact]
    public void Given_TheCapVariable_When_OptionsAreRead_Then_ItSetsTheCap()
    {
        // Act
        var unset = TestRunOptions.FromEnvironment("cln", _ => null);
        var off = TestRunOptions.FromEnvironment("cln", k => k == RunAdmission.MaxRunsVariable ? "off" : null);
        var three = TestRunOptions.FromEnvironment("cln", k => k == RunAdmission.MaxRunsVariable ? "3" : null);

        // Assert
        Assert.Equal(RunAdmission.DefaultMaxRuns, unset.MaxConcurrentRuns);
        Assert.Null(off.MaxConcurrentRuns);
        Assert.Equal(3, three.MaxConcurrentRuns);
    }
}