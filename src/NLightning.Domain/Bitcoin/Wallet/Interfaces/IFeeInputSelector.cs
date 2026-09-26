namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;
using Money;

/// <summary>
/// Picks confirmed wallet outputs to pay a fee and reserves them (BOLT 5 plan O7-T1): the fee inputs of a CPFP child
/// through an anchor, or of an anchor channel's zero-fee HTLC transactions (O7-T2, O7-T3).
/// </summary>
/// <remarks>
/// A reservation is persisted (tables <c>FeeInputReservations</c>/<c>FeeInputReservationInputs</c>, keyed by outpoint,
/// so an outpoint is never in two reservations) before it is returned, and restored at startup with the wallet's UTXO
/// set, so neither another reservation nor a channel funding picks its outputs, also after a crash. Outputs locked to a
/// channel funding are never picked, nor are outputs that a pending <c>BroadcastTransactions</c> row spends (our own
/// unconfirmed funding, sweep or child: channel funding locks are memory only, so after a restart that row is what
/// keeps its inputs out). Sign the spend with <c>ILightningSigner.SignWalletTransaction(tx, reservationId, ...)</c>,
/// which signs that reservation's wallet inputs only; persist the spend as a broadcast row before publishing it. At
/// startup the chain monitor deletes the reservations none of whose inputs is still in the wallet (their spend was
/// processed in a block). A reorg that unconfirms a confirmed spend does not bring its reservation back: the pending
/// broadcast row keeps the outputs out, and the caller reserves again if it builds another spend.
/// </remarks>
public interface IFeeInputSelector
{
    /// <summary>
    /// Reserves wallet outputs worth at least <paramref name="targetFee"/> plus <paramref name="feeRatePerKw"/> over
    /// <paramref name="extraWeight"/>, the inputs' own weight and, when there is change, a P2WPKH change output.
    /// </summary>
    /// <param name="targetFee">The fee the spend must pay on top of its own weight (for a CPFP child: what the package
    /// still lacks for the parent); may be zero.</param>
    /// <param name="feeRatePerKw">The fee rate, in satoshis per kiloweight, charged for the added weight.</param>
    /// <param name="extraWeight">The weight of the rest of the spend (version, locktime, the non-wallet inputs with their
    /// witnesses, the other outputs), charged at <paramref name="feeRatePerKw"/>.</param>
    /// <param name="purpose">What the inputs pay for, 1 to 128 characters.</param>
    /// <param name="cancellationToken">Cancels the wait for the selector.</param>
    /// <returns>The persisted reservation.</returns>
    /// <exception cref="Exceptions.InsufficientFundsException">The unreserved confirmed wallet outputs cannot cover
    /// it; nothing is reserved.</exception>
    Task<FeeInputReservation> ReserveAsync(LightningMoney targetFee, LightningMoney feeRatePerKw, int extraWeight,
                                           string purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the outputs of a reservation to the wallet: its spend was abandoned or replaced by one that does not use
    /// them. A no-op for an unknown id.
    /// </summary>
    Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a reservation whose spend confirmed: its rows are deleted. The wallet outputs themselves are removed only
    /// by the chain monitor when it processes the block that spends them, so this refuses (returns false, nothing
    /// changed) while any of the reservation's inputs is still in the wallet; call it again after that block. Call it
    /// at a reorg-safe depth: a reorg that unconfirms the spend does not bring the reservation back (the outputs stay
    /// excluded while the spend is a pending broadcast). True for an unknown id (already ended, e.g. by the startup
    /// sweep of reservations whose inputs are all spent).
    /// </summary>
    Task<bool> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>The stored reservation with that id, or null.</summary>
    Task<FeeInputReservation?> GetAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>Every stored reservation (to resume the spends after a restart).</summary>
    Task<IReadOnlyList<FeeInputReservation>> GetAllAsync(CancellationToken cancellationToken = default);
}