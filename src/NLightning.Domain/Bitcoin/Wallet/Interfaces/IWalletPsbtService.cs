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
    /// <summary>The wallet outputs free to spend (not leased, reserved, locked to a channel or spent by a pending
    /// broadcast of ours) with <paramref name="minConfirmations"/> to <paramref name="maxConfirmations"/>
    /// confirmations.</summary>
    Task<IReadOnlyList<WalletUnspentOutput>> ListUnspentAsync(uint minConfirmations, uint maxConfirmations,
                                                              CancellationToken cancellationToken = default);

    /// <summary>Leases one wallet output (a lease with the same id is extended).</summary>
    /// <exception cref="Exceptions.WalletPsbtException">Unknown, locked, reserved or leased under another id.</exception>
    Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
                                 CancellationToken cancellationToken = default);

    /// <summary>Ends the lease of one output (the id must match).</summary>
    /// <exception cref="Exceptions.WalletPsbtException">Not leased, or leased under another id.</exception>
    Task ReleaseAsync(byte[] lockId, TxId txId, uint index, CancellationToken cancellationToken = default);

    /// <summary>The leases that have not expired.</summary>
    Task<IReadOnlyList<WalletLease>> ListLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Selects (or takes the given) wallet inputs for the outputs, adds change, leases the inputs.</summary>
    /// <exception cref="Exceptions.WalletPsbtException">The request cannot be funded.</exception>
    Task<PsbtFundResult> FundPsbtAsync(PsbtFundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Signs every input of <paramref name="psbt"/>, each of which must be a leased wallet output.</summary>
    /// <exception cref="Exceptions.WalletPsbtException">An input is not a leased wallet output, or the PSBT does not
    /// parse.</exception>
    Task<PsbtFinalizeResult> FinalizePsbtAsync(byte[] psbt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a signed transaction. One that spends wallet outputs must spend leased ones only (it is then stored
    /// and rebroadcast after every block until it confirms, like <c>withdraw</c>); one that spends none is sent once.
    /// </summary>
    /// <returns>Whether bitcoind accepted it now.</returns>
    Task<bool> PublishAsync(byte[] rawTransaction, string? label, CancellationToken cancellationToken = default);
}