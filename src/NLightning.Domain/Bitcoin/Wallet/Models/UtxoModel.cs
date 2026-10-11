namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Channels.ValueObjects;
using Enums;
using Money;
using ValueObjects;

public sealed class UtxoModel
{
    public TxId TxId { get; }
    public uint Index { get; }
    public LightningMoney Amount { get; }
    public uint BlockHeight { get; }
    public uint AddressIndex { get; private set; }
    public bool IsAddressChange { get; private set; }
    public AddressType AddressType { get; private set; }
    public ChannelId? LockedToChannelId { get; set; }
    public TxId? UsedInTransactionId { get; set; }

    public WalletAddressModel? WalletAddress { get; private set; }
    public SilentPaymentOutputModel? SilentPayment { get; private set; }

    public UtxoModel(TxId txId, uint index, LightningMoney amount, uint blockHeight, uint addressIndex,
                     bool isAddressChange, AddressType addressType)
    {
        TxId = txId;
        Index = index;
        Amount = amount;
        BlockHeight = blockHeight;
        AddressIndex = addressIndex;
        IsAddressChange = isAddressChange;
        AddressType = addressType;
    }

    public UtxoModel(TxId txId, uint index, LightningMoney amount, uint blockHeight, WalletAddressModel walletAddress)
    {
        TxId = txId;
        Index = index;
        Amount = amount;
        BlockHeight = blockHeight;
        SetWalletAddress(walletAddress);
    }

    public UtxoModel(SilentPaymentOutputModel output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Ignored || output.SpentByTransactionId is not null || output.AmountSats < 0
            || output.OutputKey.Length != 32 || output.Tweak.Length != 32)
            throw new ArgumentException("An ignored, spent or malformed silent payment is not spendable.", nameof(output));
        TxId = output.TransactionId;
        Index = output.Index;
        Amount = LightningMoney.Satoshis(output.AmountSats);
        BlockHeight = output.BlockHeight;
        AddressType = AddressType.P2Tr;
        SilentPayment = output;
    }

    /// <summary>
    /// Whether this output backs the anchors reserve (NL-379) at <paramref name="currentBlockHeight"/>: the fee input
    /// selector can spend it (mined, with a known P2WPKH or P2TR address) and it is confirmed by the wallet's
    /// three-block rule. Locks and reservations are not checked here.
    /// </summary>
    public bool BacksAnchorReserve(uint currentBlockHeight) =>
        BlockHeight != 0 && BlockHeight + 3 <= currentBlockHeight && (WalletAddress is not null || SilentPayment is not null)
     && AddressType is (AddressType.P2Wpkh or AddressType.P2Tr);

    public void SetWalletAddress(WalletAddressModel walletAddress)
    {
        ArgumentNullException.ThrowIfNull(walletAddress);
        SilentPayment = null;
        WalletAddress = walletAddress;

        AddressIndex = walletAddress.Index;
        IsAddressChange = walletAddress.IsChange;
        AddressType = walletAddress.AddressType;
    }
}