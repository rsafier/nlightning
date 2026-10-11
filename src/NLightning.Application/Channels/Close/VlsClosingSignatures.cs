using NBitcoin;
using NBitcoinTransaction = NBitcoin.Transaction;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Our signature of an agreed legacy closing transaction, read back from the stored transaction's witness: a VLS node
/// answers a restarted negotiation with the exact signature VLS gave once, never with a new request (NL-1330).
/// </summary>
internal static class VlsClosingSignatures
{
    public static CompactSignature FromStoredClosing(SignedTransaction stored, ChannelModel channel)
    {
        var tx = NBitcoinTransaction.Load(stored.RawTxBytes, Network.RegTest);
        if (tx.Inputs.Count != 1 || tx.Inputs[0].WitScript.PushCount != 4)
            throw new InvalidOperationException(
                $"The stored closing transaction of channel {channel.ChannelId} is not a 2-of-2 funding spend");

        var witness = tx.Inputs[0].WitScript;
        var keys = PayToMultiSigTemplate.Instance.ExtractScriptPubKeyParameters(new Script(witness[3]))?.PubKeys
                ?? throw new InvalidOperationException(
                       $"The stored closing transaction of channel {channel.ChannelId} has no funding script");
        var ours = new PubKey((byte[])channel.LocalFundingPubKey);
        var index = Array.IndexOf(keys, ours);
        if (index < 0)
            throw new InvalidOperationException(
                $"The stored closing transaction of channel {channel.ChannelId} is not signed by our funding key");

        return new CompactSignature(new TransactionSignature(witness[index + 1]).Signature.ToCompact());
    }
}