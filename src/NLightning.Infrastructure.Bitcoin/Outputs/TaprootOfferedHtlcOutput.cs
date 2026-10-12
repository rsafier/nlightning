using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// An HTLC the commitment holder offered: timeout leaf <c>&lt;local_htlcpubkey&gt; OP_CHECKSIGVERIFY
/// &lt;remote_htlcpubkey&gt; OP_CHECKSIG</c>, success leaf with the preimage, <c>remote_htlcpubkey</c> and a 1-block CSV.
/// </summary>
public sealed class TaprootOfferedHtlcOutput : TaprootHtlcOutput
{
    public TaprootOfferedHtlcOutput(LightningMoney amount, ulong cltvExpiry, PubKey localHtlcPubKey,
                                    ReadOnlyMemory<byte> paymentHash, PubKey remoteHtlcPubKey,
                                    PubKey revocationPubKey)
        : base(amount, cltvExpiry, localHtlcPubKey, paymentHash, remoteHtlcPubKey, revocationPubKey,
               SimpleTaprootScripts.CreateOfferedHtlcTimeoutScript(localHtlcPubKey, remoteHtlcPubKey),
               SimpleTaprootScripts.CreateOfferedHtlcSuccessScript(paymentHash.Span, remoteHtlcPubKey))
    {
    }
}