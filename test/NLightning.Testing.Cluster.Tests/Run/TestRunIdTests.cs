namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Kube;
using Cluster.Run;

public class TestRunIdTests
{
    [Theory]
    [InlineData("lane-a", "lane-a")]
    [InlineData("Batch10_CLN", "batch10-cln")]
    [InlineData("  spike//run..1  ", "spike-run-1")]
    [InlineData("-x-", "x")]
    public void Given_ARequestedId_When_Normalized_Then_ItIsADnsLabel(string requested, string expected)
    {
        // Act
        var id = TestRunId.Normalize(requested);

        // Assert
        Assert.Equal(expected, id);
        Assert.True(KubeNames.IsDns1123Label(id));
    }

    [Fact]
    public void Given_ALongId_When_Normalized_Then_ItIsCappedWithoutATrailingDash()
    {
        // Arrange
        var requested = new string('a', 39) + "-bbbbbbbb";

        // Act
        var id = TestRunId.Normalize(requested);

        // Assert
        Assert.Equal(new string('a', 39), id);
        Assert.True(id.Length <= TestRunId.MaxLength);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("__")]
    public void Given_NothingUsable_When_Normalized_Then_ItThrows(string requested)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => TestRunId.Normalize(requested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Given_NoRequestedId_When_Resolved_Then_ARandomHexIdIsGenerated(string? requested)
    {
        // Act
        var first = TestRunId.Resolve(requested);
        var second = TestRunId.Resolve(requested);

        // Assert
        Assert.Equal(TestRunId.GeneratedLength, first.Length);
        Assert.Matches("^[0-9a-f]+$", first);
        Assert.NotEqual(first, second);
    }
}