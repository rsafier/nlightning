namespace NLightning.Integration.Tests.Fixtures;

public class TestBackendTests
{
    [Theory]
    [InlineData(null, TestBackendKind.Docker)]
    [InlineData("", TestBackendKind.Docker)]
    [InlineData("docker", TestBackendKind.Docker)]
    [InlineData(" Docker ", TestBackendKind.Docker)]
    [InlineData("cluster", TestBackendKind.Cluster)]
    [InlineData("CLUSTER", TestBackendKind.Cluster)]
    [InlineData("k8s", TestBackendKind.Cluster)]
    [InlineData("kubernetes", TestBackendKind.Cluster)]
    public void Given_ABackendValue_When_Parsed_Then_ItNamesTheBackend(string? value, TestBackendKind expected)
    {
        // Arrange (the value)

        // Act
        var backend = TestBackend.Parse(value);

        // Assert
        Assert.Equal(expected, backend);
    }

    [Fact]
    public void Given_AnUnknownBackend_When_Parsed_Then_ItThrowsInsteadOfFallingBackToDocker()
    {
        // Arrange
        const string typo = "dokcer";

        // Act
        var exception = Assert.Throws<ArgumentException>(() => TestBackend.Parse(typo));

        // Assert
        Assert.Contains(TestBackend.EnvironmentVariable, exception.Message);
        Assert.Contains(typo, exception.Message);
    }
}