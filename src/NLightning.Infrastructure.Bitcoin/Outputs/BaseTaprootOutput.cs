using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Outputs;

using Domain.Money;
using Taproot;

/// <summary>
/// A P2TR output of a simple taproot channel (bolt-simple-taproot.md): <c>OP_1 &lt;output key&gt;</c> committed to an
/// internal key and a tapscript tree (<see cref="Tree"/>). It has no redeem script: a script-path spend reveals one of
/// the tree's leaves with its control block, a key-path spend signs with the tweaked internal key.
/// </summary>
public abstract class BaseTaprootOutput : BaseOutput
{
    /// <summary>The output's internal key, tapscript tree and output key.</summary>
    public TapscriptTree Tree { get; }

    protected BaseTaprootOutput(LightningMoney amount, TapscriptTree tree)
        : base(amount, Script.Empty, (tree ?? throw new ArgumentNullException(nameof(tree))).ScriptPubKey,
               ScriptType.Taproot)
    {
        Tree = tree;
    }

    /// <summary>The control block of <paramref name="leaf"/>, as bytes for a witness.</summary>
    public byte[] GetControlBlock(TapScript leaf) => Tree.GetControlBlock(leaf).ToBytes();

    /// <inheritdoc />
    /// <remarks>A taproot output has no redeem script, so it is never a <see cref="ScriptCoin"/>.</remarks>
    public override ScriptCoin ToCoin() =>
        throw new NotSupportedException("A taproot output is spent by key or script path; use its tapscript tree");
}