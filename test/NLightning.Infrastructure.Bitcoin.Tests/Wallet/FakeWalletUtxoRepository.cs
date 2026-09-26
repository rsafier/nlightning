using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;

/// <summary>
/// The UTXO set and fee reservations of <c>UtxoMemoryRepository</c> (which this project does not reference), enough for
/// the wallet signer and the fee input selector.
/// </summary>
internal sealed class FakeWalletUtxoRepository : IUtxoMemoryRepository
{
    private readonly Dictionary<(TxId, uint), UtxoModel> _utxos = [];
    private readonly Dictionary<(TxId, uint), Guid> _reservations = [];
    private readonly Lock _lock = new();

    /// <summary>Runs before the reservation check of <see cref="TryReserveForFee"/> (to simulate a concurrent lock).</summary>
    public Action? BeforeReserve { get; set; }

    public void Add(UtxoModel utxoModel)
    {
        lock (_lock)
            _utxos.Add((utxoModel.TxId, utxoModel.Index), utxoModel);
    }

    public void Spend(UtxoModel utxoModel)
    {
        lock (_lock)
            _utxos.Remove((utxoModel.TxId, utxoModel.Index));
    }

    public bool TryGetUtxo(TxId txId, uint index, [MaybeNullWhen(false)] out UtxoModel utxoModel)
    {
        lock (_lock)
            return _utxos.TryGetValue((txId, index), out utxoModel);
    }

    public LightningMoney GetConfirmedBalance(uint currentBlockHeight) => throw new NotSupportedException();
    public LightningMoney GetUnconfirmedBalance(uint currentBlockHeight) => throw new NotSupportedException();
    public LightningMoney GetLockedBalance() => throw new NotSupportedException();
    public void Load(List<UtxoModel> utxoSet) => utxoSet.ForEach(Add);

    public List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId) =>
        throw new NotSupportedException();

    public List<UtxoModel> GetLockedUtxosForChannel(ChannelId channelId) => throw new NotSupportedException();
    public List<UtxoModel> ReturnUtxosNotSpentOnChannel(ChannelId channelId) => throw new NotSupportedException();
    public void ConfirmSpendOnChannel(ChannelId channelId) => throw new NotSupportedException();

    public void UpgradeChannelIdOnLockedUtxos(ChannelId oldChannelId, ChannelId newChannelId) =>
        throw new NotSupportedException();

    public List<UtxoModel> GetUnreservedUtxos()
    {
        lock (_lock)
            return _utxos.Values.Where(u => u.LockedToChannelId is null && !_reservations.ContainsKey((u.TxId, u.Index)))
                         .ToList();
    }

    public bool TryReserveForFee(IReadOnlyCollection<(TxId TxId, uint Index)> outpoints, Guid reservationId)
    {
        BeforeReserve?.Invoke();
        lock (_lock)
        {
            if (outpoints.Count == 0 || outpoints.Any(o => !_utxos.TryGetValue(o, out var u)
                                                         || u.LockedToChannelId is not null
                                                         || _reservations.ContainsKey(o)))
                return false;

            foreach (var outpoint in outpoints)
                _reservations[outpoint] = reservationId;
            return true;
        }
    }

    public void ReleaseFeeReservation(Guid reservationId)
    {
        lock (_lock)
        {
            foreach (var key in _reservations.Where(r => r.Value == reservationId).Select(r => r.Key).ToList())
                _reservations.Remove(key);
        }
    }

    public bool TryGetFeeReservation(TxId txId, uint index, out Guid reservationId)
    {
        lock (_lock)
            return _reservations.TryGetValue((txId, index), out reservationId);
    }

    public void LoadFeeReservations(IEnumerable<(TxId TxId, uint Index, Guid ReservationId)> reservations)
    {
        lock (_lock)
        {
            foreach (var (txId, index, id) in reservations)
                _reservations[(txId, index)] = id;
        }
    }
}