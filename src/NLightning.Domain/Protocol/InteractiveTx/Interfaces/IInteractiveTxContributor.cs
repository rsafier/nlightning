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

    /// <summary>
    /// Returns to the wallet the outputs our side added to <paramref name="discarded"/>, a signed attempt (a splice or
    /// one of its RBF siblings, or a dual-funded open's RBF candidate) that can no longer confirm: the transaction that
    /// conflicts with it (a commitment of the funding it spends, or the sibling that locked) is irrevocably confirmed
    /// (NL-492, splicing plan §3.6; SPR-T2 for the siblings of a locked splice). Outputs in
    /// <paramref name="keptOutpoints"/> (spent by the confirmed transaction, e.g. an input the locked RBF sibling re-added)
    /// stay reserved. Works from the attempt alone, without the in-memory contribution (after a restart the
    /// reservation is found by outpoint). Idempotent; returns how many outputs were released. Call it only after the
    /// conflict is irrevocable: releasing earlier (IT-ABT-01) could hand the outputs to another spend while the
    /// discarded attempt may still confirm after a reorg.
    /// </summary>
    Task<int> ReleaseDiscardedAsync(ConstructedInteractiveTx discarded,
                                    IReadOnlyCollection<(TxId TxId, uint Vout)> keptOutpoints,
                                    CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("NL-492 (wave spr)");
}