using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Taproot;

/// <summary>
/// A BIP 341 output key committed to an internal key and a tapscript tree of one or two leaves (all the shapes
/// <c>option_simple_taproot</c> uses), with the control block of each leaf.
/// </summary>
/// <remarks>
/// Built on NBitcoin's <see cref="TaprootBuilder"/>: leaves are tagged <c>TapLeaf</c> hashes of
/// <c>0xc0 || compact_size(script) || script</c>, two leaves are joined by <c>TapBranch</c> over the lexicographically
/// sorted pair, the output key is <c>internal + tagged_hash("TapTweak", internal || root) * G</c> and a control block is
/// <c>(parity | 0xc0) || internal key || the sibling leaf's hash</c> (no inclusion proof for a single leaf). Byte-exact
/// against bolt-simple-taproot.md's vectors.
/// </remarks>
public sealed class TapscriptTree
{
    private readonly TaprootSpendInfo _spendInfo;

    /// <summary>The internal key (x-only).</summary>
    public TaprootInternalPubKey InternalKey => _spendInfo.InternalPubKey;

    /// <summary>The leaves in the order they were given.</summary>
    public IReadOnlyList<TapScript> Leaves { get; }

    /// <summary>The tapscript root (the single leaf's hash for a one-leaf tree).</summary>
    public uint256 MerkleRoot { get; }

    /// <summary>The tweaked output key with its y parity.</summary>
    public TaprootFullPubKey OutputKey => _spendInfo.OutputPubKey;

    /// <summary>Whether the output key's y coordinate is odd (the parity bit of every control block).</summary>
    public bool OutputKeyParityIsOdd => OutputKey.OutputKeyParity;

    /// <summary>The output's scriptPubKey: <c>OP_1 &lt;32-byte output key&gt;</c>.</summary>
    public Script ScriptPubKey => OutputKey.ScriptPubKey;

    private TapscriptTree(TaprootSpendInfo spendInfo, IReadOnlyList<TapScript> leaves)
    {
        _spendInfo = spendInfo;
        Leaves = leaves;
        MerkleRoot = spendInfo.MerkleRoot
                  ?? throw new InvalidOperationException("A tapscript tree with leaves has a merkle root");
    }

    /// <summary>
    /// Builds the tree of one leaf, or of two leaves at depth 1, committed to <paramref name="internalKey"/>.
    /// </summary>
    /// <param name="internalKey">The internal key; its x coordinate is used (BIP 340).</param>
    /// <param name="leafScripts">One or two leaf scripts, each with leaf version 0xc0.</param>
    public static TapscriptTree Create(PubKey internalKey, params Script[] leafScripts)
    {
        ArgumentNullException.ThrowIfNull(internalKey);
        ArgumentNullException.ThrowIfNull(leafScripts);
        if (leafScripts.Length is < 1 or > 2)
            throw new ArgumentException("A simple taproot tree has one or two leaves", nameof(leafScripts));

        var leaves = leafScripts.Select(s => new TapScript(s ?? throw new ArgumentNullException(nameof(leafScripts)),
                                                           SimpleTaprootScripts.LeafVersion))
                                .ToArray();
        var depth = (uint)(leaves.Length - 1);
        var builder = new TaprootBuilder();
        foreach (var leaf in leaves)
            builder.AddLeaf(depth, leaf);

        return new TapscriptTree(builder.Finalize(internalKey.TaprootInternalKey), leaves);
    }

    /// <summary>The control block that proves <paramref name="leaf"/> is in this tree.</summary>
    public ControlBlock GetControlBlock(TapScript leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        if (!_spendInfo.TryGetControlBlock(leaf, out var controlBlock) || controlBlock is null)
            throw new ArgumentException("The leaf is not in this tapscript tree", nameof(leaf));

        return controlBlock;
    }
}