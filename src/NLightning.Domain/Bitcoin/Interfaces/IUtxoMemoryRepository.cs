using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;
using Money;
using ValueObjects;
using Wallet.Models;

public interface IUtxoMemoryRepository
{
    void Add(UtxoModel utxoModel);
    void Spend(UtxoModel utxoModel);
    bool TryGetUtxo(TxId txId, uint index, [MaybeNullWhen(false)] out UtxoModel utxoModel);
    LightningMoney GetConfirmedBalance(uint currentBlockHeight);
    LightningMoney GetUnconfirmedBalance(uint currentBlockHeight);

    /// <summary>
    /// The wallet outputs with at least <paramref name="minConfirmations"/> confirmations at
    /// <paramref name="currentBlockHeight"/> (an output mined at the tip has 1; one with no block height, or above the
    /// tip, has 0, so <paramref name="minConfirmations"/> 0 is every output). Locks and reservations are not checked.
    /// Only for reports that follow another rule than the wallet's own (LND's 1-confirmation <c>WalletBalance</c>,
    /// NL-1236): <see cref="GetConfirmedBalance"/> is the node's rule.
    /// </summary>
    LightningMoney GetBalanceWithConfirmations(uint currentBlockHeight, uint minConfirmations);

    LightningMoney GetLockedBalance();
    void Load(List<UtxoModel> utxoSet);
    /// <summary>
    /// Locks outputs worth at least <paramref name="requestFundingAmount"/> for the funding of
    /// <paramref name="channelId"/>, with no reserve and no excluded outpoints. The node funds channels through
    /// <c>IAnchorReserveService.LockFundingUtxosAsync</c>, which uses the overload that keeps the anchors reserve
    /// (NL-379) and skips outputs spent by our pending broadcasts (NL-385).
    /// </summary>
    List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId);

    /// <summary>
    /// Locks outputs worth at least <paramref name="requestFundingAmount"/> for the funding of
    /// <paramref name="channelId"/> plus the worst-case fee of its funding transaction at
    /// <paramref name="fundingFeeRatePerKw"/> (P2WPKH inputs, a P2TR change output), from the P2WPKH/P2TR outputs
    /// neither locked, reserved for a fee nor in <paramref name="excludedOutpoints"/> (outputs our own pending
    /// broadcasts spend, NL-385). When <paramref name="reserveToKeep"/> (the anchors reserve, NL-379) is not zero, the
    /// outputs left that back it (see <see cref="GetAvailableConfirmedBalance"/>) plus the least change the funding
    /// returns (after its fee; none when dust) must cover it. Atomic against the other lock and reservation calls.
    /// Throws <see cref="InvalidOperationException"/> when there are no free outputs or they do not cover the amount
    /// and fee, and <see cref="Exceptions.AnchorReserveException"/> when they do but not with the reserve.
    /// </summary>
    List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId,
                                              LightningMoney reserveToKeep,
                                              IReadOnlySet<(TxId TxId, uint Index)> excludedOutpoints,
                                              LightningMoney fundingFeeRatePerKw, uint currentBlockHeight);

    /// <summary>
    /// The balance that backs the anchors reserve: the outputs neither locked to a channel, reserved for a fee nor in
    /// <paramref name="excludedOutpoints"/> that the fee input selector can spend (mined, with a known P2WPKH or P2TR
    /// address) and that are confirmed by the rule of <see cref="GetConfirmedBalance"/>.
    /// </summary>
    LightningMoney GetAvailableConfirmedBalance(uint currentBlockHeight,
                                                IReadOnlySet<(TxId TxId, uint Index)> excludedOutpoints);
    List<UtxoModel> GetLockedUtxosForChannel(ChannelId channelId);
    List<UtxoModel> ReturnUtxosNotSpentOnChannel(ChannelId channelId);
    void ConfirmSpendOnChannel(ChannelId channelId);
    void UpgradeChannelIdOnLockedUtxos(ChannelId oldChannelId, ChannelId newChannelId);

    /// <summary>
    /// Re-locks every outpoint of <paramref name="outpoints"/> that is still a free wallet output to
    /// <paramref name="channelId"/>, at startup (NL-462: the channel locks of a funder are memory only, so the pending
    /// funding of a V1FundingSigned channel gets them back). Outpoints that are spent, or locked to another channel,
    /// are skipped. Returns how many were locked.
    /// </summary>
    int RestoreLocksForChannel(ChannelId channelId, IReadOnlyCollection<(TxId TxId, uint Index)> outpoints);

    /// <summary>
    /// The wallet outputs neither locked to a channel funding nor reserved for a fee (BOLT 5 plan O7-T1).
    /// </summary>
    List<UtxoModel> GetUnreservedUtxos();

    /// <summary>
    /// Reserves every one of <paramref name="outpoints"/> for the fee reservation <paramref name="reservationId"/>, or
    /// none: false when one is unknown, locked to a channel or already reserved. Atomic against the other reservation
    /// and channel-lock calls.
    /// </summary>
    bool TryReserveForFee(IReadOnlyCollection<(TxId TxId, uint Index)> outpoints, Guid reservationId);

    /// <summary>Clears the reservation <paramref name="reservationId"/> from every outpoint that carries it.</summary>
    void ReleaseFeeReservation(Guid reservationId);

    /// <summary>A snapshot of every outpoint carrying this reservation, including temporarily missing reorg outputs.</summary>
    IReadOnlyList<(TxId TxId, uint Index)> GetFeeReservedOutpoints(Guid reservationId) =>
        throw new NotSupportedException("This wallet repository cannot enumerate fee reservations.");

    /// <summary>The fee reservation of an outpoint, if any (the outpoint need not be in the UTXO set).</summary>
    bool TryGetFeeReservation(TxId txId, uint index, out Guid reservationId);

    /// <summary>Restores the persisted fee reservations at startup (kept even for outpoints not in the set).</summary>
    void LoadFeeReservations(IEnumerable<(TxId TxId, uint Index, Guid ReservationId)> reservations);
}