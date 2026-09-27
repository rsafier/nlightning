namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Bitcoin.ValueObjects;
using Bitcoin.Wallet.Models;
using Models;

/// <summary>
/// Chooses and signs what our wallet adds to an interactive-tx negotiation (splicing plan §3.9). Implemented by
/// <c>WalletInteractiveTxContributor</c> in Application (lane IT-B, IT2-T3) over the fee-input reservations
/// (<c>IFeeInputSelector</c>) and <c>ILightningSigner.SignWalletTransaction(tx, reservationId, ...)</c>.
/// </summary>
public interface IInteractiveTxContributor
{
    /// <summary>
    /// Reserves wallet outputs for <paramref name="request"/> and returns the inputs and outputs to add (change above
    /// dust). The reservation is persisted, so a restart never hands the same outputs to another spend.
    /// </summary>
    /// <exception cref="Exceptions.InsufficientFundsException">The wallet cannot fund the request.</exception>
    Task<InteractiveTxContribution> ContributeAsync(InteractiveTxContributionRequest request,
                                                    CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the reservation of an abandoned negotiation (<c>tx_abort</c> or a disconnection before our
    /// <c>tx_signatures</c>). Never call it after our <c>tx_signatures</c> were sent: the transaction may still confirm
    /// (IT-ABT-01). A contribution without a reservation is ignored.
    /// </summary>
    Task ReleaseAsync(InteractiveTxContribution contribution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs our inputs of <paramref name="transaction"/> (SIGHASH_ALL only, IT-SIG-01).
    /// </summary>
    /// <param name="transaction">The constructed transaction.</param>
    /// <param name="contribution">Our contribution; only its reserved inputs are signed.</param>
    /// <param name="otherSpentOutputs">The outputs spent by the inputs we did not add (the peer's and the shared
    /// input), which a P2TR signature commits to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Our witnesses in ascending <c>serial_id</c> order of our inputs, as <c>tx_signatures</c> carries
    /// them.</returns>
    Task<IReadOnlyList<Witness>> SignAsync(ConstructedInteractiveTx transaction, InteractiveTxContribution contribution,
                                           IReadOnlyList<SpentOutput> otherSpentOutputs,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// An input of the negotiated transaction (or of a transaction that double-spent it) is spent on chain: the
    /// reservation kept after our <c>tx_signatures</c> can be settled (IT-ABT-01). Returns false while the wallet has
    /// not seen the spend yet (call again on a later block).
    /// </summary>
    Task<bool> ConfirmAsync(InteractiveTxContribution contribution, CancellationToken cancellationToken = default);
}