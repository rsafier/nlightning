using System.Text;

namespace NLightning.Domain.Accounting.Labels;

using Constants;

/// <summary>
/// The shared checks of an operator's label and tags at the source (NL-602 A3-T1, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2 and §9): the client checks them before it sends a request and the daemon
/// again before anything is stored, with the same rules and messages.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A label is at most <see cref="AccountingSchemaLimits.LabelMaxBytes"/> UTF-8 bytes with no control character
/// (an empty label is no label).</item>
/// <item>At most <see cref="AccountingSchemaLimits.MaxTags"/> tags, each <c>key=value</c>: the key
/// <c>[a-z0-9_.-]{1,32}</c>, the value at most <see cref="AccountingSchemaLimits.TagValueMaxBytes"/> UTF-8 bytes with no
/// control character (it may be empty and may hold <c>=</c>); a key at most once; the canonical list (sorted by key,
/// one <c>key=value</c> per line) at most <see cref="AccountingSchemaLimits.TagsMaxBytes"/> bytes.</item>
/// </list>
/// Control characters are refused so a label or tag can never break the canonical list's lines, a CLI table or an
/// export line.
/// </remarks>
public static class SourceLabelRules
{
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    /// <summary>
    /// Why <paramref name="label"/> is not a valid label, or null when it is (null and empty are valid: no label).
    /// </summary>
    public static string? ValidateLabel(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return null;

        if (ContainsControlCharacter(label))
            return "The label must not contain control characters.";

        var bytes = GetUtf8ByteCount(label);
        if (bytes is null)
            return "The label is not valid Unicode.";

        return bytes > AccountingSchemaLimits.LabelMaxBytes
                   ? $"The label is {bytes} bytes; at most {AccountingSchemaLimits.LabelMaxBytes} UTF-8 bytes are allowed."
                   : null;
    }

    /// <summary>
    /// Why <paramref name="key"/> is not a valid tag key (<c>[a-z0-9_.-]{1,32}</c>), or null when it is.
    /// </summary>
    public static string? ValidateTagKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "A tag key must not be empty.";

        if (key.Length > AccountingSchemaLimits.TagKeyMaxLength)
            return $"The tag key '{Shorten(key)}' is longer than {AccountingSchemaLimits.TagKeyMaxLength} characters.";

        foreach (var c in key)
        {
            if (!IsKeyCharacter(c))
                return $"The tag key '{Shorten(key)}' may only hold a-z, 0-9, '_', '.' and '-'.";
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="value"/> is not a valid tag value, or null when it is (empty is valid).
    /// </summary>
    public static string? ValidateTagValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (ContainsControlCharacter(value))
            return $"The value of tag '{key}' must not contain control characters.";

        var bytes = GetUtf8ByteCount(value);
        if (bytes is null)
            return $"The value of tag '{key}' is not valid Unicode.";

        return bytes > AccountingSchemaLimits.TagValueMaxBytes
                   ? $"The value of tag '{key}' is {bytes} bytes; at most {AccountingSchemaLimits.TagValueMaxBytes} "
                   + "UTF-8 bytes are allowed."
                   : null;
    }

    /// <summary>
    /// Parses one <c>key=value</c> tag (split at the first <c>=</c>) and checks both halves.
    /// </summary>
    /// <returns>True with the tag, or false with <paramref name="error"/> set.</returns>
    public static bool TryParseTag(string? text, out SourceTag tag, out string? error)
    {
        tag = default;
        if (string.IsNullOrEmpty(text))
        {
            error = "A tag must be key=value.";
            return false;
        }

        var separator = text.IndexOf('=');
        if (separator < 0)
        {
            error = $"The tag '{Shorten(text)}' must be key=value.";
            return false;
        }

        var key = text[..separator];
        var value = text[(separator + 1)..];
        error = ValidateTagKey(key) ?? ValidateTagValue(key, value);
        if (error is not null)
            return false;

        tag = new SourceTag(key, value);
        return true;
    }

    /// <summary>A character of a tag key: <c>[a-z0-9_.-]</c>.</summary>
    public static bool IsKeyCharacter(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-';

    internal static int? GetUtf8ByteCount(string text)
    {
        try
        {
            return s_strictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            // A lone surrogate: not text that survives a round trip through UTF-8
            return null;
        }
    }

    private static bool ContainsControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }

    private static string Shorten(string text)
    {
        var printable = new string(text.Select(c => char.IsControl(c) ? '?' : c).Take(40).ToArray());
        return text.Length > 40 ? printable + "..." : printable;
    }
}