using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// An HTLC the commitment holder received (accepted): timeout leaf <c>&lt;remote_htlcpubkey&gt; OP_CHECKSIGVERIFY 1
/// OP_CSV OP_VERIFY &lt;cltv_expiry&gt; OP_CLTV</c>, success leaf with the preimage, <c>local_htlcpubkey</c> and
/// <c>remote_htlcpubkey</c>.
/// </summary>
public sealed class TaprootReceivedHtlcOutput : TaprootHtlcOutput
{
    public TaprootReceivedHtlcOutput(LightningMoney amount, ulong cltvExpiry, PubKey localHtlcPubKey,
                                     ReadOnlyMemory<byte> paymentHash, PubKey remoteHtlcPubKey,
                                     PubKey revocationPubKey)
        : base(amount, cltvExpiry, localHtlcPubKey, paymentHash, remoteHtlcPubKey, revocationPubKey,
               SimpleTaprootScripts.CreateAcceptedHtlcTimeoutScript(remoteHtlcPubKey, checked((uint)cltvExpiry)),
               SimpleTaprootScripts.CreateAcceptedHtlcSuccessScript(paymentHash.Span, localHtlcPubKey,
                                                                     remoteHtlcPubKey))
    {
    }
}