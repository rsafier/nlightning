using System.Text.RegularExpressions;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// LNUnit stays confined to the Docker LND backend (<c>Fixtures/Lnd/DockerLndBackend.cs</c>, NL-819): no other test
/// source uses its namespaces, and only Integration.Tests references the package. Reads the sources of the checkout
/// the test assembly was built from (skipped when it runs without one, e.g. a copied output directory).
/// </summary>
public partial class LnUnitConfinementTests
{
    private const string AllowedSource = "test/NLightning.Integration.Tests/Fixtures/Lnd/DockerLndBackend.cs";
    private const string AllowedProject = "test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj";

    [Fact]
    public void Given_TheTestSources_When_Scanned_Then_OnlyTheLndBackendUsesLnUnit()
    {
        // Arrange
        var root = FindRepositoryRoot();

        // Act
        var users = EnumerateSources(root, "*.cs")
                   .Where(path => File.ReadLines(path).Any(line => LnUnitUsage().IsMatch(line)))
                   .Select(path => Relative(root, path))
                   .ToList();

        // Assert
        Assert.Equal([AllowedSource], users);
    }

    [Fact]
    public void Given_TheTestProjects_When_Scanned_Then_OnlyIntegrationTestsReferencesLnUnit()
    {
        // Arrange
        var root = FindRepositoryRoot();

        // Act
        var projects = EnumerateSources(root, "*.csproj")
                      .Where(path => File.ReadLines(path).Any(line => LnUnitPackage().IsMatch(line)))
                      .Select(path => Relative(root, path))
                      .ToList();

        // Assert
        Assert.Equal([AllowedProject], projects);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NLightning.sln"))
             && File.Exists(Path.Combine(directory.FullName, AllowedSource)))
                return directory.FullName;
        }

        Assert.Skip("No NLightning checkout above the test assembly");
        return string.Empty;
    }

    private static IEnumerable<string> EnumerateSources(string root, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(root, "test"), pattern, SearchOption.AllDirectories)
                 .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), pattern, SearchOption.AllDirectories))
                 .Concat(Directory.EnumerateFiles(Path.Combine(root, "tools"), pattern, SearchOption.AllDirectories))
                 .Where(path => !IsBuildOutput(root, path))
                 .Order(StringComparer.Ordinal);

    private static bool IsBuildOutput(string root, string path) =>
        Relative(root, path).Split('/').Any(segment => segment is "bin" or "obj");

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A <c>using</c> of an LNUnit namespace, or a qualified name in code (comments start with //).</summary>
    [GeneratedRegex(@"^\s*(using\s+(static\s+)?LNUnit\b|[^/]*\bLNUnit\.(Setup|LND|Fixtures|Models)\b)")]
    private static partial Regex LnUnitUsage();

    [GeneratedRegex("""PackageReference\s+Include="LNUnit""", RegexOptions.IgnoreCase)]
    private static partial Regex LnUnitPackage();
}