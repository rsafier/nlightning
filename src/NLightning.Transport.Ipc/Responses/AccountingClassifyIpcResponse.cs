using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Client.Responses;
using Requests;

/// <summary>
/// The classify part of an AccountingAdmin response (ClientCommand 45, action 5, NL-602 A3-T3): the fields of the
/// action are set. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingClassifyIpcResponse
{
    /// <summary>The <c>AccountingClassifyAction</c> value.</summary>
    [Key(0)] public required int Action { get; init; }

    /// <summary>The <c>AccountingProfile</c> value in effect (0 operational, 1 financial).</summary>
    [Key(1)] public required int Profile { get; init; }

    [Key(2)] public List<AccountingRuleIpcModel>? Rules { get; init; }
    [Key(3)] public bool? Changed { get; init; }
    [Key(4)] public AccountingClassifyTestIpcResponse? Test { get; init; }
    [Key(5)] public AccountingOverrideIpcModel? Override { get; init; }
    [Key(6)] public List<AccountingOverrideIpcModel>? Overrides { get; init; }
    [Key(7)] public List<AccountingUnclassifiedIpcItem>? Unclassified { get; init; }

    /// <summary>The unclassified listing's next cursor.</summary>
    [Key(8)] public long NextAfter { get; init; }

    /// <summary>Whether the unclassified listing has more entries to look at.</summary>
    [Key(9)] public bool HasMore { get; init; }

    /// <summary>How many entries the unclassified page looked at.</summary>
    [Key(10)] public int Scanned { get; init; }

    [Key(11)] public List<string> Warnings { get; set; } = [];

    public static AccountingClassifyIpcResponse FromClientResponse(AccountingClassifyClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AccountingClassifyIpcResponse
        {
            Action = (int)response.Action,
            Profile = (int)response.Profile,
            Rules = response.Rules?.Select(AccountingRuleIpcModel.FromRule).ToList(),
            Changed = response.Changed,
            Test = response.Test is { } test ? AccountingClassifyTestIpcResponse.From(test) : null,
            Override = response.Override is { } o ? AccountingOverrideIpcModel.From(o) : null,
            Overrides = response.Overrides?.Select(AccountingOverrideIpcModel.From).ToList(),
            Unclassified = response.Unclassified?.Items.Select(AccountingUnclassifiedIpcItem.From).ToList(),
            NextAfter = response.Unclassified?.NextAfter ?? 0,
            HasMore = response.Unclassified?.HasMore ?? false,
            Scanned = response.Unclassified?.Scanned ?? 0,
            Warnings = response.Warnings.ToList()
        };
    }
}

/// <summary>One event classified by <c>classify rule test</c> (NL-602 A3-T3).</summary>
[MessagePackObject]
public sealed class AccountingClassifyTestIpcResponse
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required string EventKey { get; init; }

    /// <summary>The <c>AccountingEventKind</c> value.</summary>
    [Key(2)] public required int Kind { get; init; }

    [Key(3)] public required long OccurredAtUnixMilliseconds { get; init; }

    /// <summary>The account of the classifiable lines, or null when the entry has none.</summary>
    [Key(4)] public string? Account { get; init; }

    /// <summary>The <c>AccountingClassificationSource</c> value (1 default, 2 rule, 3 override).</summary>
    [Key(5)] public required int Source { get; init; }

    [Key(6)] public long? RuleId { get; init; }
    [Key(7)] public required bool IsUnclassified { get; init; }
    [Key(8)] public required string Reason { get; init; }
    [Key(9)] public List<long> TimedOutRuleIds { get; set; } = [];
    [Key(10)] public List<AccountingClassifyLineIpcResponse> Lines { get; set; } = [];
    [Key(11)] public bool? CandidateMatches { get; init; }
    [Key(12)] public bool CandidateTimedOut { get; init; }

    public static AccountingClassifyTestIpcResponse From(AccountingClassifyTestResult test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var classification = test.Classification;
        return new AccountingClassifyTestIpcResponse
        {
            LedgerSeq = test.LedgerSeq,
            EventKey = test.EventKey,
            Kind = (int)test.Kind,
            OccurredAtUnixMilliseconds = test.OccurredAt.ToUnixTimeMilliseconds(),
            Account = classification.Account,
            Source = (int)classification.Source,
            RuleId = classification.RuleId,
            IsUnclassified = classification.IsUnclassified,
            Reason = classification.Reason,
            TimedOutRuleIds = classification.TimedOutRuleIds.ToList(),
            Lines = test.Lines.Select(AccountingClassifyLineIpcResponse.From).ToList(),
            CandidateMatches = test.CandidateMatches,
            CandidateTimedOut = test.CandidateTimedOut
        };
    }
}

/// <summary>One financial line of a tested entry (NL-602 A3-T3).</summary>
[MessagePackObject]
public sealed class AccountingClassifyLineIpcResponse
{
    /// <summary>The operational <c>AccountRole</c> value.</summary>
    [Key(0)] public required int Role { get; init; }

    [Key(1)] public required long AmountMsat { get; init; }
    [Key(2)] public required string AccountName { get; init; }

    public static AccountingClassifyLineIpcResponse From(AccountingPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        return new AccountingClassifyLineIpcResponse
        {
            Role = (int)posting.Account,
            AmountMsat = posting.AmountMsat,
            AccountName = posting.AccountName ?? posting.Account.ToString()
        };
    }
}

/// <summary>A manual reclassification (NL-602 A3-T3).</summary>
[MessagePackObject]
public sealed class AccountingOverrideIpcModel
{
    [Key(0)] public required string EventKey { get; init; }
    [Key(1)] public required string Account { get; init; }
    [Key(2)] public string? Note { get; init; }
    [Key(3)] public required long CreatedAtUnixMilliseconds { get; init; }

    public static AccountingOverrideIpcModel From(AccountingOverride accountingOverride)
    {
        ArgumentNullException.ThrowIfNull(accountingOverride);
        return new AccountingOverrideIpcModel
        {
            EventKey = accountingOverride.EventKey,
            Account = accountingOverride.Account,
            Note = accountingOverride.Note,
            CreatedAtUnixMilliseconds = accountingOverride.CreatedAt.ToUnixTimeMilliseconds()
        };
    }
}

/// <summary>An entry that goes to an unclassified account (NL-602 A3-T3).</summary>
[MessagePackObject]
public sealed class AccountingUnclassifiedIpcItem
{
    [Key(0)] public required long LedgerSeq { get; init; }
    [Key(1)] public required string EventKey { get; init; }

    /// <summary>The <c>AccountingEventKind</c> value.</summary>
    [Key(2)] public required int Kind { get; init; }

    [Key(3)] public required long OccurredAtUnixMilliseconds { get; init; }
    [Key(4)] public required long AmountMsat { get; init; }
    [Key(5)] public required string Account { get; init; }
    [Key(6)] public required string Reason { get; init; }
    [Key(7)] public string? Label { get; init; }

    public static AccountingUnclassifiedIpcItem From(AccountingUnclassifiedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AccountingUnclassifiedIpcItem
        {
            LedgerSeq = item.LedgerSeq,
            EventKey = item.EventKey,
            Kind = (int)item.Kind,
            OccurredAtUnixMilliseconds = item.OccurredAt.ToUnixTimeMilliseconds(),
            AmountMsat = item.AmountMsat,
            Account = item.Account,
            Reason = item.Reason,
            Label = item.Label
        };
    }
}