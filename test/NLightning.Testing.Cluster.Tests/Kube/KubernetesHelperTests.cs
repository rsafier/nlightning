namespace NLightning.Testing.Cluster.Tests.Kube;

using Cluster.Kube;

public class KubernetesHelperTests
{
    [Fact]
    public void Given_AClaim_When_Built_Then_ItIsReadWriteOnceWithTheRequestedSize()
    {
        // Act
        var claim = KubernetesHelper.BuildPersistentVolumeClaim("nltg-spike-r1", "shared", "5Gi",
                                                                new Dictionary<string, string> { ["nltg.run"] = "r1" },
                                                                "local-path");

        // Assert
        Assert.Equal("shared", claim.Metadata.Name);
        Assert.Equal("nltg-spike-r1", claim.Metadata.NamespaceProperty);
        Assert.Equal("r1", claim.Metadata.Labels["nltg.run"]);
        Assert.Equal(["ReadWriteOnce"], claim.Spec.AccessModes);
        Assert.Equal("5Gi", claim.Spec.Resources.Requests["storage"].ToString());
        Assert.Equal("local-path", claim.Spec.StorageClassName);
    }

    [Fact]
    public void Given_AFailedCommand_When_SuccessIsRequired_Then_TheExceptionCarriesStdErr()
    {
        // Arrange
        var result = new ExecResult(1, [], "cat: /x: No such file"u8.ToArray());

        // Act
        var exception = Assert.Throws<KubeExecException>(() => result.EnsureSuccess("cat /x"));

        // Assert
        Assert.Equal("cat /x exited with 1: cat: /x: No such file", exception.Message);
        Assert.Same(result, exception.Result);
    }

    [Theory]
    [InlineData("nltg-spike-r1", true)]
    [InlineData("a", true)]
    [InlineData("A", false)]
    [InlineData("a.b", false)]
    [InlineData("a-", false)]
    [InlineData(null, false)]
    public void Given_AName_When_CheckedAsADnsLabel_Then_TheRuleApplies(string? name, bool valid)
    {
        // Act & Assert
        Assert.Equal(valid, KubeNames.IsDns1123Label(name));
    }
}