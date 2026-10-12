namespace NLightning.Domain.Accounting.Labels;

/// <summary>
/// One operator tag (NL-602 A3-T1): a key (<c>[a-z0-9_.-]{1,32}</c>) and a value (at most 128 UTF-8 bytes, may be
/// empty). Build it through <see cref="SourceLabelRules.TryParseTag"/> or <see cref="SourceLabels.Create"/>, which
/// check both.
/// </summary>
public readonly record struct SourceTag(string Key, string Value)
{
    /// <summary>The <c>key=value</c> form, as the CLI takes it and the canonical list stores it.</summary>
    public override string ToString() => $"{Key}={Value}";
}