using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed record WalletUtxo(TxId TxId, uint Index, LightningMoney Amount, uint BlockHeight, uint AddressIndex,
                                bool IsAddressChange, AddressType AddressType, ChannelId? LockedToChannelId,
                                TxId? UsedInTransactionId, WalletAddressModel? WalletAddress)
{
    public static WalletUtxo From(UtxoModel value) => new(value.TxId, value.Index, value.Amount, value.BlockHeight,
        value.AddressIndex, value.IsAddressChange, value.AddressType, value.LockedToChannelId, value.UsedInTransactionId,
        value.WalletAddress);
}
public sealed record FeeReservation(TxId TxId, uint Index, Guid ReservationId);
/// <summary>Host-supplied wallet context; it is not independent signer policy or proof of chain ownership.</summary>
public sealed record WalletSnapshot(UtxoModel[] Utxos, FeeReservation[] Reservations)
{
    public static WalletSnapshot Create(IUtxoMemoryRepository? wallet, SignedTransaction transaction)
    {
        if (wallet is null) throw new InvalidOperationException("Remote wallet signing requires a wallet context repository.");
        var tx = Transaction.Load(transaction.RawTxBytes, NBitcoin.Network.Main);
        var utxos = new List<UtxoModel>(); var reservations = new List<FeeReservation>();
        foreach (var input in tx.Inputs)
        {
            var txid = new TxId(input.PrevOut.Hash.ToBytes());
            if (wallet.TryGetUtxo(txid, input.PrevOut.N, out var utxo)) utxos.Add(utxo);
            if (wallet.TryGetFeeReservation(txid, input.PrevOut.N, out var reservation))
                reservations.Add(new FeeReservation(txid, input.PrevOut.N, reservation));
        }
        return new WalletSnapshot(utxos.ToArray(), reservations.ToArray());
    }
    public void Clear(IUtxoMemoryRepository wallet)
    {
        foreach (var utxo in Utxos) wallet.Spend(utxo);
        foreach (var reservation in Reservations.Select(r => r.ReservationId).Distinct()) wallet.ReleaseFeeReservation(reservation);
    }
    public void Apply(IUtxoMemoryRepository wallet)
    {
        foreach (var utxo in Utxos) { wallet.Spend(utxo); wallet.Add(utxo); }
        wallet.LoadFeeReservations(Reservations.Select(r => (r.TxId, r.Index, r.ReservationId)));
    }
}