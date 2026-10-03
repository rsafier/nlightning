using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// The simple taproot to_remote output: internal key <see cref="SimpleTaprootScripts.NumsPoint"/> and one leaf
/// <c>&lt;remotepubkey&gt; OP_CHECKSIGVERIFY 1 OP_CSV</c>, spent by script path with <c>nSequence = 1</c>.
/// </summary>
public sealed class TaprootToRemoteOutput : BaseTaprootOutput
{
    public PubKey RemotePubKey { get; }

    /// <summary>The single leaf.</summary>
    public TapScript Leaf => Tree.Leaves[0];

    public TaprootToRemoteOutput(LightningMoney amount, PubKey remotePubKey)
        : base(amount, TapscriptTree.Create(SimpleTaprootScripts.NumsPoint,
                                            SimpleTaprootScripts.CreateToRemoteScript(remotePubKey)))
    {
        RemotePubKey = remotePubKey;
    }
}