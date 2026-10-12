namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunIdentityTests
{
    private static readonly DateTimeOffset s_started = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_SpikeOptions_When_Created_Then_TheNamespaceIsSpikePrefixedAndLabelled()
    {
        // Arrange
        var options = new TestRunOptions { RunId = "Lane-A", Suite = "cln interop" };

        // Act
        var run = RunIdentity.Create(options, s_started);

        // Assert
        Assert.Equal("lane-a", run.Id);
        Assert.Equal("nltg-spike-lane-a", run.Namespace);
        Assert.Equal("lane-a", run.Labels[RunLabels.Run]);
        Assert.Equal("cln-interop", run.Labels[RunLabels.Suite]);
        Assert.Equal("1790942400", run.Labels[RunLabels.Started]);
        Assert.Equal("true", run.Labels[RunLabels.Spike]);
        Assert.Equal(RunLabels.ManagedByValue, run.Labels[RunLabels.ManagedBy]);
        Assert.Equal("alice.nltg-spike-lane-a.svc.cluster.local", run.ServiceDnsName("alice"));
    }

    [Fact]
    public void Given_TheRealHarnessPrefix_When_Created_Then_TheNamespaceIsNltgRunAndNotSpike()
    {
        // Arrange
        var options = new TestRunOptions
        {
            RunId = "abc123",
            NamespacePrefix = TestRunOptions.DefaultNamespacePrefix,
            Spike = false
        };

        // Act
        var run = RunIdentity.Create(options, s_started);

        // Assert
        Assert.Equal("nltg-abc123", run.Namespace);
        Assert.False(run.Labels.ContainsKey(RunLabels.Spike));
    }

    [Fact]
    public void Given_NoRunId_When_Created_Then_AnIdIsGenerated()
    {
        // Act
        var run = RunIdentity.Create(new TestRunOptions(), s_started);

        // Assert
        Assert.StartsWith("nltg-spike-", run.Namespace);
        Assert.Equal(TestRunId.GeneratedLength, run.Id.Length);
    }

    [Theory]
    [InlineData("NLTG")]
    [InlineData("nltg_spike")]
    [InlineData("a-prefix-that-is-far-too-long")]
    [InlineData("")]
    public void Given_AnInvalidPrefix_When_Created_Then_ItThrows(string prefix)
    {
        // Arrange
        var options = new TestRunOptions { RunId = "x", NamespacePrefix = prefix };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => RunIdentity.Create(options, s_started));
    }

    [Fact]
    public void Given_TheLongestPrefixAndId_When_Created_Then_TheNamespaceFitsALabel()
    {
        // Arrange
        var options = new TestRunOptions
        {
            RunId = new string('r', TestRunId.MaxLength),
            NamespacePrefix = new string('p', TestRunOptions.MaxPrefixLength)
        };

        // Act
        var run = RunIdentity.Create(options, s_started);

        // Assert
        Assert.Equal(63, run.Namespace.Length);
    }
}