namespace NLightning.Domain.Bitcoin.Events;

using Money;
using ValueObjects;

public class WalletMovementEventArgs : EventArgs
{
    public string WalletAddress { get; }
    public LightningMoney Amount { get; }
    public TxId TxId { get; }
    public uint BlockHeight { get; }

    /// <summary>The deposit's output in <see cref="TxId"/> (NL-997: a Cashu deposit is named by its outpoint).</summary>
    public uint OutputIndex { get; }

    public WalletMovementEventArgs(string walletAddress, LightningMoney amount, TxId txId, uint blockHeight,
                                   uint outputIndex = 0)
    {
        OutputIndex = outputIndex;
        WalletAddress = walletAddress;
        Amount = amount;
        TxId = txId;
        BlockHeight = blockHeight;
    }
}