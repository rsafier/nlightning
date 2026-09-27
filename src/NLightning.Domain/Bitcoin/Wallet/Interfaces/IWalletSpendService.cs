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
}