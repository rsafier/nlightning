namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;

/// <summary>
/// Spends the on-chain wallet to an external address (<c>withdraw</c>, ClientCommand 25).
/// </summary>
/// <remarks>
/// The inputs are confirmed wallet outputs reserved through <see cref="IFeeInputSelector"/> (purpose
/// <c>withdraw</c>), so an output locked to a channel funding, reserved for another spend or spent by one of our
/// pending broadcasts is never taken, and the reservation survives a restart. The wallet keeps the anchors reserve
/// (<see cref="IAnchorReserveService"/>): after the spend, the reserve-backing outputs left plus the spend's own change
/// must cover it (the change counts, as for a channel funding, although it backs the reserve only three blocks after it
/// confirms). The transaction is signed with <c>ILightningSigner.SignWalletTransaction(tx, reservationId, ...)</c>,
/// stored as a <c>BroadcastTransactions</c> row before it is sent, and rebroadcast after every block until it confirms.
/// </remarks>
public interface IWalletSpendService
{
    /// <summary>
    /// Builds, signs, stores and publishes a payment to <see cref="WalletWithdrawRequest.Address"/>.
    /// </summary>
    /// <exception cref="Exceptions.WalletSpendException">The request is invalid (address, network, dust, fee rate) or
    /// the chain processing is halted; nothing was reserved.</exception>
    /// <exception cref="Exceptions.AnchorReserveException">The spend would leave the wallet below the anchors reserve;
    /// nothing was reserved.</exception>
    /// <exception cref="Exceptions.InsufficientFundsException">The confirmed wallet outputs do not cover the amount and
    /// the fee; nothing was reserved.</exception>
    Task<WalletWithdrawResult> WithdrawAsync(WalletWithdrawRequest request,
                                             CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimates the fee of <see cref="WithdrawAsync"/> for <paramref name="request"/> (an amount, not "all") without
    /// reserving, signing or storing anything: the confirmed spendable outputs largest first, as the selector takes
    /// them, plus a change output. The actual fee may differ when the wallet changes in between.
    /// </summary>
    /// <exception cref="Exceptions.WalletSpendException">As <see cref="WithdrawAsync"/>.</exception>
    /// <exception cref="Exceptions.InsufficientFundsException">The confirmed wallet outputs do not cover the amount and
    /// the fee.</exception>
    /// <remarks>The default is for test doubles that do not estimate.</remarks>
    Task<WalletWithdrawEstimate> EstimateWithdrawFeeAsync(WalletWithdrawRequest request,
                                                          CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This wallet spend service does not estimate fees.");

    /// <summary>
    /// Ends the <c>withdraw</c> reservations that no longer hold a spend: those none of whose inputs is still in the
    /// wallet (their spend was processed in a block) are confirmed, and those none of whose inputs a pending
    /// <c>BroadcastTransactions</c> row spends (a crash or a failed save between the reservation and the row) are
    /// released, so their outputs are selectable again. Withdrawals are serialized with it, so an in-flight one is never
    /// touched. <see cref="WithdrawAsync"/> runs it first; the host also runs it once at startup, after the chain monitor
    /// has loaded the wallet's outputs and reservations. When the pending broadcasts cannot be read, nothing is released.
    /// </summary>
    /// <returns>How many reservations were released as orphans.</returns>
    Task<int> ReleaseOrphanedReservationsAsync(CancellationToken cancellationToken = default);
}