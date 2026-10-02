namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class TestRunOptionsTests
{
    [Fact]
    public void Given_AnEmptyEnvironment_When_OptionsAreRead_Then_TheSpikeDefaultsApply()
    {
        // Act
        var options = TestRunOptions.FromEnvironment("cln", _ => null);

        // Assert
        Assert.Equal("cln", options.Suite);
        Assert.Null(options.RunId);
        Assert.Equal(TestRunOptions.SpikeNamespacePrefix, options.NamespacePrefix);
        Assert.Null(options.KubeContext);
        Assert.False(options.KeepNamespace);
        Assert.True(options.Spike);
    }

    [Fact]
    public void Given_TheHarnessVariables_When_OptionsAreRead_Then_TheyApply()
    {
        // Arrange
        var environment = new Dictionary<string, string>
        {
            [TestRunId.EnvironmentVariable] = "lane-b",
            [TestRunOptions.NamespacePrefixVariable] = "nltg",
            [KubeClientFactory.ContextVariable] = "orbstack",
            [TestRunOptions.KeepNamespaceVariable] = "true"
        };

        // Act
        var options = TestRunOptions.FromEnvironment("lnd", k => environment.GetValueOrDefault(k));

        // Assert
        Assert.Equal("lane-b", options.RunId);
        Assert.Equal("nltg", options.NamespacePrefix);
        Assert.Equal("orbstack", options.KubeContext);
        Assert.True(options.KeepNamespace);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    public void Given_TheAdoptVariable_When_OptionsAreRead_Then_AdoptNamespaceFollowsIt(string? value, bool expected)
    {
        // Act
        var options = TestRunOptions.FromEnvironment(
            "runner", k => k == TestRunOptions.AdoptNamespaceVariable ? value : null);

        // Assert
        Assert.Equal(expected, options.AdoptNamespace);
    }
}