namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>
/// xunit v3's simple filters (<c>-class</c>, <c>-class-</c>, <c>-namespace</c>, <c>-namespace-</c>, <c>-method</c>,
/// <c>-method-</c>, <c>-trait</c>, <c>-trait-</c>) evaluated for one test, as the in-process runner documents them:
/// positive filters of one kind are ORed, kinds are ANDed, every negative filter must hold; names match exactly or with a
/// <c>*</c> at the start and/or end. Lets a test check which suite of <see cref="SuiteCatalog"/> runs a class without
/// starting the runner.
/// </summary>
public static class SuiteFilter
{
    /// <summary>Whether the test <paramref name="className"/>.<paramref name="method"/> with
    /// <paramref name="traits"/> passes <paramref name="filters"/> (pairs of option and value).</summary>
    /// <exception cref="ArgumentException">An option without a value, or one this evaluator does not know.</exception>
    public static bool Matches(IReadOnlyList<string> filters, string className, string method,
                               IEnumerable<KeyValuePair<string, string>> traits)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(className);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(traits);
        if (filters.Count % 2 != 0)
            throw new ArgumentException($"'{filters[^1]}' has no value", nameof(filters));

        var traitList = traits.ToList();
        var dot = className.LastIndexOf('.');
        var ns = dot > 0 ? className[..dot] : "";
        var positives = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < filters.Count; i += 2)
        {
            var (option, value) = (filters[i], filters[i + 1]);
            var matched = option.TrimEnd('-') switch
            {
                "-class" => Like(className, value),
                "-namespace" => Like(ns, value),
                "-method" => Like($"{className}.{method}", value),
                "-trait" => HasTrait(traitList, value),
                _ => throw new ArgumentException($"unknown filter '{option}'", nameof(filters))
            };
            if (option.EndsWith('-'))
            {
                if (matched)
                    return false;
            }
            else
            {
                positives[option] = positives.GetValueOrDefault(option) || matched;
            }
        }

        return positives.Values.All(m => m);
    }

    /// <summary><paramref name="text"/> equals <paramref name="pattern"/>, which may start and/or end with '*'.</summary>
    public static bool Like(string text, string pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        var starts = pattern.StartsWith('*');
        var ends = pattern.Length > 1 && pattern.EndsWith('*');
        var core = pattern[(starts ? 1 : 0)..(pattern.Length - (ends ? 1 : 0))];
        return (starts, ends) switch
        {
            (true, true) => text.Contains(core, StringComparison.Ordinal),
            (true, false) => text.EndsWith(core, StringComparison.Ordinal),
            (false, true) => text.StartsWith(core, StringComparison.Ordinal),
            _ => text.Equals(core, StringComparison.Ordinal)
        };
    }

    private static bool HasTrait(List<KeyValuePair<string, string>> traits, string filter)
    {
        var equals = filter.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
            throw new ArgumentException($"trait filter '{filter}' is not name=value", nameof(filter));

        var (name, value) = (filter[..equals], filter[(equals + 1)..]);
        return traits.Any(t => Like(t.Key, name) && Like(t.Value, value));
    }
}