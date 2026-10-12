// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;

/// <summary>
/// One wallet outpoint of a <see cref="FeeInputReservationEntity"/> (migration <c>AddFeeInputReservations</c>). Keyed by
/// the outpoint, so an output is never in two reservations. No foreign key to <c>Utxos</c>: the row outlives the UTXO row
/// the chain monitor deletes when the spend confirms, until the reservation is confirmed or released.
/// </summary>
public class FeeInputReservationInputEntity
{
    public required TxId TransactionId { get; set; }
    public required uint Index { get; set; }
    public required Guid ReservationId { get; set; }
    public required long AmountSats { get; set; }
    public required AddressType AddressType { get; set; }
    public required byte[] ScriptPubKey { get; set; }

    /// <summary>The input's position in the reservation (largest first).</summary>
    public required int Position { get; set; }

    // Default constructor for EF Core
    internal FeeInputReservationInputEntity()
    {
    }
}