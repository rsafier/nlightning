namespace NLightning.Domain.Accounting.Labels;

using Constants;

/// <summary>
/// An operator's label and tags at the source (NL-602 A3-T1, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2): given with
/// <c>--label</c>/<c>--tag k=v</c> on <c>createinvoice</c>, <c>payinvoice</c>, <c>keysend</c>, <c>createoffer</c>,
/// <c>payoffer</c>, <c>withdraw</c> and <c>openchannel</c>, stored on the row (<c>Label</c>, and <c>Tags</c> as
/// <see cref="CanonicalTags"/>) and copied by the accounting writers into the event's details
/// (<see cref="AccountingDetailKeys.Label"/>, <see cref="AccountingDetailKeys.TagPrefix"/><c>&lt;key&gt;</c>), where the
/// financial classification (A3-T3) reads them.
/// </summary>
/// <remarks>
/// Immutable; <see cref="Tags"/> is sorted by key (ordinal) and holds a key at most once. Only <see cref="Create"/> and
/// <see cref="TryCreate"/> check the rules (<see cref="SourceLabelRules"/>); <see cref="FromStored"/> and
/// <see cref="FromDetails"/> read what was stored or written before and never throw.
/// </remarks>
public sealed class SourceLabels
{
    /// <summary>No label and no tags.</summary>
    public static SourceLabels None { get; } = new(null, []);

    private SourceLabels(string? label, IReadOnlyList<SourceTag> tags)
    {
        Label = label;
        Tags = tags;
    }

    /// <summary>The label, or null (never empty).</summary>
    public string? Label { get; }

    /// <summary>The tags, sorted by key.</summary>
    public IReadOnlyList<SourceTag> Tags { get; }

    /// <summary>True without a label and without tags.</summary>
    public bool IsEmpty => Label is null && Tags.Count == 0;

    /// <summary>
    /// The stored form of <see cref="Tags"/>: one <c>key=value</c> per line (<c>\n</c>), sorted by key; null without
    /// tags.
    /// </summary>
    public string? CanonicalTags => Tags.Count == 0 ? null : string.Join('\n', Tags.Select(t => t.ToString()));

    /// <summary>The tags as <c>key=value</c> strings, sorted by key (the IPC form).</summary>
    public IReadOnlyList<string> TagStrings => Tags.Select(t => t.ToString()).ToArray();

    /// <summary>
    /// Checks and builds a label and tags.
    /// </summary>
    /// <param name="label">The label; null or empty for none.</param>
    /// <param name="tags">The tags as <c>key=value</c>, in any order; null for none.</param>
    /// <exception cref="ArgumentException">A rule of <see cref="SourceLabelRules"/> is broken (the message says which).
    /// </exception>
    public static SourceLabels Create(string? label, IEnumerable<string>? tags)
    {
        return TryCreate(label, tags, out var labels, out var error)
                   ? labels
                   : throw new ArgumentException(error, nameof(tags));
    }

    /// <summary>
    /// Checks and builds a label and tags.
    /// </summary>
    /// <returns>True with <paramref name="labels"/>, or false with <paramref name="error"/> set (and
    /// <paramref name="labels"/> <see cref="None"/>).</returns>
    public static bool TryCreate(string? label, IEnumerable<string>? tags, out SourceLabels labels, out string? error)
    {
        labels = None;
        error = SourceLabelRules.ValidateLabel(label);
        if (error is not null)
            return false;

        var parsed = new SortedDictionary<string, SourceTag>(StringComparer.Ordinal);
        foreach (var text in tags ?? [])
        {
            if (!SourceLabelRules.TryParseTag(text, out var tag, out error))
                return false;

            if (!parsed.TryAdd(tag.Key, tag))
            {
                error = $"The tag '{tag.Key}' is given more than once.";
                return false;
            }

            if (parsed.Count > AccountingSchemaLimits.MaxTags)
            {
                error = $"At most {AccountingSchemaLimits.MaxTags} tags are allowed.";
                return false;
            }
        }

        var built = new SourceLabels(string.IsNullOrEmpty(label) ? null : label, parsed.Values.ToArray());
        if (built.CanonicalTags is { } canonical
         && SourceLabelRules.GetUtf8ByteCount(canonical) > AccountingSchemaLimits.TagsMaxBytes)
        {
            error = $"The tags take more than {AccountingSchemaLimits.TagsMaxBytes} bytes together.";
            return false;
        }

        labels = built.IsEmpty ? None : built;
        return true;
    }

    /// <summary>
    /// The label and tags stored on a row (<c>Label</c>, <c>Tags</c> in <see cref="CanonicalTags"/> form). Lenient: a
    /// line that is not <c>key=value</c> with a valid key is skipped, a repeated key keeps its first value. Never throws.
    /// </summary>
    public static SourceLabels FromStored(string? label, string? canonicalTags)
    {
        var tags = new SortedDictionary<string, SourceTag>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(canonicalTags))
        {
            foreach (var line in canonicalTags.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator];
                if (SourceLabelRules.ValidateTagKey(key) is null)
                    tags.TryAdd(key, new SourceTag(key, line[(separator + 1)..]));
            }
        }

        return string.IsNullOrEmpty(label) && tags.Count == 0
                   ? None
                   : new SourceLabels(string.IsNullOrEmpty(label) ? null : label, tags.Values.ToArray());
    }

    /// <summary>
    /// The label and tags an accounting event carries in its details (<see cref="AccountingDetailKeys.Label"/> and
    /// <see cref="AccountingDetailKeys.TagPrefix"/><c>&lt;key&gt;</c>); <see cref="None"/> when it carries none. Never
    /// throws.
    /// </summary>
    public static SourceLabels FromDetails(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
            return None;

        details.TryGetValue(AccountingDetailKeys.Label, out var label);
        var tags = new SortedDictionary<string, SourceTag>(StringComparer.Ordinal);
        foreach (var (key, value) in details)
        {
            if (!key.StartsWith(AccountingDetailKeys.TagPrefix, StringComparison.Ordinal))
                continue;

            var tagKey = key[AccountingDetailKeys.TagPrefix.Length..];
            if (SourceLabelRules.ValidateTagKey(tagKey) is null)
                tags.TryAdd(tagKey, new SourceTag(tagKey, value));
        }

        return string.IsNullOrEmpty(label) && tags.Count == 0
                   ? None
                   : new SourceLabels(string.IsNullOrEmpty(label) ? null : label, tags.Values.ToArray());
    }

    /// <summary>
    /// The detail pairs an accounting writer adds for these labels: <see cref="AccountingDetailKeys.Label"/> (when
    /// set) and one <see cref="AccountingDetailKeys.TagPrefix"/><c>&lt;key&gt;</c> per tag. Empty for <see cref="None"/>.
    /// </summary>
    public IEnumerable<(string Key, string? Value)> ToDetailPairs()
    {
        if (Label is not null)
            yield return (AccountingDetailKeys.Label, Label);

        foreach (var tag in Tags)
            yield return (AccountingDetailKeys.TagPrefix + tag.Key, tag.Value);
    }
}