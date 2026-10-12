using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>
/// Which xunit test or test collection a run was started from, read from <see cref="TestContext.Current"/>: a run
/// started inside a test body belongs to that test, one started by a class or collection fixture to that collection,
/// one started outside xunit (or by an assembly fixture) to neither. It names the dump folder and decides which runs a
/// failed test's hook dumps.
/// </summary>
/// <param name="TestId">The xunit test's unique id, or null outside a test.</param>
/// <param name="TestLabel">The test as a folder name (<c>Class.Method</c>, a hash for theory rows), or null.</param>
/// <param name="CollectionId">The test collection's unique id, or null outside a collection.</param>
/// <param name="CollectionLabel">The collection as a folder name, or null.</param>
public sealed partial record ClusterTestScope(string? TestId, string? TestLabel, string? CollectionId,
                                              string? CollectionLabel)
{
    /// <summary>The longest folder name a label becomes (the rest is a hash).</summary>
    public const int MaxLabelLength = 150;

    /// <summary>Outside xunit.</summary>
    public static ClusterTestScope None { get; } = new(null, null, null, null);

    /// <summary>The folder name of a dump: the test, else <c>fixture-&lt;collection&gt;</c>, else <c>run</c>.</summary>
    public string Label => TestLabel ?? (CollectionLabel is { } c ? $"fixture-{c}" : "run");

    /// <summary>The scope of the code running now.</summary>
    public static ClusterTestScope Current()
    {
        try
        {
            var context = TestContext.Current;
            var test = context.Test;
            var collection = context.TestCollection;
            string? testLabel = null;
            if (test is not null)
            {
                var display = test.TestDisplayName;
                var className = test.TestCase.TestClassName;
                if (className is not null && display.StartsWith(className + ".", StringComparison.Ordinal))
                    display = display[(className.LastIndexOf('.') + 1)..];
                testLabel = ToLabel(display);
            }

            return new ClusterTestScope(test?.UniqueID, testLabel, collection?.UniqueID,
                                        collection is null ? null : ToLabel(collection.TestCollectionDisplayName));
        }
        catch (Exception)
        {
            // No xunit context (a console host): the run belongs to no test
            return None;
        }
    }

    /// <summary>
    /// Whether a failure in this scope concerns a run started in <paramref name="run"/>'s scope: the same test, or a
    /// run of no test (a fixture's) in the same collection or in none (an assembly fixture's, or outside xunit).
    /// </summary>
    public bool Covers(ClusterTestScope run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.TestId is not null)
            return run.TestId == TestId;

        return run.CollectionId is null || run.CollectionId == CollectionId;
    }

    /// <summary>
    /// <paramref name="text"/> as a folder name: characters outside <c>[A-Za-z0-9._-]</c> become '_', and a text that
    /// changed (theory arguments) or is too long ends with 8 hex characters of its SHA-256 so rows stay apart.
    /// </summary>
    public static string ToLabel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parenthesis = text.IndexOf('(', StringComparison.Ordinal);
        var head = Unsafe().Replace(parenthesis >= 0 ? text[..parenthesis] : text, "_").Trim('_', '.');
        if (head.Length == 0)
            head = "test";

        var needsHash = parenthesis >= 0 || head.Length > MaxLabelLength;
        if (!needsHash)
            return head;

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
        return $"{head[..Math.Min(head.Length, MaxLabelLength - 9)]}-{hash}";
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();
}