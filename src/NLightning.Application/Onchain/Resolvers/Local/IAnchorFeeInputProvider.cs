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
/// The host adapts the wallet's fee-input selector and <c>SignWalletTransaction</c> (O7-T1) to this port. Without one,
/// <see cref="UnavailableAnchorFeeInputProvider"/> selects nothing and the resolver retries every block.
/// </remarks>
public interface IAnchorFeeInputProvider
{
    /// <summary>
    /// Selects and reserves wallet outputs that pay the fee of a transaction of <paramref name="baseWeight"/> (the
    /// HTLC transaction with a change output to the returned script, before any fee input) plus the inputs' own
    /// weights, at <paramref name="feeratePerKw"/>, with change above dust where the wallet can.
    /// </summary>
    /// <param name="channelId">The channel whose HTLC transaction the inputs pay for (for logs and reservations).</param>
    /// <param name="baseWeight">The weight without fee inputs.</param>
    /// <param name="feeratePerKw">The feerate the transaction will pay.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    /// <returns>The reserved inputs and the change script, or null when the wallet cannot pay.</returns>
    Task<AnchorFeeInputSelection?> SelectAsync(ChannelId channelId, long baseWeight, uint feeratePerKw,
                                               CancellationToken cancellationToken);

    /// <summary>
    /// Adds the witnesses of the fee inputs (every input from index 1, each <c>SIGHASH_ALL</c>) to a combined
    /// transaction whose input 0 already carries the HTLC witness; returns the fully signed transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">An input is not a wallet output the wallet can sign.</exception>
    Task<SignedTransaction> SignAsync(SignedTransaction transaction, IReadOnlyList<AnchorFeeInput> feeInputs,
                                      CancellationToken cancellationToken);

    /// <summary>Releases the reservation of inputs that will not be spent (the transaction was not built).</summary>
    Task ReleaseAsync(IReadOnlyList<AnchorFeeInput> feeInputs, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IAnchorFeeInputProvider.SelectAsync"/> reserved.</summary>
/// <param name="Inputs">The wallet outputs to add as fee inputs.</param>
/// <param name="ChangeScript">The wallet script the change (if any) pays to.</param>
public sealed record AnchorFeeInputSelection(IReadOnlyList<AnchorFeeInput> Inputs, byte[] ChangeScript);