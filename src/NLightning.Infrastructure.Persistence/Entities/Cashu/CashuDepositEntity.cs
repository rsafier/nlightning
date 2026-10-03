// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Cashu;

using Domain.Bitcoin.ValueObjects;

/// <summary>
/// An output paid to an on-chain mint quote's address (<c>CashuDepositModel</c>, NL-997, migration
/// <c>AddCashuProcessorQuotes</c>), keyed by its outpoint.
/// </summary>
/// <remarks>
/// No foreign key to <c>Utxos</c>: the wallet deletes a UTXO when it spends it, and the mint may ask about the
/// deposit after that. No foreign key to <c>CashuQuotes</c> either: the quote row is always saved first, and neither
/// is ever deleted.
/// </remarks>
public class CashuDepositEntity
{
    public required TxId TxId { get; set; }
    public required uint OutputIndex { get; set; }
    public required string QuoteId { get; set; }
    public required long AmountSat { get; set; }
    public required uint BlockHeight { get; set; }

    /// <summary>Stored as UTC ticks.</summary>
    public DateTimeOffset? ReportedAt { get; set; }

    // Default constructor for EF Core
    internal CashuDepositEntity()
    {
    }
}