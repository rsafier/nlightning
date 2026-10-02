using System.Security.Cryptography;
using System.Text;

namespace NLightning.Testing.Cluster.Run;

/// <summary>
/// The run id of a test process (plan R1): <see cref="EnvironmentVariable"/> when set (lanes pass their slug), a
/// random one otherwise. It names the run's namespace and labels everything in it.
/// </summary>
public static class TestRunId
{
    /// <summary>The variable a runner sets to name the run.</summary>
    public const string EnvironmentVariable = "NLTG_TEST_RUN_ID";

    /// <summary>The longest run id (the namespace adds its prefix and must stay a 63-character label).</summary>
    public const int MaxLength = 40;

    /// <summary>The length of a generated id.</summary>
    public const int GeneratedLength = 8;

    /// <summary>
    /// The normalized <paramref name="requested"/> id, or a generated one when it is null or blank.
    /// </summary>
    public static string Resolve(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? Generate() : Normalize(requested);

    /// <summary>The id from <see cref="EnvironmentVariable"/>, or a generated one.</summary>
    public static string FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>A random id of <see cref="GeneratedLength"/> lower-case hex characters.</summary>
    public static string Generate() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(GeneratedLength / 2));

    /// <summary>
    /// Makes <paramref name="value"/> a run id: lower case, every run of characters outside <c>[a-z0-9]</c> becomes one
    /// '-', no '-' at the ends, at most <see cref="MaxLength"/> characters.
    /// </summary>
    /// <exception cref="ArgumentException">Nothing usable is left.</exception>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var id = builder.ToString();
        if (id.Length > MaxLength)
            id = id[..MaxLength];
        id = id.Trim('-');

        if (id.Length == 0)
            throw new ArgumentException($"'{value}' leaves no usable run id", nameof(value));

        return id;
    }

    /// <summary>
    /// The <paramref name="n"/>-th id derived from <paramref name="id"/> (<c>&lt;id&gt;-&lt;n&gt;</c>, <paramref name="id"/>
    /// shortened to keep <see cref="MaxLength"/>): a process that starts several runs under one
    /// <see cref="EnvironmentVariable"/> gives them distinct namespaces. <paramref name="n"/> 1 is <paramref name="id"/>.
    /// </summary>
    public static string WithSuffix(string id, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        id = Normalize(id);
        if (n == 1)
            return id;

        var suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (id.Length + suffix.Length > MaxLength)
            id = id[..(MaxLength - suffix.Length)].TrimEnd('-');

        return id + suffix;
    }
}