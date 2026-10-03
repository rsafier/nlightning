using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// The output of a simple taproot HTLC-success or HTLC-timeout transaction: internal key <c>revocation_pubkey</c> (the
/// penalty is a key-path spend) and one leaf <c>&lt;local_delayedpubkey&gt; OP_CHECKSIGVERIFY &lt;to_self_delay&gt;
/// OP_CSV</c>.
/// </summary>
public sealed class TaprootHtlcResolutionOutput : BaseTaprootOutput
{
    public PubKey RevocationPubKey { get; }
    public PubKey LocalDelayedPubKey { get; }
    public uint ToSelfDelay { get; }

    /// <summary>The delay leaf.</summary>
    public TapScript DelayLeaf => Tree.Leaves[0];

    public TaprootHtlcResolutionOutput(LightningMoney amount, PubKey localDelayedPubKey, PubKey revocationPubKey,
                                       uint toSelfDelay)
        : base(amount, TapscriptTree.Create(revocationPubKey,
                                            SimpleTaprootScripts.CreateToLocalDelayScript(localDelayedPubKey,
                                                toSelfDelay)))
    {
        RevocationPubKey = revocationPubKey;
        LocalDelayedPubKey = localDelayedPubKey;
        ToSelfDelay = toSelfDelay;
    }
}