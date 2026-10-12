using System.Text;
using System.Text.RegularExpressions;

namespace NLightning.Domain.Accounting.Financial.Classification;

using Constants;
using Labels;

/// <summary>
/// What a classification rule may hold (NL-602 A3-T3, D-A10), checked before it is stored.
/// </summary>
public static class AccountingRuleValidator
{
    /// <summary>The time one label match may take (D-A10).</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The options of every label pattern (D-A10: linear time, no backtracking).</summary>
    public const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>Whether <paramref name="rule"/> may be stored.</summary>
    /// <returns>True, or false with <paramref name="error"/> set.</returns>
    public static bool TryValidate(AccountingRule rule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(rule);
        error = null;

        if (!FinancialAccountNameRules.TryValidateTarget(rule.TargetAccount, out error))
            return false;

        if (rule.Kinds is { } kinds)
        {
            foreach (var kind in kinds)
            {
                if (!Enum.IsDefined(kind))
                {
                    error = $"Unknown accounting event kind {(int)kind}.";
                    return false;
                }
            }

            if (kinds.Count > 0
             && string.Join(',', kinds.Select(k => ((int)k).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                      .Length > AccountingSchemaLimits.RuleKindsMaxLength)
            {
                error = "Too many kinds in one rule.";
                return false;
            }
        }

        if (rule.LabelPattern is { } pattern && !TryCompile(pattern, RegexTimeout, out _, out error))
            return false;

        if (rule.TagKey is { } key && !IsTagKey(key))
        {
            error = $"Invalid tag key '{key}': 1 to {AccountingSchemaLimits.TagKeyMaxLength} of a-z, 0-9, '_', '.', '-'.";
            return false;
        }

        if (rule.TagValue is { } value)
        {
            if (rule.TagKey is null)
            {
                error = "A tag value pattern needs a tag key.";
                return false;
            }

            if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > AccountingSchemaLimits.TagValueMaxBytes
                                  || value.Any(char.IsControl))
            {
                error = $"A tag value pattern is 1 to {AccountingSchemaLimits.TagValueMaxBytes} bytes without control "
                      + "characters.";
                return false;
            }
        }

        if (rule.Description is { Length: > AccountingSchemaLimits.NoteMaxLength })
        {
            error = $"A rule's description is at most {AccountingSchemaLimits.NoteMaxLength} characters.";
            return false;
        }

        return true;
    }

    /// <summary>Compiles a label pattern the way the engine runs it.</summary>
    /// <returns>True with the regex, or false with <paramref name="error"/> set (empty, too long, invalid, or a
    /// construct the non-backtracking engine refuses, such as a backreference or a lookaround).</returns>
    public static bool TryCompile(string pattern, TimeSpan timeout, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;
        if (pattern.Length == 0 || pattern.Length > AccountingSchemaLimits.LabelPatternMaxLength)
        {
            error = $"A label pattern is 1 to {AccountingSchemaLimits.LabelPatternMaxLength} characters.";
            return false;
        }

        try
        {
            regex = new Regex(pattern, PatternOptions, timeout);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            error = $"Invalid label pattern '{pattern}': {e.Message}";
            return false;
        }
    }

    /// <summary>Whether <paramref name="key"/> is a tag key: <c>[a-z0-9_.-]{1,32}</c> (A3-T1).</summary>
    public static bool IsTagKey(string key) => SourceLabelRules.ValidateTagKey(key) is null;
}