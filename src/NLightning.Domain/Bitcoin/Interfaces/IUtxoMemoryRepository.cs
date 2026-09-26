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
    LightningMoney GetLockedBalance();
    void Load(List<UtxoModel> utxoSet);
    List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId);
    List<UtxoModel> GetLockedUtxosForChannel(ChannelId channelId);
    List<UtxoModel> ReturnUtxosNotSpentOnChannel(ChannelId channelId);
    void ConfirmSpendOnChannel(ChannelId channelId);
    void UpgradeChannelIdOnLockedUtxos(ChannelId oldChannelId, ChannelId newChannelId);

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

    /// <summary>The fee reservation of an outpoint, if any (the outpoint need not be in the UTXO set).</summary>
    bool TryGetFeeReservation(TxId txId, uint index, out Guid reservationId);

    /// <summary>Restores the persisted fee reservations at startup (kept even for outpoints not in the set).</summary>
    void LoadFeeReservations(IEnumerable<(TxId TxId, uint Index, Guid ReservationId)> reservations);
}