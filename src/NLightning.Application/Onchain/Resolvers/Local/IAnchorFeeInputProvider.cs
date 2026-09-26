namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Models;

/// <summary>
/// The wallet side of an anchors HTLC-timeout/success transaction (BOLT 5 §Generation of HTLC Transactions,
/// B5-HTX-02, plan O7-T3): the zero-fee HTLC transaction is combined with wallet outputs that pay its fee, and the
/// wallet signs those inputs. The resolver never sees a wallet key.
/// </summary>
/// <remarks>
/// <para>The host adapts the wallet's fee-input selector and <c>SignWalletTransaction</c> (O7-T1) to this port. Without
/// one, <see cref="UnavailableAnchorFeeInputProvider"/> selects nothing and the resolver retries every block.</para>
/// <para>Reservations (required of every implementation that selects real outputs): a reservation belongs to one
/// <see cref="AnchorFeeInputOwner"/> (one HTLC output of one commitment) and is <b>persistent</b>: it is written before
/// <see cref="SelectAsync"/> returns (in the provider's own save, as the onion replay store does) and survives a
/// restart, so the wallet never spends a reserved output elsewhere while a transaction that spends it may still be in a
/// mempool. The resolver's round is saved afterwards and may fail; the owner's next <see cref="SelectAsync"/> then
/// <b>replaces</b> the earlier reservation (it releases it first, and may select the same outputs again), so a failed
/// round leaks nothing. The same replacement serves an RBF replacement of the owner's transaction, which conflicts with
/// the one the earlier inputs fund. <see cref="ReleaseAsync"/> ends the owner's reservation: the resolver calls it when
/// the transaction was not built, and once the HTLC output is spent on chain (by any transaction).</para>
/// </remarks>
public interface IAnchorFeeInputProvider
{
    /// <summary>
    /// Selects and reserves, for <paramref name="owner"/> (replacing its earlier reservation, if any), wallet outputs
    /// that pay the fee of a transaction of <paramref name="baseWeight"/> (the HTLC transaction with a change output to
    /// the returned script, before any fee input) plus the inputs' own weights, at <paramref name="feeratePerKw"/>, with
    /// change above dust where the wallet can. Only confirmed outputs are selected (a replacement may not add
    /// unconfirmed inputs, BIP 125).
    /// </summary>
    /// <param name="owner">The HTLC output whose transaction the inputs pay for (the reservation's key).</param>
    /// <param name="baseWeight">The weight without fee inputs.</param>
    /// <param name="feeratePerKw">The feerate the transaction will pay.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    /// <returns>The reserved inputs and the change script, or null (nothing reserved) when the wallet cannot pay.</returns>
    Task<AnchorFeeInputSelection?> SelectAsync(AnchorFeeInputOwner owner, long baseWeight, uint feeratePerKw,
                                               CancellationToken cancellationToken);

    /// <summary>
    /// Adds the witnesses of the fee inputs (every input from index 1, each <c>SIGHASH_ALL</c>) to a combined
    /// transaction whose input 0 already carries the HTLC witness; returns the fully signed transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">An input is not a wallet output the wallet can sign.</exception>
    Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                      CancellationToken cancellationToken);

    /// <summary>Ends the reservation of <paramref name="owner"/> (idempotent: nothing reserved is not an error).</summary>
    Task ReleaseAsync(AnchorFeeInputOwner owner, CancellationToken cancellationToken);
}

/// <summary>The HTLC output a reservation of fee inputs belongs to (see <see cref="IAnchorFeeInputProvider"/>).</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="CommitmentTxId">Our commitment transaction that holds the HTLC output.</param>
/// <param name="OutputIndex">The HTLC output's index in it.</param>
public sealed record AnchorFeeInputOwner(ChannelId ChannelId, TxId CommitmentTxId, uint OutputIndex);

/// <summary>What <see cref="IAnchorFeeInputProvider.SelectAsync"/> reserved.</summary>
/// <param name="Inputs">The wallet outputs to add as fee inputs.</param>
/// <param name="ChangeScript">The wallet script the change (if any) pays to.</param>
public sealed record AnchorFeeInputSelection(IReadOnlyList<AnchorFeeInput> Inputs, byte[] ChangeScript);