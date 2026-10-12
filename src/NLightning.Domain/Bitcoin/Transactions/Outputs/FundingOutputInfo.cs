namespace NLightning.Domain.Bitcoin.Transactions.Outputs;

using Crypto.ValueObjects;
using Enums;
using Interfaces;
using Money;
using ValueObjects;

public class FundingOutputInfo : IOutputInfo
{
    public LightningMoney Amount { get; }
    public CompactPubKey LocalFundingPubKey { get; set; }
    public CompactPubKey RemoteFundingPubKey { get; set; }

    public OutputType OutputType => OutputType.Funding;
    public TxId? TransactionId { get; set; }
    public ushort? Index { get; set; }

    /// <summary>
    /// The output is a simple taproot channel's MuSig2 P2TR output (the BIP 86 key path of
    /// <c>KeyAgg(KeySort(both funding keys))</c>, bolt-simple-taproot.md §Funding Transactions; NL-877 T5, NL-953)
    /// instead of the P2WSH 2-of-2.
    /// </summary>
    public bool IsSimpleTaproot { get; set; }

    public FundingOutputInfo(LightningMoney amount, CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey)
    {
        Amount = amount;
        LocalFundingPubKey = localFundingPubKey;
        RemoteFundingPubKey = remoteFundingPubKey;
    }

    public FundingOutputInfo(LightningMoney amount, CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey,
                             TxId transactionId, ushort index)
        : this(amount, localFundingPubKey, remoteFundingPubKey)
    {
        TransactionId = transactionId;
        Index = index;
    }
}