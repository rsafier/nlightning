// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A classification rule of the financial book (<c>AccountingRule</c>, NL-602 A3-T3, D-A10; migration
/// <c>AddAccountingFinancial</c>), managed over IPC 45 (<c>classify rule add/list/remove/test</c>). Read in
/// (<see cref="Priority"/>, <see cref="Id"/>) order; the first enabled rule whose every non-null match column matches
/// sends the entry to <see cref="TargetAccount"/>. Kept in the node's database so it is in its backups.
/// </summary>
public class AccountingRuleEntity
{
    /// <summary>Storage id (identity).</summary>
    public long Id { get; set; }

    /// <summary>Lower first.</summary>
    public required int Priority { get; set; }

    /// <summary>Comma-separated <c>AccountingEventKind</c> values, or null for any kind.</summary>
    public string? Kinds { get; set; }

    /// <summary>A regular expression on the label, or null.</summary>
    public string? LabelPattern { get; set; }

    /// <summary>A tag key the entry must carry, or null.</summary>
    public string? TagKey { get; set; }

    /// <summary>A glob on that tag's value, or null for any value.</summary>
    public string? TagValue { get; set; }

    /// <summary>The counterparty node id, or null.</summary>
    public CompactPubKey? Counterparty { get; set; }

    /// <summary>The BOLT 12 offer id, or null.</summary>
    public Hash? OfferId { get; set; }

    /// <summary>The channel, or null.</summary>
    public ChannelId? ChannelId { get; set; }

    /// <summary>The financial account name.</summary>
    public required string TargetAccount { get; set; }

    public required bool Enabled { get; set; }

    /// <summary>When it was added (UTC ticks).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>The operator's note, or null.</summary>
    public string? Description { get; set; }

    // Default constructor for EF Core
    internal AccountingRuleEntity()
    {
    }
}