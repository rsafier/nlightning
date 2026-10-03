using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>What one xunit v3 XML result file (<c>-xml</c>) says.</summary>
/// <param name="Found">The file exists and parses.</param>
/// <param name="FailedClasses">The classes (<c>test/@type</c>) with a failed test, in the file's order.</param>
/// <param name="FirstError">The first failed test and the first line of its message.</param>
/// <param name="FixtureFailures">
/// Failed tests whose failure is a fixture's (an assembly, collection or class fixture that threw in its constructor
/// or <c>InitializeAsync</c>, or could not be built): xunit v3 reports those as a failure of every test of the
/// collection or class, not as an error.
/// </param>
public sealed record XunitRunResult(
    bool Found,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    int NotRun,
    int Errors,
    IReadOnlyList<string> FailedClasses,
    string? FirstError,
    int FixtureFailures = 0)
{
    public static XunitRunResult Missing { get; } = new(false, 0, 0, 0, 0, 0, 0, [], null);

    /// <summary>Tests ran, none failed and the run reported no error (fixture cleanup, catastrophic).</summary>
    public bool IsGreen => Found && Total > 0 && Failed == 0 && Errors == 0;
}

/// <summary>Reads xunit v3 XML result files.</summary>
public static partial class XunitResults
{
    /// <summary>
    /// Whether a failure <paramref name="message"/> is a fixture's, as xunit v3's <c>FixtureMappingManager</c> words it
    /// ("Collection fixture type 'X' threw in InitializeAsync", "... threw in its constructor", "... had one or more
    /// unresolved constructor arguments", "The following constructor parameters did not have matching fixture data").
    /// </summary>
    public static bool IsFixtureFailure(string? message) =>
        message is not null && FixtureFailure().IsMatch(message);

    /// <summary>The result in <paramref name="path"/>; <see cref="XunitRunResult.Missing"/> when absent or broken.</summary>
    public static XunitRunResult Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return XunitRunResult.Missing;

        try
        {
            return Parse(XDocument.Load(path));
        }
        catch (XmlException)
        {
            return XunitRunResult.Missing;
        }
    }

    /// <summary>The result in <paramref name="document"/> (every <c>assembly</c> element summed).</summary>
    public static XunitRunResult Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var assemblies = document.Descendants("assembly").ToList();
        if (assemblies.Count == 0)
            return XunitRunResult.Missing;

        int Sum(string attribute) =>
            assemblies.Sum(a => int.TryParse((string?)a.Attribute(attribute), NumberStyles.None,
                                             CultureInfo.InvariantCulture, out var n)
                                    ? n
                                    : 0);

        var failed = document.Descendants("test").Where(t => (string?)t.Attribute("result") == "Fail").ToList();
        var classes = failed.Select(ClassOf).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        string? firstError = null;
        if (failed.Count > 0)
        {
            var test = failed[0];
            var message = test.Element("failure")?.Element("message")?.Value.Trim() ?? "";
            var line = message.Split('\n', 2)[0].Trim();
            firstError = $"{(string?)test.Attribute("method") ?? (string?)test.Attribute("name")}: "
                       + (line.Length > 0 ? line : "failed");
        }

        // Errors outside tests (a fixture that failed to clean up, a catastrophic failure) name no test
        var errors = Sum("errors");
        if (firstError is null && errors > 0)
        {
            var error = document.Descendants("error").FirstOrDefault();
            var message = error?.Element("failure")?.Element("message")?.Value.Trim().Split('\n', 2)[0].Trim();
            firstError = $"{(string?)error?.Attribute("type") ?? "error"}"
                       + $"{((string?)error?.Attribute("name") is { } n ? $" {n}" : "")}: {message ?? "error"}";
        }

        var fixtureFailures = failed.Count(t => IsFixtureFailure(t.Element("failure")?.Element("message")?.Value));
        return new XunitRunResult(true, Sum("total"), Sum("passed"), Sum("failed"), Sum("skipped"), Sum("not-run"),
                                  errors, classes, firstError, fixtureFailures);
    }

    [GeneratedRegex(@"(?:Assembly|Collection|Class) fixture type '[^']*' (?:threw in (?:its constructor|InitializeAsync)"
                  + @"|had one or more unresolved constructor arguments|may only define a single public constructor)"
                  + @"|constructor parameters did not have matching fixture data")]
    private static partial Regex FixtureFailure();

    private static string ClassOf(XElement test)
    {
        if ((string?)test.Attribute("type") is { Length: > 0 } type)
            return type;

        // Older result files: the class is the display name minus the method
        var name = (string?)test.Attribute("name") ?? "";
        var method = (string?)test.Attribute("method");
        if (method is not null && name.EndsWith("." + method, StringComparison.Ordinal))
            return name[..^(method.Length + 1)];

        var paren = name.IndexOf('(', StringComparison.Ordinal);
        var bare = paren >= 0 ? name[..paren] : name;
        var dot = bare.LastIndexOf('.');
        return dot > 0 ? bare[..dot] : "";
    }
}