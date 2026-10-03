namespace NLightning.Integration.Tests.Fixtures;

public class TestBackendTests
{
    private static readonly Action s_kubeConfigured = () => { };

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("cluster", true)]
    [InlineData(" CLUSTER ", true)]
    [InlineData("k8s", true)]
    [InlineData("kubernetes", true)]
    public void Given_ABackendValue_When_Parsed_Then_OnlyTheClusterOptsIn(string? value, bool expected)
    {
        // Arrange (the value)

        // Act
        var isCluster = TestBackend.IsClusterValue(value);

        // Assert
        Assert.Equal(expected, isCluster);
    }

    [Theory]
    [InlineData("dokcer")]
    [InlineData("docker")]
    public void Given_AnUnknownOrRetiredBackend_When_Parsed_Then_ItThrowsInsteadOfSkipping(string value)
    {
        // Act
        var exception = Assert.Throws<ArgumentException>(() => TestBackend.IsClusterValue(value));

        // Assert: the Docker backends are retired (NL-866), a stale docker value fails as loudly as a typo
        Assert.Contains(TestBackend.EnvironmentVariable, exception.Message);
        Assert.Contains(value, exception.Message);
    }

    [Fact]
    public void Given_NoOptIn_When_TheAvailabilityIsRead_Then_TheFixtureIsSkippedWithTheRunCommand()
    {
        // Arrange
        var probed = false;
        var skips = new List<string>();

        // Act
        var availability = new ClusterAvailability("the CLN fixture", "NL-866", "scripts/run-cluster.sh --matrix cln",
                                                   _ => null, () => probed = true, skips.Add);
        availability.SkipIfUnavailable();
        availability.ThrowIfMisconfigured();

        // Assert: no Kubernetes configuration read, one skip with a reason that says how to run it
        Assert.False(probed);
        Assert.False(availability.CanStart);
        Assert.Null(availability.ConfigurationError);
        Assert.Equal("The CLN fixture runs on the cluster backend only (NL-866): set NLTG_TEST_BACKEND=cluster or run "
                   + "scripts/run-cluster.sh --matrix cln", Assert.Single(skips));
    }

    [Fact]
    public void Given_TheOptInWithoutAKubeConfiguration_When_TheAvailabilityIsRead_Then_ItIsAnErrorNeverASkip()
    {
        // Arrange
        var skips = new List<string>();

        // Act
        var availability = new ClusterAvailability("the Eclair fixture", "NL-866", "x", Environment("cluster"),
                                                   () => throw new FileNotFoundException("no kubeconfig"), skips.Add);
        availability.SkipIfUnavailable();

        // Assert (NL-860)
        Assert.False(availability.CanStart);
        Assert.Null(availability.UnavailableReason);
        Assert.Empty(skips);
        var error = Assert.Throws<InvalidOperationException>(availability.ThrowIfMisconfigured);
        Assert.Contains("no Kubernetes cluster is configured to run the Eclair fixture on", error.Message);
        Assert.Contains("no kubeconfig", error.Message);
    }

    [Fact]
    public void Given_TheOptInWithAKubeConfiguration_When_TheAvailabilityIsRead_Then_TheFixtureMayStart()
    {
        // Act
        var availability = new ClusterAvailability("the LDK fixture", "NL-866", "x", Environment("cluster"),
                                                   s_kubeConfigured, _ => Assert.Fail("skipped"));
        availability.SkipIfUnavailable();
        availability.ThrowIfMisconfigured();

        // Assert
        Assert.True(availability.CanStart);
    }

    private static Func<string, string?> Environment(string? backend) =>
        name => name == TestBackend.EnvironmentVariable ? backend : null;
}