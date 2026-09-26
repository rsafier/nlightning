namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Channels.ValueObjects;
using Models;
using Money;

/// <summary>
/// Keeps the on-chain wallet reserve of <c>option_anchors</c> channels (NL-379; <c>Node:Anchors</c>): refuses channel
/// opens and accepts that the wallet could not back, and selects channel funding inputs that never take the wallet below
/// the reserve nor spend an output one of our pending broadcasts already spends (NL-385).
/// </summary>
/// <remarks>
/// Fee-input reservations (<see cref="IFeeInputSelector"/>) are not limited by the reserve: the CPFP child of our
/// commitment and the fee inputs of our anchors HTLC transactions are what it is kept for.
/// </remarks>
public interface IAnchorReserveService
{
    /// <summary>The <c>option_anchors</c> channels that need the reserve (every one not yet Closed or Stale).</summary>
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
    /// As the fundee of an <c>option_anchors</c> channel: throws <see cref="Exceptions.AnchorReserveException"/> when
    /// the confirmed available balance does not cover the reserve including the new channel.
    /// </summary>
    Task EnsureCanAcceptAnchorsChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// As the opener: throws <see cref="Exceptions.AnchorReserveException"/> when the confirmed available balance minus
    /// <paramref name="fundingAmount"/> would not cover the reserve (including the new channel when
    /// <paramref name="anchorsChannel"/>).
    /// </summary>
    Task EnsureCanFundAsync(LightningMoney fundingAmount, bool anchorsChannel,
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks wallet outputs for the funding of <paramref name="channelId"/> (see
    /// <c>IUtxoMemoryRepository.LockUtxosToSpendOnChannel</c>), leaving at least the reserve (including the new
    /// channel when <paramref name="anchorsChannel"/>) unlocked and never picking an output spent by one of our pending
    /// broadcasts. Throws <see cref="Exceptions.AnchorReserveException"/> when that is not possible.
    /// </summary>
    Task<List<UtxoModel>> LockFundingUtxosAsync(LightningMoney fundingAmount, ChannelId channelId, bool anchorsChannel,
                                                CancellationToken cancellationToken = default);
}