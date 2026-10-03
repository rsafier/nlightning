namespace NLightning.Domain.Client.Responses;

using Accounting.Books.Export;

/// <summary>
/// One page of an export of the books (<c>ClientCommand.AccountingExport</c>, NL-602 A2).
/// </summary>
/// <param name="Format">The format.</param>
/// <param name="Chunk">The page's text and the cursor of the next page.</param>
public sealed record AccountingExportClientResponse(AccountingExportFormat Format, AccountingExportChunk Chunk)
{
    /// <summary>A financial export's next cursor adjustment (NL-602 A3-T6; pass it back with
    /// <c>Chunk.NextAfter</c>), null for the operational book.</summary>
    public int? NextAfterAdjustment { get; init; }
}