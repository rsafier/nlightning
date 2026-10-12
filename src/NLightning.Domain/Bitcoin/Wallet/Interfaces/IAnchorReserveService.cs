namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Channels.Models;
using Channels.ValueObjects;
using Models;
using Money;

/// <summary>
/// Keeps the on-chain wallet reserve of <c>option_anchors</c> channels (NL-379; <c>Node:Anchors</c>): refuses channel
/// opens and accepts that the wallet could not back, and selects channel funding inputs that never take the wallet below
/// the reserve (the funding transaction's fee included) nor spend an output one of our pending broadcasts already spends
/// (NL-385).
/// </summary>
/// <remarks>
/// Anchors channels still being opened count toward the reserve from the check that admits them
/// (<see cref="EnsureCanAcceptAnchorsChannelAsync"/>, <see cref="EnsureCanFundAsync"/>) until they are funded, dropped or
/// <c>Node:Anchors:PendingOpenTimeout</c> passes; the checks are serialized, so two concurrent opens or accepts cannot
/// both pass against the reserve of one. Fee-input reservations (<see cref="IFeeInputSelector"/>) are not limited by
/// the reserve: the CPFP child of our commitment and the fee inputs of our anchors HTLC transactions are what it is kept
/// for.
/// </remarks>
public interface IAnchorReserveService
{
    /// <summary>
    /// The <c>option_anchors</c> channels that need the reserve: every one not yet Closed or Stale, plus the anchors
    /// channels still being opened that a check admitted.
    /// </summary>
    int CountAnchorsChannels();

    /// <summary>
    /// The reserve for the current anchors channels plus <paramref name="additionalAnchorsChannels"/> new ones.
    /// </summary>
    LightningMoney GetRequiredReserve(int additionalAnchorsChannels = 0);

    /// <summary>
    /// The reserve and the confirmed balance around it now (for <c>walletbalance</c>).
    /// </summary>
    Task<AnchorReserveStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// As the fundee of the <c>option_anchors</c> channel <paramref name="channel"/> (still under its temporary id):
    /// throws <see cref="Exceptions.AnchorReserveException"/> when the confirmed available balance does not cover the
    /// reserve including it; otherwise it counts toward the reserve from now on.
    /// </summary>
    Task EnsureCanAcceptAnchorsChannelAsync(ChannelModel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// As the opener of <paramref name="channel"/> (still under its temporary id): throws
    /// <see cref="Exceptions.AnchorReserveException"/> when the confirmed available balance minus
    /// <paramref name="fundingAmount"/> and the funding transaction's fee (at the channel's feerate) would not cover the
    /// reserve, including the new channel when it has anchors; an anchors channel counts toward the reserve from then on.
    /// </summary>
    Task EnsureCanFundAsync(LightningMoney fundingAmount, ChannelModel channel,
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks wallet outputs for the funding of <paramref name="channel"/> (see
    /// <c>IUtxoMemoryRepository.LockUtxosToSpendOnChannel</c>): enough for the amount and the funding transaction's fee
    /// at the channel's feerate, leaving at least the reserve (including the new channel when it has anchors) and never
    /// picking an output spent by one of our pending broadcasts. Throws
    /// <see cref="Exceptions.AnchorReserveException"/> when that is not possible.
    /// </summary>
    Task<List<UtxoModel>> LockFundingUtxosAsync(LightningMoney fundingAmount, ChannelModel channel,
                                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops counting the open of <paramref name="temporaryChannelId"/> as pending (it was funded, and so counts as a
    /// channel, or it failed). Unknown ids are ignored.
    /// </summary>
    void ReleasePendingChannel(ChannelId temporaryChannelId);
}