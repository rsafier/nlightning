using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// A simple taproot anchor: internal key the owner's key (the holder's <c>local_delayedpubkey</c> for to_local_anchor,
/// the other side's <c>remotepubkey</c> for to_remote_anchor), spendable by key path by its owner and by anyone through
/// the leaf <c>OP_16 OP_CSV</c> (<c>nSequence = 16</c>).
/// </summary>
public sealed class TaprootAnchorOutput : BaseTaprootOutput
{
    /// <summary>The anchor's internal key.</summary>
    public PubKey InternalPubKey { get; }

    /// <summary>The 16-block sweep leaf.</summary>
    public TapScript SweepLeaf => Tree.Leaves[0];

    public TaprootAnchorOutput(LightningMoney amount, PubKey internalPubKey)
        : base(amount, TapscriptTree.Create(internalPubKey, SimpleTaprootScripts.CreateAnchorScript()))
    {
        InternalPubKey = internalPubKey;
    }
}