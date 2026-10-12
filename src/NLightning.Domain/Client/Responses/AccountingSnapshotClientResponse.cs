namespace NLightning.Domain.Client.Responses;

using Accounting.Models;

/// <summary>
/// The answer to <c>accountingsnapshot</c> (<c>ClientCommand.AccountingSnapshot</c>, NL-602).
/// </summary>
/// <param name="Snapshot">The live balances by bucket.</param>
public sealed record AccountingSnapshotClientResponse(AccountingSnapshot Snapshot);