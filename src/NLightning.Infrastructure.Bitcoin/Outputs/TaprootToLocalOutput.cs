using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// The simple taproot to_local output: internal key <see cref="SimpleTaprootScripts.NumsPoint"/>, the delay leaf
/// (spent by the holder after <c>to_self_delay</c>, <c>nSequence = to_self_delay</c>) and the revocation leaf (the
/// penalty). Both spends are script-path spends.
/// </summary>
public sealed class TaprootToLocalOutput : BaseTaprootOutput
{
    public PubKey LocalDelayedPubKey { get; }
    public PubKey RevocationPubKey { get; }
    public uint ToSelfDelay { get; }

    /// <summary>The delay leaf: <c>&lt;local_delayedpubkey&gt; OP_CHECKSIGVERIFY &lt;to_self_delay&gt; OP_CSV</c>.</summary>
    public TapScript DelayLeaf => Tree.Leaves[0];

    /// <summary>The revocation leaf: <c>&lt;local_delayedpubkey&gt; OP_DROP &lt;revocation_pubkey&gt; OP_CHECKSIG</c>.</summary>
    public TapScript RevokeLeaf => Tree.Leaves[1];

    public TaprootToLocalOutput(LightningMoney amount, PubKey localDelayedPubKey, PubKey revocationPubKey,
                                uint toSelfDelay)
        : base(amount, TapscriptTree.Create(SimpleTaprootScripts.NumsPoint,
                                            SimpleTaprootScripts.CreateToLocalDelayScript(localDelayedPubKey,
                                                toSelfDelay),
                                            SimpleTaprootScripts.CreateToLocalRevokeScript(localDelayedPubKey,
                                                revocationPubKey)))
    {
        LocalDelayedPubKey = localDelayedPubKey;
        RevocationPubKey = revocationPubKey;
        ToSelfDelay = toSelfDelay;
    }
}