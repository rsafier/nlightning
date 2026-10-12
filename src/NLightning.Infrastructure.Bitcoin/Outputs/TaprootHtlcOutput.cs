using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// A simple taproot HTLC output: internal key <c>revocation_pubkey</c> (the penalty is a key-path spend) and two leaves,
/// <see cref="TimeoutLeaf"/> and <see cref="SuccessLeaf"/>; a script-path spend proves its leaf with the other leaf's
/// hash.
/// </summary>
public abstract class TaprootHtlcOutput : BaseTaprootOutput
{
    public PubKey RevocationPubKey { get; }
    public PubKey LocalHtlcPubKey { get; }
    public PubKey RemoteHtlcPubKey { get; }
    public ReadOnlyMemory<byte> PaymentHash { get; }
    public ulong CltvExpiry { get; }

    /// <summary>The timeout leaf (offered: the HTLC-timeout transaction's; accepted: the other side's claim).</summary>
    public TapScript TimeoutLeaf => Tree.Leaves[0];

    /// <summary>The success leaf (offered: the other side's preimage claim; accepted: the HTLC-success transaction's).</summary>
    public TapScript SuccessLeaf => Tree.Leaves[1];

    protected TaprootHtlcOutput(LightningMoney amount, ulong cltvExpiry, PubKey localHtlcPubKey,
                                ReadOnlyMemory<byte> paymentHash, PubKey remoteHtlcPubKey, PubKey revocationPubKey,
                                Script timeoutScript, Script successScript)
        : base(amount, TapscriptTree.Create(revocationPubKey, timeoutScript, successScript))
    {
        RevocationPubKey = revocationPubKey;
        LocalHtlcPubKey = localHtlcPubKey;
        RemoteHtlcPubKey = remoteHtlcPubKey;
        PaymentHash = paymentHash;
        CltvExpiry = cltvExpiry;
    }
}