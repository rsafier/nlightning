namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;
using ValueObjects;

/// <summary>
/// The wallet's PSBT and lease surface for external spenders (LND's walletrpc <c>ListUnspent</c>, <c>LeaseOutput</c>,
/// <c>ReleaseOutput</c>, <c>ListLeases</c>, <c>FundPsbt</c>, <c>FinalizePsbt</c>, <c>PublishTransaction</c>; NL-1184).
/// </summary>
/// <remarks>
/// A lease is an <see cref="IFeeInputSelector"/> reservation whose purpose names the lease (so it survives a restart and
/// no other spend, channel funding or fee input selection takes the output); it ends at its expiration, when released,
/// or when its outputs are spent in a block. Only leased wallet outputs are ever signed
/// (<see cref="FinalizePsbtAsync"/>), through <c>ILightningSigner.SignWalletTransaction</c>'s reserved-inputs rule, and
/// an output our own pending broadcast spends (a funding, a sweep) is never leased.
/// </remarks>
public interface IWalletPsbtService
{
    /// <summary>Publishes a child spending the named live wallet mempool output at an ancestor-package target.</summary>
    Task<TxId> BumpOutputAsync(TxId txId, uint index, long feeRatePerKw, long? budgetSat = null,
                             CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Wallet CPFP is unavailable.");
    /// <summary>The wallet outputs free to spend (not leased, reserved, locked to a channel or spent by a pending
    /// broadcast of ours) with <paramref name="minConfirmations"/> to <paramref name="maxConfirmations"/>
    /// confirmations.</summary>
    Task<IReadOnlyList<WalletUnspentOutput>> ListUnspentAsync(uint minConfirmations, uint maxConfirmations,
                                                              CancellationToken cancellationToken = default);

    /// <summary>Leases one wallet output (a lease with the same id is extended).</summary>
    /// <exception cref="Exceptions.WalletPsbtException">Unknown, locked, reserved or leased under another id.</exception>
    Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
                                 CancellationToken cancellationToken = default);

    /// <summary>Leases an output until its confirmed spend reaches the requested depth.</summary>
    Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
        uint releaseAfterSpendConfs, CancellationToken cancellationToken = default) =>
        releaseAfterSpendConfs == 0 ? LeaseAsync(lockId, txId, index, duration, cancellationToken)
        : throw new NotSupportedException("Confirmation-depth leases are unavailable.");

    /// <summary>Ends the lease of one output (the id must match).</summary>
    /// <exception cref="Exceptions.WalletPsbtException">Not leased, or leased under another id.</exception>
    Task ReleaseAsync(byte[] lockId, TxId txId, uint index, CancellationToken cancellationToken = default);

    /// <summary>The leases that have not expired.</summary>
    Task<IReadOnlyList<WalletLease>> ListLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Selects (or takes the given) wallet inputs for the outputs, adds change, leases the inputs.</summary>
    /// <exception cref="Exceptions.WalletPsbtException">The request cannot be funded.</exception>
    Task<PsbtFundResult> FundPsbtAsync(PsbtFundRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs and finalizes the wallet inputs of <paramref name="psbt"/>, each of which must be a leased wallet output; every
    /// other input must already be finalized and carry its UTXO (LND: we are the last signer), and the whole transaction
    /// must then verify.
    /// </summary>
    /// <exception cref="Exceptions.WalletPsbtException">A wallet input is not leased, another input is not finalized or
    /// has no UTXO, or the PSBT does not parse.</exception>
    Task<PsbtFinalizeResult> FinalizePsbtAsync(byte[] psbt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs the wallet inputs of <paramref name="psbt"/> without finalizing anything (LND's <c>SignPsbt</c>, NL-1186):
    /// each wallet input must be a leased wallet output, other inputs are left as they are; an input already finalized
    /// is skipped. A PSBT without a wallet input comes back unchanged with no signed input.
    /// </summary>
    /// <exception cref="Exceptions.WalletPsbtException">A wallet input is not leased, a P2TR wallet input lacks the
    /// other spent outputs, or the PSBT does not parse.</exception>
    Task<PsbtSignResult> SignPsbtAsync(byte[] psbt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a signed transaction. Its wallet inputs must be leased. One that spends only wallet outputs is stored
    /// and rebroadcast after every block until it confirms, like <c>withdraw</c>; one that also spends others' outputs
    /// (a collaborative transaction, NL-1186) is stored and rebroadcast too, but booked only by its wallet movements;
    /// one that spends no wallet output is sent once.
    /// </summary>
    /// <returns>Whether bitcoind accepted it now.</returns>
    Task<bool> PublishAsync(byte[] rawTransaction, string? label, CancellationToken cancellationToken = default);
}