using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

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

    public static AccountingExportIpcResponse FromClientResponse(AccountingExportClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new AccountingExportIpcResponse
        {
            Format = (int)response.Format,
            Text = response.Chunk.Text,
            NextAfter = response.Chunk.NextAfter,
            HasMore = response.Chunk.HasMore,
            EntryCount = response.Chunk.EntryCount
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
                    Note = l.Note
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
            Verification = verification
        };
    }
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

    /// <summary>Books less node.</summary>
    [Key(4)] public required long DriftMsat { get; init; }

    [Key(5)] public string? Note { get; init; }
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