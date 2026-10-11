namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Run;

public class RunLabelsTests
{
    [Fact]
    public void Given_RunLabels_When_ANodeIsLabelled_Then_ItKeepsTheRunLabelsAndAddsNodeAndKind()
    {
        // Arrange
        var run = RunLabels.ForRun("r1", "lnd", DateTimeOffset.FromUnixTimeSeconds(100), spike: false);

        // Act
        var labels = RunLabels.ForNode(run, "alice", NodeKind.Lnd);

        // Assert
        Assert.Equal("r1", labels[RunLabels.Run]);
        Assert.Equal("lnd", labels[RunLabels.Suite]);
        Assert.Equal("100", labels[RunLabels.Started]);
        Assert.Equal("alice", labels[RunLabels.Node]);
        Assert.Equal("lnd", labels[RunLabels.Kind]);
        Assert.False(labels.ContainsKey(RunLabels.Spike));
        Assert.All(labels.Values, v => Assert.True(KubeNames.IsLabelValue(v)));
    }

    [Fact]
    public void Given_Labels_When_TurnedIntoASelector_Then_KeysAreSortedAndJoined()
    {
        // Act
        var selector = RunLabels.ToSelector(RunLabels.NodeSelector("r1", "miner"));

        // Assert
        Assert.Equal("nltg.node=miner,nltg.run=r1", selector);
    }

    [Theory]
    [InlineData("Docker.Interop.Cln", "Docker.Interop.Cln")]
    [InlineData("cln suite #2", "cln-suite--2")]
    [InlineData("--x--", "x")]
    [InlineData("", "")]
    public void Given_AValue_When_Sanitized_Then_ItIsAValidLabelValue(string value, string expected)
    {
        // Act
        var sanitized = RunLabels.SanitizeValue(value);

        // Assert
        Assert.Equal(expected, sanitized);
        Assert.True(KubeNames.IsLabelValue(sanitized));
    }

    [Fact]
    public void Given_ALongValue_When_Sanitized_Then_ItIsCappedAt63()
    {
        // Act
        var sanitized = RunLabels.SanitizeValue(new string('a', 100));

        // Assert
        Assert.Equal(63, sanitized.Length);
    }
}