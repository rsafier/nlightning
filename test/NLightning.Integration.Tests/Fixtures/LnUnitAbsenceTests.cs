using System.Text.Json;
using System.Text.RegularExpressions;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// The solution references no LNUnit package (NL-820: the Docker LND backend, LNUnit's last user, was retired; every
/// LND client is <c>NLightning.Testing.Lnd</c> and the LND network runs on the cluster harness): no project or shared
/// MSBuild file references <c>LNUnit</c>, <c>LNUnit.LND</c> or <c>lnunit.lnd</c>, no source uses an LNUnit namespace, and this
/// assembly (which references nearly every project) resolves none of them, not even transitively. Reads the sources of
/// the checkout the test assembly was built from (skipped when it runs without one, e.g. a copied output directory).
/// </summary>
public partial class LnUnitAbsenceTests
{
    /// <summary>This file: its test data spells the patterns out.</summary>
    private const string ThisSource = "test/NLightning.Integration.Tests/Fixtures/LnUnitAbsenceTests.cs";

    [Fact]
    public void Given_TheProjectsAndSharedMsBuildFiles_When_Scanned_Then_NoneReferencesAnLnUnitPackage()
    {
        // Arrange
        var root = FindRepositoryRoot();

        // Act: the csprojs and every shared MSBuild file (Directory.Build.props/.targets, Directory.Packages.props, any
        // imported .props/.targets) under test/, src/, tools/ and at the root
        var references = EnumerateSources(root, "*.csproj")
                        .Concat(EnumerateSources(root, "*.props"))
                        .Concat(EnumerateSources(root, "*.targets"))
                        .Concat(Directory.EnumerateFiles(root, "Directory.*.props"))
                        .Concat(Directory.EnumerateFiles(root, "Directory.*.targets"))
                        .Distinct(StringComparer.Ordinal)
                        .Where(path => File.ReadLines(path).Any(line => LnUnitPackage().IsMatch(line)))
                        .Select(path => Relative(root, path))
                        .Order(StringComparer.Ordinal)
                        .ToList();

        // Assert
        Assert.Empty(references);
    }

    [Fact]
    public void Given_TheSources_When_Scanned_Then_NoneUsesAnLnUnitNamespace()
    {
        // Arrange
        var root = FindRepositoryRoot();

        // Act
        var users = EnumerateSources(root, "*.cs")
                   .Where(path => Relative(root, path) != ThisSource)
                   .Where(path => File.ReadLines(path).Any(line => LnUnitUsage().IsMatch(line)))
                   .Select(path => Relative(root, path))
                   .ToList();

        // Assert
        Assert.Empty(users);
    }

    [Fact]
    public void Given_ThisAssemblysDependencies_When_Read_Then_NoLnUnitLibraryIsResolved()
    {
        // Arrange: the deps file lists every package the build resolved, transitive ones included
        var depsFile = Path.Combine(AppContext.BaseDirectory, "NLightning.Integration.Tests.deps.json");
        Assert.SkipUnless(File.Exists(depsFile), $"No {Path.GetFileName(depsFile)} next to the test assembly");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));

        // Act
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject()
                            .Select(library => library.Name)
                            .Where(name => name.StartsWith("lnunit", StringComparison.OrdinalIgnoreCase))
                            .ToList();

        // Assert
        Assert.Empty(libraries);
    }

    [Theory]
    [InlineData("""    <PackageReference Include="LNUnit" Version="3.0.4"/>""", true)]
    [InlineData("""    <PackageReference Include="LNUnit.LND" Version="3.0.4" />""", true)]
    [InlineData("""    <PackageVersion Include="lnunit.lnd" Version="3.0.4" />""", true)]
    [InlineData("""    <PackageReference Update="LNUnit" Version="3.0.5"/>""", true)]
    [InlineData("""    <PackageReference Include="NLightning.LnUnitReplacement" Version="1.0.0"/>""", false)]
    [InlineData("""    <PackageReference Include="Grpc.Net.Client" Version="2.76.0"/>""", false)]
    public void Given_AnMsBuildLine_When_Matched_Then_OnlyLnUnitPackagesCount(string line, bool matches)
    {
        // Act & Assert
        Assert.Equal(matches, LnUnitPackage().IsMatch(line));
    }

    [Theory]
    [InlineData("using LNUnit.Setup;", true)]
    [InlineData("global using LNUnit.LND;", true)]
    [InlineData("        var builder = new LNUnit.Setup.LNUnitBuilder();", true)]
    [InlineData("using Builder = LNUnit.Setup.LNUnitBuilder;", true)]
    [InlineData("using Lnrpc;", false)]
    [InlineData("using NLightning.Testing.Lnd.Lnrpc;", false)]
    [InlineData("// LNUnit's AddPolarLNDNode flags", false)]
    [InlineData("/// <c>LNUnit.Setup</c>'s Docker extensions", false)]
    public void Given_ASourceLine_When_Matched_Then_OnlyLnUnitNamespacesCount(string line, bool matches)
    {
        // Act & Assert
        Assert.Equal(matches, LnUnitUsage().IsMatch(line));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NLightning.sln")))
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

    /// <summary>
    /// A <c>using</c> (global or static) of an LNUnit namespace, or a qualified name of one in code (comments start
    /// with //). <c>lnunit.lnd</c>'s global LND namespaces (<c>Lnrpc</c>, ...) are not matched: inside
    /// <c>NLightning.*</c> namespaces a <c>using Lnrpc;</c> binds to our client, and the deps check above proves the
    /// package itself is gone.
    /// </summary>
    [GeneratedRegex(@"^\s*((global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?(global::)?LNUnit\b|[^/]*\bLNUnit\.(Setup|LND|Fixtures|Models)\b)")]
    private static partial Regex LnUnitUsage();

    /// <summary>A package reference, central version or update of LNUnit or one of its packages.</summary>
    [GeneratedRegex(@"Package(Reference|Version)\s+(Include|Update)=""LNUnit(\.[A-Za-z]+)?""", RegexOptions.IgnoreCase)]
    private static partial Regex LnUnitPackage();
}