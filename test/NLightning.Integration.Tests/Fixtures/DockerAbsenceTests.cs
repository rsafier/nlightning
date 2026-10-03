using System.Text.RegularExpressions;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The cluster is the only backend of every suite but Tor (NL-820 for the LND suites, NL-866 for CLN, Eclair, LDK and
/// Postgres): the Docker API (<c>Docker.DotNet</c>) and the <c>docker</c> CLI are used only by the Tor interop fixture
/// (<c>Fixtures/Tor/</c>, the one Docker suite), the shared container helpers it uses, the SQL Server fixture (its tests
/// are not run) and the Explicit live test of <c>NLightning.Testing.Lnd.Tests</c>, which drives <c>docker</c> itself.
/// Only this assembly references the <c>Docker.DotNet</c> package, and it holds no <c>Docker*Backend</c> type, so a
/// Docker path cannot come back into a cluster fixture unnoticed. Reads the sources of the checkout the test assembly was
/// built from (skipped without one).
/// </summary>
public partial class DockerAbsenceTests
{
    /// <summary>The sources allowed to orchestrate Docker: a path, or a folder ending with <c>/</c>.</summary>
    private static readonly string[] s_allowedDockerSources =
    [
        "test/NLightning.Integration.Tests/Fixtures/Tor/",
        "test/NLightning.Integration.Tests/Fixtures/DockerContainerUtils.cs",
        "test/NLightning.Integration.Tests/Fixtures/SqlServerFixture.cs",
        "test/NLightning.Testing.Lnd.Tests/Docker/DockerCli.cs",
        // This file: its test data spells the patterns out
        "test/NLightning.Integration.Tests/Fixtures/DockerAbsenceTests.cs"
    ];

    /// <summary>The one project that may reference <c>Docker.DotNet</c>.</summary>
    private const string DockerPackageProject = "test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj";

    [Fact]
    public void Given_TheSources_When_Scanned_Then_OnlyTheTorAndSqlServerFixturesAndTheLiveLndTestDriveDocker()
    {
        // Arrange
        var root = LnUnitAbsenceTests.FindRepositoryRoot();

        // Act
        var users = LnUnitAbsenceTests.EnumerateSources(root, "*.cs")
                                      .Select(path => (Path: LnUnitAbsenceTests.Relative(root, path), Full: path))
                                      .Where(source => !IsAllowed(source.Path))
                                      .Where(source => File.ReadLines(source.Full)
                                                           .Any(line => DockerOrchestration().IsMatch(line)))
                                      .Select(source => source.Path)
                                      .ToList();

        // Assert
        Assert.Empty(users);
    }

    [Fact]
    public void Given_TheProjectsAndSharedMsBuildFiles_When_Scanned_Then_OnlyThisProjectReferencesDockerDotNet()
    {
        // Arrange
        var root = LnUnitAbsenceTests.FindRepositoryRoot();

        // Act
        var references = LnUnitAbsenceTests.EnumerateSources(root, "*.csproj")
                                           .Concat(LnUnitAbsenceTests.EnumerateSources(root, "*.props"))
                                           .Concat(LnUnitAbsenceTests.EnumerateSources(root, "*.targets"))
                                           .Concat(Directory.EnumerateFiles(root, "Directory.*.props"))
                                           .Distinct(StringComparer.Ordinal)
                                           .Where(path => File.ReadLines(path)
                                                              .Any(line => DockerPackage().IsMatch(line)))
                                           .Select(path => LnUnitAbsenceTests.Relative(root, path))
                                           .ToList();

        // Assert
        Assert.Equal([DockerPackageProject], references);
    }

    [Fact]
    public void Given_ThisAssembly_When_ItsTypesAreRead_Then_NoFixtureHasADockerBackend()
    {
        // Act: the retired DockerClnBackend, DockerEclairBackend, DockerLdkBackend, DockerPostgresBackend (NL-866) and
        // DockerLndBackend (NL-820), or any new one
        var dockerBackends = typeof(DockerAbsenceTests).Assembly.GetTypes()
                                                       .Select(type => type.Name)
                                                       .Where(name => name.StartsWith("Docker", StringComparison.Ordinal)
                                                                   && name.Contains("Backend", StringComparison.Ordinal))
                                                       .ToList();

        // Assert
        Assert.Empty(dockerBackends);
    }

    [Theory]
    [InlineData("using Docker.DotNet;", true)]
    [InlineData("using Docker.DotNet.Models;", true)]
    [InlineData("    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();", true)]
    [InlineData("        var info = new ProcessStartInfo(\"docker\")", true)]
    [InlineData("        var info = new System.Diagnostics.ProcessStartInfo(\"docker\")", true)]
    [InlineData("        using var process = Process.Start(\"docker\", \"ps\");", true)]
    [InlineData("using Docker.Utils;", false)]
    [InlineData("namespace NLightning.Integration.Tests.Docker.Interop.Cln;", false)]
    [InlineData("/// build it once with <c>docker build -t nltg-eclair:0.14.3 test/Docker/eclair</c>", false)]
    [InlineData("    public static bool CurrentTestFailed => TestDiagnostics.CurrentTestFailed;", false)]
    public void Given_ASourceLine_When_Matched_Then_OnlyDockerOrchestrationCounts(string line, bool matches)
    {
        // Act & Assert
        Assert.Equal(matches, DockerOrchestration().IsMatch(line));
    }

    [Theory]
    [InlineData("""    <PackageReference Include="Docker.DotNet" Version="3.125.15"/>""", true)]
    [InlineData("""    <PackageVersion Include="Docker.DotNet" Version="3.125.15" />""", true)]
    [InlineData("""    <PackageReference Include="Docker.DotNet.X509" Version="3.125.15"/>""", true)]
    [InlineData("""    <PackageReference Include="KubernetesClient" Version="18.0.5"/>""", false)]
    public void Given_AnMsBuildLine_When_Matched_Then_OnlyDockerPackagesCount(string line, bool matches)
    {
        // Act & Assert
        Assert.Equal(matches, DockerPackage().IsMatch(line));
    }

    private static bool IsAllowed(string path) =>
        s_allowedDockerSources.Any(allowed => allowed.EndsWith('/')
                                                  ? path.StartsWith(allowed, StringComparison.Ordinal)
                                                  : path == allowed);

    /// <summary>
    /// The Docker API (<c>Docker.DotNet</c>, <c>DockerClient</c>) or a <c>docker</c> CLI process, outside comments.
    /// </summary>
    [GeneratedRegex(@"^(?!\s*//)[^/]*(\bDocker\.DotNet\b|\bDockerClient\w*\b|Process(StartInfo)?\s*\(\s*""docker""|Process\.Start\(\s*""docker"")")]
    private static partial Regex DockerOrchestration();

    /// <summary>A package reference or central version of <c>Docker.DotNet</c> (or one of its packages).</summary>
    [GeneratedRegex(@"Package(Reference|Version)\s+(Include|Update)=""Docker\.DotNet(\.[A-Za-z0-9]+)?""", RegexOptions.IgnoreCase)]
    private static partial Regex DockerPackage();
}