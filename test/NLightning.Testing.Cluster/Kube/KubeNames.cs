using System.Text.RegularExpressions;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// Kubernetes name rules the harness relies on.
/// </summary>
public static partial class KubeNames
{
    /// <summary>The longest DNS-1123 label (namespace, service and pod names).</summary>
    public const int MaxLabelLength = 63;

    /// <summary>
    /// The longest workload name: a StatefulSet's pods carry <c>controller-revision-hash=&lt;name&gt;-&lt;10 chars&gt;</c>,
    /// a label value of at most 63 characters.
    /// </summary>
    public const int MaxWorkloadNameLength = 52;

    /// <summary>The longest label value.</summary>
    public const int MaxLabelValueLength = 63;

    /// <summary>Whether <paramref name="name"/> is a DNS-1123 label (lower case, digits, '-', alphanumeric ends).</summary>
    public static bool IsDns1123Label(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLabelLength && Dns1123Label().IsMatch(name);

    /// <summary>Whether <paramref name="value"/> is a valid label value (empty included).</summary>
    public static bool IsLabelValue(string? value) =>
        value is not null && value.Length <= MaxLabelValueLength && (value.Length == 0 || LabelValue().IsMatch(value));

    /// <summary>
    /// Throws when <paramref name="name"/> is not a DNS-1123 label of at most <paramref name="maxLength"/> characters.
    /// </summary>
    public static string RequireDns1123Label(string name, string what, int maxLength = MaxLabelLength)
    {
        if (!IsDns1123Label(name) || name.Length > maxLength)
            throw new ArgumentException(
                $"The {what} '{name}' is not a DNS-1123 label of at most {maxLength} characters "
              + "(lower case letters, digits and '-', starting and ending with a letter or digit)", what);

        return name;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex Dns1123Label();

    [GeneratedRegex("^[A-Za-z0-9]([-A-Za-z0-9_.]*[A-Za-z0-9])?$")]
    private static partial Regex LabelValue();
}