using System.Text.RegularExpressions;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// LNUnit stays confined to the Docker LND backend (<c>Fixtures/Lnd/DockerLndBackend.cs</c>, NL-819): no other test
/// source uses its namespaces, no source of a project that references it uses the LND 0.20 client its
/// <c>lnunit.lnd</c> brings along as global namespaces (<c>Lnrpc</c>, <c>Routerrpc</c>, ...; our client is
/// <c>NLightning.Testing.Lnd</c>), and only Integration.Tests references the package, from its csproj (never from a
/// shared <c>.props</c>/<c>.targets</c>). Reads the sources of the checkout the test assembly was built from (skipped
/// when it runs without one, e.g. a copied output directory; fails when the allowed backend file moved).
/// </summary>
public partial class LnUnitConfinementTests
{
    private const string AllowedSource = "test/NLightning.Integration.Tests/Fixtures/Lnd/DockerLndBackend.cs";
    private const string AllowedProject = "test/NLightning.Integration.Tests/NLightning.Integration.Tests.csproj";

    /// <summary>This file: its test data spells the patterns out.</summary>
    private const string ThisSource = "test/NLightning.Integration.Tests/Fixtures/LnUnitConfinementTests.cs";

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
    public void Given_TheProjectsThatReferenceLnUnit_When_Scanned_Then_NoneUsesLnUnitsLndClient()
    {
        // Arrange: in those projects a `using Lnrpc;` binds to lnunit.lnd's global namespace (LND 0.20 protos), whose
        // RPCs LND 0.21 partly removed; elsewhere (Testing.Lnd and its tests) it resolves to our own client
        var root = FindRepositoryRoot();
        var projectDirectories = EnumerateSources(root, "*.csproj")
                                .Where(path => File.ReadLines(path).Any(line => LnUnitPackage().IsMatch(line)))
                                .Select(path => Path.GetDirectoryName(path)!)
                                .ToList();

        // Act
        var users = projectDirectories
                   .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
                   .Where(path => !IsBuildOutput(root, path))
                   .Where(path => File.ReadLines(path).Any(line => LnUnitLndClientUsage().IsMatch(line)))
                   .Select(path => Relative(root, path))
                   .Where(path => path != ThisSource)
                   .Order(StringComparer.Ordinal)
                   .ToList();

        // Assert
        Assert.NotEmpty(projectDirectories);
        Assert.Empty(users);
    }

    [Theory]
    [InlineData("using Lnrpc;", true)]
    [InlineData("global using Routerrpc;", true)]
    [InlineData("    using static Walletrpc.WalletKit;", true)]
    [InlineData("using Payment = Lnrpc.Payment;", true)]
    [InlineData("        var request = new global::Lnrpc.GetInfoRequest();", true)]
    [InlineData("using NLightning.Testing.Lnd.Lnrpc;", false)]
    [InlineData("using LnrpcHelpers;", false)]
    [InlineData("// using Lnrpc;", false)]
    public void Given_ALine_When_Matched_Then_OnlyLnUnitsLndNamespacesCount(string line, bool matches)
    {
        // Act & Assert
        Assert.Equal(matches, LnUnitLndClientUsage().IsMatch(line));
    }

    [Fact]
    public void Given_TheTestProjects_When_Scanned_Then_OnlyIntegrationTestsReferencesLnUnit()
    {
        // Arrange
        var root = FindRepositoryRoot();

        // Act: the csprojs, and every shared MSBuild file (Directory.Build.props/.targets, Directory.Packages.props, any
        // imported .props/.targets) under test/, src/, tools/ and at the root, where a reference would reach every
        // project
        var projects = EnumerateSources(root, "*.csproj")
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
        Assert.Equal([AllowedProject], projects);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "NLightning.sln")))
                continue;

            // A checkout without the allowed file: the backend moved or was renamed, and the guard must say so
            // instead of going quiet
            Assert.True(File.Exists(Path.Combine(directory.FullName, AllowedSource)),
                        $"{AllowedSource} is gone: update {nameof(LnUnitConfinementTests)}.{nameof(AllowedSource)} to "
                      + "the file that now holds the Docker LND backend's LNUnit code");
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

    /// <summary>
    /// A <c>using</c> (global, static or alias) of one of <c>lnunit.lnd</c>'s global namespaces, or a
    /// <c>global::</c>-qualified name in one; <c>NLightning.Testing.Lnd.*</c> (our client) does not count.
    /// </summary>
    [GeneratedRegex(@"^\s*((global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?(global::)?|[^/]*\bglobal::)"
                  + @"(Lnrpc|Routerrpc|Walletrpc|Invoicesrpc|Signrpc|Chainrpc|Peersrpc|Devrpc|Verrpc|Autopilotrpc"
                  + @"|Watchtowerrpc|Wtclientrpc|Neutrinorpc|Looprpc|Stateservice|Lnclipb)\b")]
    private static partial Regex LnUnitLndClientUsage();

    /// <summary>A package reference, central version or update of LNUnit or one of its packages (LNUnit.LND, ...).</summary>
    [GeneratedRegex("""Package(Reference|Version)\s+(Include|Update)="LNUnit""", RegexOptions.IgnoreCase)]
    private static partial Regex LnUnitPackage();
}