namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// A BOLT 10 seed query name: the seed root, optionally narrowed by the key-value labels <c>l</c> (node id),
/// <c>n</c> (number of records), <c>a</c> (address types) and <c>r</c> (realm), written in that order from the left,
/// each at most once (<c>n10.a2.r0.nodes.lightning.directory</c>).
/// </summary>
/// <param name="SeedRoot">The seed's domain.</param>
/// <param name="Realm">The <c>r</c> condition (realm byte; 0 is Lightning on Bitcoin).</param>
/// <param name="AddressTypes">The <c>a</c> condition (a bit field of BOLT 7 address types, see
/// <see cref="DnsSeedAddressTypes"/>).</param>
/// <param name="NodeIdLabel">The <c>l</c> condition: the bech32 node id (<c>ln1...</c>).</param>
/// <param name="Count">The <c>n</c> condition (number of records wanted).</param>
public sealed record DnsSeedQuery(
    string SeedRoot,
    byte? Realm = null,
    byte? AddressTypes = null,
    string? NodeIdLabel = null,
    int? Count = null)
{
    /// <summary>The longest DNS label, in octets.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>The longest DNS name (dotted, without the final dot).</summary>
    public const int MaxNameLength = 253;

    /// <summary>
    /// The name to query, lower-cased.
    /// </summary>
    /// <exception cref="ArgumentException">A label is empty or longer than 63 octets, or the name is longer than 253
    /// characters.</exception>
    public string ToHostName()
    {
        var labels = new List<string>(5);
        if (NodeIdLabel is not null)
            labels.Add("l" + NodeIdLabel.ToLowerInvariant());
        if (Count is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(Count));
            labels.Add("n" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (AddressTypes is { } addressTypes)
            labels.Add("a" + addressTypes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Realm is { } realm)
            labels.Add("r" + realm.ToString(System.Globalization.CultureInfo.InvariantCulture));

        labels.Add(NormalizeRoot(SeedRoot));
        var name = string.Join('.', labels);
        if (!IsValidDnsName(name, out var reason))
            throw new ArgumentException($"Invalid seed query name '{name}': {reason}", nameof(SeedRoot));

        return name;
    }

    /// <summary>The SRV alias of a seed, <c>_nodes._tcp.&lt;root&gt;</c>.</summary>
    public static string SrvAlias(string root) => "_nodes._tcp." + NormalizeRoot(root);

    /// <summary>The virtual host of a node under a seed, <c>&lt;bech32 node id&gt;.&lt;root&gt;</c>.</summary>
    public static string VirtualHost(string nodeIdBech32, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeIdBech32);
        return nodeIdBech32.ToLowerInvariant() + "." + NormalizeRoot(root);
    }

    /// <summary>
    /// True when <paramref name="name"/> is a DNS name: at most 253 characters, labels of 1 to 63 octets of letters,
    /// digits, <c>-</c> and <c>_</c> (the SRV prefixes), no leading or trailing <c>-</c>. A final dot is allowed.
    /// </summary>
    public static bool IsValidDnsName(string? name, out string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "empty name";
            return false;
        }

        if (name.EndsWith('.'))
            name = name[..^1];
        if (name.Length > MaxNameLength)
        {
            reason = $"longer than {MaxNameLength} characters";
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0)
            {
                reason = "empty label";
                return false;
            }

            if (label.Length > MaxLabelLength)
            {
                reason = $"label '{label}' is longer than {MaxLabelLength} octets";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-')
                                      || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                reason = $"label '{label}' has invalid characters";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return root.Trim().TrimEnd('.').ToLowerInvariant();
    }
}