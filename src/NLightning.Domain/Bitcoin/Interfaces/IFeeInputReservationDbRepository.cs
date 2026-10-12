namespace NLightning.Domain.Bitcoin.Interfaces;

using ValueObjects;
using Wallet.Models;

/// <summary>
/// The persisted fee input reservations (BOLT 5 plan O7-T1, migration <c>AddFeeInputReservations</c>).
/// </summary>
public interface IFeeInputReservationDbRepository
{
    /// <summary>Stages a reservation with its inputs; an outpoint already reserved fails the save (primary key).</summary>
    void Add(FeeInputReservation reservation, DateTimeOffset createdAt);

    /// <summary>Stages the deletion of a reservation and its inputs; false when there is none.</summary>
    Task<bool> DeleteAsync(Guid reservationId);

    /// <summary>The reservation with that id, with its inputs, or null.</summary>
    Task<FeeInputReservation?> GetByIdAsync(Guid reservationId);

    /// <summary>Every reservation with its inputs, oldest first.</summary>
    Task<IReadOnlyList<FeeInputReservation>> GetAllAsync();

    /// <summary>Every reserved outpoint with its reservation (loaded into the UTXO memory repository at startup).</summary>
    Task<IReadOnlyList<(TxId TxId, uint Index, Guid ReservationId)>> GetReservedOutpointsAsync();
}