using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunNamespaceTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "spike" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_ARun_When_ItsNamespaceIsBuilt_Then_ItCarriesTheRunLabels()
    {
        // Act
        var ns = RunNamespace.Build(s_run);

        // Assert
        Assert.Equal("nltg-spike-r1", ns.Metadata.Name);
        Assert.Equal("r1", ns.Metadata.Labels[RunLabels.Run]);
        Assert.Equal("spike", ns.Metadata.Labels[RunLabels.Suite]);
        Assert.Equal("true", ns.Metadata.Labels[RunLabels.Spike]);
        Assert.True(RunNamespace.IsOwnedBy(ns, "r1", "nltg-spike"));
    }

    [Fact]
    public void Given_AQuota_When_Built_Then_ItHoldsRequestsLimitsAndCounts()
    {
        // Act
        var quota = RunNamespace.BuildQuota(s_run, NamespaceQuota.Spike);

        // Assert
        Assert.Equal(NamespaceQuota.ObjectName, quota.Metadata.Name);
        Assert.Equal("nltg-spike-r1", quota.Metadata.NamespaceProperty);
        Assert.Equal("6", quota.Spec.Hard["requests.cpu"].ToString());
        Assert.Equal("6Gi", quota.Spec.Hard["requests.memory"].ToString());
        Assert.Equal("12", quota.Spec.Hard["limits.cpu"].ToString());
        Assert.Equal("20", quota.Spec.Hard["pods"].ToString());
        Assert.Equal("20", quota.Spec.Hard["persistentvolumeclaims"].ToString());
    }

    [Fact]
    public void Given_ARequestsOnlyQuota_When_Built_Then_ItHasNoLimits()
    {
        // Act
        var quota = RunNamespace.BuildQuota(s_run, new NamespaceQuota("1", "1Gi"));

        // Assert
        Assert.Equal(2, quota.Spec.Hard.Count);
    }

    [Theory]
    [InlineData("default", "r1", true)]
    [InlineData("nltg-spike-r1", "r2", true)]
    [InlineData("nltg-spike-r1", "r1", false)]
    [InlineData("nltg-other-r1", "r1", true)]
    public void Given_ANamespaceThatIsNotTheRunsOwn_When_Checked_Then_ItIsNotOwned(string name, string runLabel,
        bool managedBy)
    {
        // Arrange
        var labels = new Dictionary<string, string> { [RunLabels.Run] = runLabel };
        if (managedBy)
            labels[RunLabels.ManagedBy] = RunLabels.ManagedByValue;
        var ns = new V1Namespace { Metadata = new V1ObjectMeta { Name = name, Labels = labels } };

        // Act
        var owned = RunNamespace.IsOwnedBy(ns, "r1", "nltg-spike");

        // Assert
        Assert.False(owned);
    }

    [Fact]
    public void Given_AnUnlabelledNamespace_When_Checked_Then_ItIsNotOwned()
    {
        // Arrange
        var ns = new V1Namespace { Metadata = new V1ObjectMeta { Name = "nltg-spike-r1" } };

        // Act & Assert
        Assert.False(RunNamespace.IsOwnedBy(ns, "r1", "nltg-spike"));
    }
}