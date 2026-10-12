using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The classify part of an AccountingAdmin request (ClientCommand 45, action 5, NL-602 A3-T3): one
/// <c>AccountingClassifyAction</c> with the fields it reads. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingClassifyIpcRequest
{
    /// <summary>The <c>AccountingClassifyAction</c> value.</summary>
    [Key(0)] public int Action { get; set; } = (int)AccountingClassifyAction.RuleList;

    /// <summary>The rule to add, or the candidate of a test.</summary>
    [Key(1)] public AccountingRuleIpcModel? Rule { get; set; }

    /// <summary>The rule of a remove, enable or disable.</summary>
    [Key(2)] public long? RuleId { get; set; }

    /// <summary>The event of a test, a set or an unset.</summary>
    [Key(3)] public string? EventKey { get; set; }

    /// <summary>The override's target account (set).</summary>
    [Key(4)] public string? Account { get; set; }

    /// <summary>The override's note (set).</summary>
    [Key(5)] public string? Note { get; set; }

    /// <summary>Where the unclassified listing starts (after this ledger sequence).</summary>
    [Key(6)] public long AfterLedgerSeq { get; set; }

    /// <summary>How many overrides to skip.</summary>
    [Key(7)] public int Skip { get; set; }

    /// <summary>The most items to return.</summary>
    [Key(8)] public int Limit { get; set; } = 100;

    /// <exception cref="ClientException">An unknown action, or a rule field in the wrong form.</exception>
    public AccountingClassifyClientRequest ToClientRequest()
    {
        if (!Enum.IsDefined(typeof(AccountingClassifyAction), Action))
            throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown classify action {Action}.");

        return new AccountingClassifyClientRequest
        {
            Action = (AccountingClassifyAction)Action,
            Rule = Rule?.ToRule(),
            RuleId = RuleId,
            EventKey = string.IsNullOrWhiteSpace(EventKey) ? null : EventKey.Trim(),
            Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note,
            AfterLedgerSeq = AfterLedgerSeq,
            Skip = Skip,
            Limit = Limit
        };
    }
}

/// <summary>
/// A classification rule on the wire (NL-602 A3-T3, D-A10): ids as hex, kinds as <c>AccountingEventKind</c> values.
/// Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingRuleIpcModel
{
    /// <summary>The storage id (0 for a new rule).</summary>
    [Key(0)] public long Id { get; set; }

    [Key(1)] public int Priority { get; set; }

    /// <summary>The <c>AccountingEventKind</c> values it matches, or null for any.</summary>
    [Key(2)] public List<int>? Kinds { get; set; }

    [Key(3)] public string? LabelPattern { get; set; }
    [Key(4)] public string? TagKey { get; set; }
    [Key(5)] public string? TagValue { get; set; }

    /// <summary>The counterparty node id (66 hex).</summary>
    [Key(6)] public string? Counterparty { get; set; }

    /// <summary>The BOLT 12 offer id (64 hex).</summary>
    [Key(7)] public string? OfferId { get; set; }

    /// <summary>The channel id (64 hex).</summary>
    [Key(8)] public string? ChannelId { get; set; }

    [Key(9)] public string TargetAccount { get; set; } = string.Empty;
    [Key(10)] public bool Enabled { get; set; } = true;
    [Key(11)] public long CreatedAtUnixMilliseconds { get; set; }
    [Key(12)] public string? Description { get; set; }

    public static AccountingRuleIpcModel FromRule(AccountingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new AccountingRuleIpcModel
        {
            Id = rule.Id,
            Priority = rule.Priority,
            Kinds = rule.Kinds?.Select(k => (int)k).ToList(),
            LabelPattern = rule.LabelPattern,
            TagKey = rule.TagKey,
            TagValue = rule.TagValue,
            Counterparty = rule.Counterparty?.ToString(),
            OfferId = rule.OfferId?.ToString(),
            ChannelId = rule.ChannelId?.ToString(),
            TargetAccount = rule.TargetAccount,
            Enabled = rule.Enabled,
            CreatedAtUnixMilliseconds = rule.CreatedAt.ToUnixTimeMilliseconds(),
            Description = rule.Description
        };
    }

    /// <exception cref="ClientException">A kind, node id, offer id or channel id in the wrong form.</exception>
    public AccountingRule ToRule()
    {
        List<AccountingEventKind>? kinds = null;
        if (Kinds is { Count: > 0 })
        {
            kinds = [];
            foreach (var kind in Kinds)
            {
                if (!Enum.IsDefined(typeof(AccountingEventKind), kind))
                    throw new ClientException(ErrorCodes.InvalidOperation, $"Unknown accounting event kind {kind}.");
                if (!kinds.Contains((AccountingEventKind)kind))
                    kinds.Add((AccountingEventKind)kind);
            }
        }

        CompactPubKey? counterparty = null;
        if (!string.IsNullOrWhiteSpace(Counterparty))
        {
            try
            {
                counterparty = new CompactPubKey(Hex(Counterparty, 33, "counterparty"));
            }
            catch (ArgumentException)
            {
                throw new ClientException(ErrorCodes.InvalidOperation,
                                          $"Invalid counterparty '{Counterparty}': expected a 66-hex node id.");
            }
        }

        Hash? offerId = null;
        if (!string.IsNullOrWhiteSpace(OfferId))
            offerId = new Hash(Hex(OfferId, 32, "offer id"));

        ChannelId? channelId = null;
        if (!string.IsNullOrWhiteSpace(ChannelId))
            channelId = new ChannelId(Hex(ChannelId, 32, "channel id"));

        return new AccountingRule(Id, Priority, kinds,
                                  string.IsNullOrEmpty(LabelPattern) ? null : LabelPattern,
                                  string.IsNullOrWhiteSpace(TagKey) ? null : TagKey.Trim(),
                                  string.IsNullOrEmpty(TagValue) ? null : TagValue, counterparty, offerId, channelId,
                                  TargetAccount.Trim(), Enabled,
                                  DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixMilliseconds),
                                  string.IsNullOrWhiteSpace(Description) ? null : Description);
    }

    private static byte[] Hex(string text, int length, string name)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text.Trim());
        }
        catch (FormatException)
        {
            bytes = [];
        }

        if (bytes.Length != length)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"Invalid {name} '{text}': expected {length * 2} hex characters.");

        return bytes;
    }
}