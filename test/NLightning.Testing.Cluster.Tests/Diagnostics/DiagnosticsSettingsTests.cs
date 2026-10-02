namespace NLightning.Testing.Cluster.Tests.Diagnostics;

using Cluster.Diagnostics;
using Cluster.Run;

public class DiagnosticsSettingsTests
{
    [Theory]
    [InlineData(null, DiagnosticsMode.Failure)]
    [InlineData("", DiagnosticsMode.Failure)]
    [InlineData("failure", DiagnosticsMode.Failure)]
    [InlineData("on", DiagnosticsMode.Failure)]
    [InlineData("ALWAYS", DiagnosticsMode.Always)]
    [InlineData("all", DiagnosticsMode.Always)]
    [InlineData("off", DiagnosticsMode.Off)]
    [InlineData("0", DiagnosticsMode.Off)]
    [InlineData(" false ", DiagnosticsMode.Off)]
    public void Given_AModeValue_When_Parsed_Then_TheModeFollows(string? value, DiagnosticsMode expected)
    {
        // Act
        var mode = DiagnosticsSettings.ParseMode(value);

        // Assert
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void Given_TheVariables_When_SettingsAreRead_Then_ModeAndRootApply()
    {
        // Arrange
        var environment = new Dictionary<string, string>
        {
            [DiagnosticsSettings.ModeVariable] = "always",
            [DiagnosticsSettings.DirectoryVariable] = "/tmp/nltg-diag-root"
        };

        // Act
        var settings = DiagnosticsSettings.FromEnvironment(k => environment.GetValueOrDefault(k));

        // Assert
        Assert.Equal(DiagnosticsMode.Always, settings.Mode);
        Assert.Equal("/tmp/nltg-diag-root", settings.ResolveRoot("run-1"));
    }

    [Fact]
    public void Given_NoDirectory_When_TheRootIsResolved_Then_ItIsTheRepositorysTestResults()
    {
        // Arrange
        var settings = DiagnosticsSettings.FromEnvironment(_ => null);

        // Act
        var root = settings.ResolveRoot("abc123");

        // Assert: the tests run under the repository, which has NLightning.sln
        Assert.Equal(DiagnosticsMode.Failure, settings.Mode);
        Assert.EndsWith(Path.Combine("TestResults", "cluster", "abc123"), root);
        var repo = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(root)))!;
        Assert.True(File.Exists(Path.Combine(repo, "NLightning.sln")), repo);
    }

    [Theory]
    [InlineData("failure", false, true)]
    [InlineData("FAILURE", false, true)]
    [InlineData("1", true, false)]
    [InlineData(null, false, false)]
    public void Given_TheKeepVariable_When_OptionsAreRead_Then_KeepOnFailureFollowsIt(string? value, bool keep,
                                                                                         bool keepOnFailure)
    {
        // Act
        var options = TestRunOptions.FromEnvironment(
            "diag", k => k == TestRunOptions.KeepNamespaceVariable ? value : null);

        // Assert
        Assert.Equal(keep, options.KeepNamespace);
        Assert.Equal(keepOnFailure, options.KeepNamespaceOnFailure);
    }

    [Fact]
    public void Given_TheDiagnosticsVariables_When_RunOptionsAreRead_Then_TheyCarryThem()
    {
        // Act
        var options = TestRunOptions.FromEnvironment(
            "diag", k => k == DiagnosticsSettings.ModeVariable ? "off" : null);

        // Assert
        Assert.Equal(DiagnosticsMode.Off, options.Diagnostics.Mode);
        Assert.Null(options.Diagnostics.RootDirectory);
    }

    [Fact]
    public void Given_TheKeepPatch_When_Built_Then_ItSetsTheKeepAnnotationOnly()
    {
        // Act
        var patch = RunAnnotations.KeepPatch();

        // Assert
        Assert.Equal("{\"metadata\":{\"annotations\":{\"nltg.keep\":\"true\"}}}", patch);
    }
}