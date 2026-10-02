using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Financial;
using Domain.Client.Responses;

/// <summary>
/// Response for AccountingExport (ClientCommand 44, NL-602 A2): one page of the export's text. Concatenated in order,
/// the pages are the whole document. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingExportIpcResponse
{
    /// <summary>The <c>AccountingExportFormat</c> value.</summary>
    [Key(0)] public required int Format { get; init; }

    /// <summary>The page's text (UTF-8 when written to a file, <c>\n</c> line ends).</summary>
    [Key(1)] public required string Text { get; init; }

    /// <summary>The cursor of the next page (pass it as <c>AfterLedgerSeq</c>).</summary>
    [Key(2)] public required long NextAfter { get; init; }

    /// <summary>Whether more entries may follow.</summary>
    [Key(3)] public required bool HasMore { get; init; }

    /// <summary>How many entries the page read.</summary>
    [Key(4)] public required int EntryCount { get; init; }

    /// <summary>A financial export's next cursor adjustment (NL-602 A3-T6): pass it as <c>AfterAdjustment</c> with
    /// <see cref="NextAfter"/>; null for the operational book.</summary>
    [Key(5)] public int? NextAfterAdjustment { get; init; }

    public static AccountingExportIpcResponse FromClientResponse(AccountingExportClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AccountingExportIpcResponse
        {
            Format = (int)response.Format,
            Text = response.Chunk.Text,
            NextAfter = response.Chunk.NextAfter,
            HasMore = response.Chunk.HasMore,
            EntryCount = response.Chunk.EntryCount,
            NextAfterAdjustment = response.NextAfterAdjustment
        };
    }
}

/// <summary>
/// Response for AccountingAdmin (ClientCommand 45, NL-602 A2): the field of the action is set. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingAdminIpcResponse
{
    /// <summary>The <c>AccountingAdminAction</c> value.</summary>
    [Key(0)] public required int Action { get; init; }

    [Key(1)] public AccountingReconcileIpcResponse? Reconcile { get; init; }

    /// <summary>How many entries a rebuild wrote.</summary>
    [Key(2)] public int? RebuiltEntries { get; init; }

    [Key(3)] public AccountingVerificationIpcResponse? Verification { get; init; }

    /// <summary>The answer of a classify action (action 5, NL-602 A3-T3).</summary>
    [Key(5)] public AccountingClassifyIpcResponse? Classify { get; init; }

    /// <summary>The answer of a <c>prices</c> action (NL-602 A3-T2).</summary>
    [Key(10)] public AccountingPricesIpcResponse? Prices { get; init; }

    // Keys 20-22 are A3-T5's (period close), apart from the other A3 lanes' keys

    /// <summary><c>close list</c>: every period, oldest first.</summary>
    [Key(20)] public List<AccountingPeriodIpcResponse>? Periods { get; init; }

    /// <summary><c>close</c> and <c>close show</c>: the period.</summary>
    [Key(21)] public AccountingPeriodIpcResponse? Period { get; init; }

    /// <summary><c>verify</c>: every closed period checked (null when the daemon does not serve closes).</summary>
    [Key(22)] public List<AccountingPeriodVerificationIpcResponse>? PeriodVerifications { get; init; }

    public static AccountingAdminIpcResponse FromClientResponse(AccountingAdminClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        AccountingReconcileIpcResponse? reconcile = null;
        if (response.Reconcile is { } result)
        {
            reconcile = new AccountingReconcileIpcResponse
            {
                TakenAtUnixMilliseconds = result.TakenAt.ToUnixTimeMilliseconds(),
                BlockHeight = result.BlockHeight,
                LedgerSeq = result.LedgerSeq,
                IsClean = result.IsClean,
                Lines = result.Lines.Select(l => new AccountingReconcileLineIpcResponse
                {
                    Account = (int)l.Account,
                    Name = response.Names[l.Account],
                    BooksMsat = l.BooksMsat,
                    NodeMsat = l.NodeMsat,
                    DriftMsat = l.DriftMsat,
                    Note = l.Note,
                    OutstandingMsat = l.OutstandingMsat
                }).ToList()
            };
        }

        AccountingVerificationIpcResponse? verification = null;
        if (response.Verification is { } verified)
        {
            verification = new AccountingVerificationIpcResponse
            {
                IsIntact = verified.IsIntact,
                VerifiedCount = verified.VerifiedCount,
                TipLedgerSeq = verified.TipLedgerSeq,
                TipHash = Convert.ToHexStringLower(verified.TipHash),
                BreakLedgerSeq = verified.BreakLedgerSeq,
                BreakReason = verified.BreakReason
            };
        }

        return new AccountingAdminIpcResponse
        {
            Action = (int)response.Action,
            Reconcile = reconcile,
            RebuiltEntries = response.RebuiltEntries,
            Verification = verification,
            Classify = response.Classify is { } classify ? AccountingClassifyIpcResponse.FromClientResponse(classify) : null,
            Prices = response.Prices is { } prices ? AccountingPricesIpcResponse.FromClientResponse(prices) : null,
            Periods = response.Periods?.Select(AccountingPeriodIpcResponse.FromReport).ToList(),
            Period = response.Period is { } period ? AccountingPeriodIpcResponse.FromReport(period) : null,
            PeriodVerifications = response.PeriodVerifications?.Select(v => new AccountingPeriodVerificationIpcResponse
            {
                PeriodId = v.PeriodId,
                IsIntact = v.IsIntact,
                DigestMatches = v.DigestMatches,
                SignatureValid = v.SignatureValid,
                ChainHashMatches = v.ChainHashMatches,
                ClosingStateMatches = v.ClosingStateMatches,
                Contiguous = v.Contiguous,
                EntryCount = v.EntryCount,
                ReliefCount = v.ReliefCount,
                OpenLotCount = v.OpenLotCount,
                Problem = v.Problem,
                StrayEntryCount = v.StrayEntryCount
            }).ToList()
        };
    }
}

/// <summary>A period of the financial book and its close (NL-602 A3-T5). Keys are append-only.</summary>
[MessagePackObject]
public sealed class AccountingPeriodIpcResponse
{
    [Key(0)] public required string PeriodId { get; init; }

    /// <summary>The first instant, Unix seconds.</summary>
    [Key(1)] public required long StartUnixSeconds { get; init; }

    /// <summary>The first instant after the period, Unix seconds (exclusive).</summary>
    [Key(2)] public required long EndUnixSeconds { get; init; }

    /// <summary>The <c>AccountingPeriodState</c> value (0 open, 1 closed).</summary>
    [Key(3)] public required int State { get; init; }

    [Key(4)] public long? ClosedAtUnixMilliseconds { get; init; }

    /// <summary>The feed's last ledger sequence the close covers.</summary>
    [Key(5)] public required long LastLedgerSeq { get; init; }

    /// <summary>The feed's chain hash there, 64 hex characters.</summary>
    [Key(6)] public string? ChainHash { get; init; }

    /// <summary>The close digest (D-A13), 64 hex characters.</summary>
    [Key(7)] public string? Digest { get; init; }

    /// <summary>The node key's compact signature of the digest, 128 hex characters.</summary>
    [Key(8)] public string? Signature { get; init; }

    /// <summary>The node id that signed, 66 hex characters.</summary>
    [Key(9)] public string? NodeId { get; init; }

    [Key(10)] public required bool Forced { get; init; }

    /// <summary>The ledger sequence a financial rebuild from this close replays after.</summary>
    [Key(11)] public long? ReplayAfterLedgerSeq { get; init; }

    /// <summary>The financial book's balances over the closed periods at this close.</summary>
    [Key(12)] public List<AccountingPeriodBalanceIpcResponse>? Balances { get; init; }

    [Key(13)] public long? EntryCount { get; init; }
    [Key(14)] public long? ReliefCount { get; init; }
    [Key(15)] public long? OpenLotCount { get; init; }
    [Key(16)] public int? UnvaluedPostings { get; init; }
    [Key(17)] public int? UnclassifiedEntries { get; init; }

    public static AccountingPeriodIpcResponse FromReport(AccountingCloseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var period = report.Period;
        return new AccountingPeriodIpcResponse
        {
            PeriodId = period.PeriodId,
            StartUnixSeconds = period.Start.ToUnixTimeSeconds(),
            EndUnixSeconds = period.End.ToUnixTimeSeconds(),
            State = (int)period.State,
            ClosedAtUnixMilliseconds = period.ClosedAt?.ToUnixTimeMilliseconds(),
            LastLedgerSeq = period.LastLedgerSeq,
            ChainHash = period.ChainHash is { } chainHash ? Convert.ToHexStringLower(chainHash) : null,
            Digest = period.Digest is { } digest ? Convert.ToHexStringLower(digest) : null,
            Signature = period.Signature is { } signature ? Convert.ToHexStringLower(signature) : null,
            NodeId = report.NodeId is { } nodeId ? Convert.ToHexStringLower(nodeId) : null,
            Forced = period.Forced,
            ReplayAfterLedgerSeq = report.ClosingState?.ReplayAfterLedgerSeq,
            Balances = report.ClosingState?.Balances.Select(b => new AccountingPeriodBalanceIpcResponse
            {
                Account = (int)b.Account,
                AccountName = b.AccountName,
                BalanceMsat = b.BalanceMsat,
                FiatAmount = AccountingClosingState.FormatFiat(b.FiatAmount)
            }).ToList(),
            EntryCount = report.EntryCount,
            ReliefCount = report.ReliefCount,
            OpenLotCount = report.OpenLotCount,
            UnvaluedPostings = report.UnvaluedPostings,
            UnclassifiedEntries = report.UnclassifiedEntries
        };
    }
}

/// <summary>One account's balance at a close (NL-602 A3-T5).</summary>
[MessagePackObject]
public sealed class AccountingPeriodBalanceIpcResponse
{
    /// <summary>The <c>AccountRole</c> value.</summary>
    [Key(0)] public required int Account { get; init; }

    /// <summary>The financial account's name.</summary>
    [Key(1)] public string? AccountName { get; init; }

    [Key(2)] public required long BalanceMsat { get; init; }

    /// <summary>The fiat sum of the valued postings, canonical decimal text.</summary>
    [Key(3)] public required string FiatAmount { get; init; }
}

/// <summary>One closed period checked by <c>verify</c> (NL-602 A3-T5).</summary>
[MessagePackObject]
public sealed class AccountingPeriodVerificationIpcResponse
{
    [Key(0)] public required string PeriodId { get; init; }
    [Key(1)] public required bool IsIntact { get; init; }
    [Key(2)] public required bool DigestMatches { get; init; }
    [Key(3)] public required bool SignatureValid { get; init; }
    [Key(4)] public required bool ChainHashMatches { get; init; }
    [Key(5)] public required bool ClosingStateMatches { get; init; }
    [Key(6)] public required bool Contiguous { get; init; }
    [Key(7)] public required long EntryCount { get; init; }
    [Key(8)] public required long ReliefCount { get; init; }
    [Key(9)] public required long OpenLotCount { get; init; }
    [Key(10)] public string? Problem { get; init; }

    /// <summary>Financial entries dated in the period that its close does not hold (a write past the lock).</summary>
    [Key(11)] public long StrayEntryCount { get; init; }
}

/// <summary>The books against a live snapshot (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingReconcileIpcResponse
{
    [Key(0)] public required long TakenAtUnixMilliseconds { get; init; }
    [Key(1)] public required uint BlockHeight { get; init; }

    /// <summary>The books' cursor at the reconcile.</summary>
    [Key(2)] public required long LedgerSeq { get; init; }

    [Key(3)] public required bool IsClean { get; init; }
    [Key(4)] public required List<AccountingReconcileLineIpcResponse> Lines { get; init; }
}

/// <summary>One bucket of a reconcile (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingReconcileLineIpcResponse
{
    /// <summary>The <c>AccountRole</c> value.</summary>
    [Key(0)] public required int Account { get; init; }

    [Key(1)] public required string Name { get; init; }
    [Key(2)] public required long BooksMsat { get; init; }
    [Key(3)] public required long NodeMsat { get; init; }

    /// <summary>Books less node less <see cref="OutstandingMsat"/>: what nothing explains.</summary>
    [Key(4)] public required long DriftMsat { get; init; }

    [Key(5)] public string? Note { get; init; }

    /// <summary>What transactions in flight explain (expected, not a drift; NL-621). 0 from a daemon before it.</summary>
    [Key(6)] public long OutstandingMsat { get; init; }
}

/// <summary>The feed's hash chain walked (NL-602 A2).</summary>
[MessagePackObject]
public sealed class AccountingVerificationIpcResponse
{
    [Key(0)] public required bool IsIntact { get; init; }
    [Key(1)] public required long VerifiedCount { get; init; }

    /// <summary>The last ledger sequence that verified.</summary>
    [Key(2)] public required long TipLedgerSeq { get; init; }

    /// <summary>Its chain hash, 64 hex characters.</summary>
    [Key(3)] public required string TipHash { get; init; }

    /// <summary>The first ledger sequence that does not verify.</summary>
    [Key(4)] public long? BreakLedgerSeq { get; init; }

    [Key(5)] public string? BreakReason { get; init; }
}