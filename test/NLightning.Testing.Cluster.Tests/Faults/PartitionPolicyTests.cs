namespace NLightning.Testing.Cluster.Tests.Faults;

using Cluster.Faults;
using Cluster.Run;

public class PartitionPolicyTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { RunId = "r1", Suite = "faults" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Given_NoOtherSide_When_Built_Then_TheSideReachesOnlyItselfAndDns()
    {
        // Act
        var policy = PartitionPolicy.Build(s_run, "nltg-partition-1", ["bob", "alice", "bob"], null,
                                           PartitionOptions.Default);

        // Assert
        Assert.Equal("nltg-partition-1", policy.Metadata.Name);
        Assert.Equal("nltg-spike-r1", policy.Metadata.NamespaceProperty);
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);

        var selected = policy.Spec.PodSelector;
        Assert.Equal("r1", selected.MatchLabels[RunLabels.Run]);
        var expression = Assert.Single(selected.MatchExpressions);
        Assert.Equal(RunLabels.Node, expression.Key);
        Assert.Equal("In", expression.OperatorProperty);
        Assert.Equal(["alice", "bob"], expression.Values);

        var ingress = Assert.Single(policy.Spec.Ingress);
        var peer = Assert.Single(ingress.FromProperty);
        Assert.Equal("In", peer.PodSelector.MatchExpressions[0].OperatorProperty);
        Assert.Null(peer.NamespaceSelector);

        Assert.Equal(2, policy.Spec.Egress.Count);
        Assert.Equal("In", policy.Spec.Egress[0].To[0].PodSelector.MatchExpressions[0].OperatorProperty);
        var dns = policy.Spec.Egress[1];
        Assert.Equal("kube-system", dns.To[0].NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("kube-dns", dns.To[0].PodSelector.MatchLabels["k8s-app"]);
        Assert.Equal(["UDP", "TCP"], dns.Ports.Select(p => p.Protocol));
        Assert.All(dns.Ports, p => Assert.Equal("53", p.Port.Value));
        Assert.Equal("*", policy.Metadata.Annotations["nltg.partition/others"]);
    }

    [Fact]
    public void Given_AnOtherSide_When_Built_Then_TheSideReachesEveryPodButTheOtherSide()
    {
        // Act
        var policy = PartitionPolicy.Build(s_run, "nltg-partition-2", ["alice"], ["carol", "bob"],
                                           PartitionOptions.Default);

        // Assert
        Assert.Equal(["alice"], policy.Spec.PodSelector.MatchExpressions[0].Values);
        var reachable = policy.Spec.Ingress[0].FromProperty[0].PodSelector;
        Assert.Null(reachable.MatchLabels);
        Assert.Equal("NotIn", reachable.MatchExpressions[0].OperatorProperty);
        Assert.Equal(["bob", "carol"], reachable.MatchExpressions[0].Values);
        Assert.Equal("NotIn", policy.Spec.Egress[0].To[0].PodSelector.MatchExpressions[0].OperatorProperty);
        Assert.Equal("bob,carol", policy.Metadata.Annotations["nltg.partition/others"]);
    }

    [Fact]
    public void Given_RunnerCidrsAndNoDns_When_Built_Then_TheCidrsMayConnectInAndDnsIsCut()
    {
        // Arrange
        var options = new PartitionOptions { AllowDns = false, AllowedIngressCidrs = ["192.168.194.0/32", "10.0.0.0/8"] };

        // Act
        var policy = PartitionPolicy.Build(s_run, "nltg-partition-3", ["alice"], null, options);

        // Assert
        var from = policy.Spec.Ingress[0].FromProperty;
        Assert.Equal(3, from.Count);
        Assert.Equal(["192.168.194.0/32", "10.0.0.0/8"], from.Skip(1).Select(p => p.IpBlock.Cidr));
        Assert.Single(policy.Spec.Egress);
    }

    [Fact]
    public void Given_APolicy_When_Built_Then_ItCarriesTheRunAndFaultLabels()
    {
        // Act
        var policy = PartitionPolicy.Build(s_run, "nltg-partition-4", ["alice"], null, PartitionOptions.Default);

        // Assert
        var labels = policy.Metadata.Labels;
        Assert.Equal("r1", labels[RunLabels.Run]);
        Assert.Equal("true", labels[RunLabels.Spike]);
        Assert.Equal(RunLabels.ManagedByValue, labels[RunLabels.ManagedBy]);
        Assert.Equal(PartitionPolicy.FaultLabelValue, labels[PartitionPolicy.FaultLabel]);
        Assert.Equal("nltg.fault=partition,nltg.run=r1", PartitionPolicy.Selector("r1"));
    }

    [Fact]
    public void Given_ANodeOnBothSides_When_Built_Then_ItThrows()
    {
        // Act / Assert
        var e = Assert.Throws<ArgumentException>(() => PartitionPolicy.Build(
                                                     s_run, "nltg-partition-5", ["alice", "bob"], ["bob"],
                                                     PartitionOptions.Default));
        Assert.Contains("bob", e.Message);
    }

    [Fact]
    public void Given_AnEmptySide_When_Built_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => PartitionPolicy.Build(s_run, "nltg-partition-6", [], null,
                                                                     PartitionOptions.Default));
        Assert.Throws<ArgumentException>(() => PartitionPolicy.Build(s_run, "nltg-partition-6", ["alice"], [],
                                                                     PartitionOptions.Default));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("al_ice")]
    [InlineData("")]
    public void Given_AnInvalidNodeName_When_Built_Then_ItThrows(string node)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => PartitionPolicy.Build(s_run, "nltg-partition-7", [node], null,
                                                                     PartitionOptions.Default));
    }

    [Fact]
    public void Given_AnInvalidPolicyName_When_Built_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => PartitionPolicy.Build(s_run, "Partition 1", ["alice"], null,
                                                                     PartitionOptions.Default));
    }

    [Fact]
    public void Given_RunnerCidrsInTheEnvironment_When_OptionsAreRead_Then_TheyAreSplitAndTrimmed()
    {
        // Act
        var options = PartitionOptions.FromEnvironment(
            name => name == PartitionOptions.RunnerCidrsVariable ? " 192.168.194.0/32 ,, 10.1.0.0/16" : null);
        var none = PartitionOptions.FromEnvironment(_ => "  ");

        // Assert
        Assert.Equal(["192.168.194.0/32", "10.1.0.0/16"], options.AllowedIngressCidrs);
        Assert.True(options.AllowDns);
        Assert.Empty(none.AllowedIngressCidrs);
    }
}