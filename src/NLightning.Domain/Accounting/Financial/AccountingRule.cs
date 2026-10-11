namespace NLightning.Domain.Accounting.Financial;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// A classification rule of the financial book (<c>AccountingRules</c>, D-A10): ordered by <see cref="Priority"/> (then
/// <see cref="Id"/>), the first enabled rule whose every set match field matches an entry sends it to
/// <see cref="TargetAccount"/>. A null match field matches anything.
/// </summary>
/// <param name="Id">The storage id (0 until saved).</param>
/// <param name="Priority">Lower first.</param>
/// <param name="Kinds">The event kinds it matches, or null for any.</param>
/// <param name="LabelPattern">A regular expression on the label (run with <c>RegexOptions.NonBacktracking</c> and a
/// 100 ms timeout), or null.</param>
/// <param name="TagKey">A tag key the entry must carry, or null.</param>
/// <param name="TagValue">A glob on that tag's value, or null for any value.</param>
/// <param name="Counterparty">The counterparty node id, or null.</param>
/// <param name="OfferId">The BOLT 12 offer id, or null.</param>
/// <param name="ChannelId">The channel, or null.</param>
/// <param name="TargetAccount">The financial account name the entry goes to.</param>
/// <param name="Enabled">Disabled rules are kept but never match.</param>
/// <param name="CreatedAt">When it was added.</param>
/// <param name="Description">The operator's note, or null.</param>
public sealed record AccountingRule(
    long Id,
    int Priority,
    IReadOnlyList<AccountingEventKind>? Kinds,
    string? LabelPattern,
    string? TagKey,
    string? TagValue,
    CompactPubKey? Counterparty,
    Hash? OfferId,
    ChannelId? ChannelId,
    string TargetAccount,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string? Description = null);