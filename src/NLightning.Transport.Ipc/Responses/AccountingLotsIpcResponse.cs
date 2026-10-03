using System.Globalization;
using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Financial.Lots;

/// <summary>
/// What <c>accounting lots import</c> did (ClientCommand 45 key 13, D-A9; NL-602 A3-T4). Amounts are invariant decimal
/// text. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingLotImportIpcResponse
{
    [Key(0)] public required string Currency { get; init; }

    /// <summary>How many lots were stored.</summary>
    [Key(1)] public required int Imported { get; init; }

    /// <summary>Their msat.</summary>
    [Key(2)] public required long ImportedMsat { get; init; }

    /// <summary>Their total cost.</summary>
    [Key(3)] public required string ImportedCost { get; init; }

    /// <summary>The opening balances' msat in the books.</summary>
    [Key(4)] public required long OpeningMsat { get; init; }

    /// <summary>How many lots of an earlier import were replaced.</summary>
    [Key(5)] public required int ReplacedLots { get; init; }

    /// <summary>How many financial entries the rebuild projected.</summary>
    [Key(6)] public required int ProjectedEntries { get; init; }

    /// <summary>The msat the last lot took so the lots hold the opening balances exactly.</summary>
    [Key(7)] public long AdjustedMsat { get; init; }

    public static AccountingLotImportIpcResponse FromResult(AccountingLotImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AccountingLotImportIpcResponse
        {
            Currency = result.Currency,
            Imported = result.Imported,
            ImportedMsat = result.ImportedMsat,
            ImportedCost = result.ImportedCost.ToString(CultureInfo.InvariantCulture),
            OpeningMsat = result.OpeningMsat,
            ReplacedLots = result.ReplacedLots,
            ProjectedEntries = result.ProjectedEntries,
            AdjustedMsat = result.AdjustedMsat
        };
    }
}