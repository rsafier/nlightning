namespace NLightning.Domain.Accounting.Constants;

/// <summary>
/// The column limits of the financial books' schema (migration <c>AddAccountingFinancial</c>, plan A3-T0) and of the
/// labels and tags at the source. The validators of the A3 lanes check against the same numbers.
/// </summary>
public static class AccountingSchemaLimits
{
    /// <summary>A label: at most 256 UTF-8 bytes (the column holds 256 characters, never fewer than the bytes).</summary>
    public const int LabelMaxBytes = 256;

    /// <summary>The canonical <c>k=v</c> tag list: at most 1 KiB.</summary>
    public const int TagsMaxBytes = 1024;

    /// <summary>At most 16 tags per row (A3-T1).</summary>
    public const int MaxTags = 16;

    /// <summary>A tag key, <c>[a-z0-9_.-]{1,32}</c>.</summary>
    public const int TagKeyMaxLength = 32;

    /// <summary>A tag value: at most 128 bytes.</summary>
    public const int TagValueMaxBytes = 128;

    /// <summary>A financial account name (a chart name, a rule's or an override's target).</summary>
    public const int AccountNameMaxLength = 256;

    /// <summary>An ISO 4217 currency code.</summary>
    public const int CurrencyLength = 3;

    /// <summary>A period id: <c>YYYY-MM</c> or <c>YYYY-MM-DD..YYYY-MM-DD</c>.</summary>
    public const int PeriodIdMaxLength = 32;

    /// <summary>A rule's label pattern.</summary>
    public const int LabelPatternMaxLength = 256;

    /// <summary>A rule's kind list (comma-separated <c>AccountingEventKind</c> values).</summary>
    public const int RuleKindsMaxLength = 256;

    /// <summary>An operator's note on a rule or an override.</summary>
    public const int NoteMaxLength = 1024;

    /// <summary>The precision of the stored fiat amounts and prices (D-A11: <c>decimal</c>, 8 places).</summary>
    public const int FiatPrecision = 28;

    /// <summary>The scale of the stored fiat amounts and prices.</summary>
    public const int FiatScale = 8;
}