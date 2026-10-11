using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Taproot;

using KeyRing;

/// <summary>LND/btcd's ordered full-tree assembly and BIP 341 inclusion-proof hashing for watch-only imports.</summary>
public static class TapscriptImport
{
    public static byte[] Leaf(uint version, byte[] script)
    {
        if (version > 254 || (version & 1) != 0 || script.Length > 10_000)
            throw new ArgumentException("invalid tapleaf version or script size");
        return new TapScript(new Script(script), (TapLeafVersion)version).LeafHash.ToBytes();
    }

    public static byte[] Branch(byte[] left, byte[] right)
    {
        if (left.Length != 32 || right.Length != 32)
            throw new ArgumentException("tapbranch nodes must be 32 bytes");
        return SwapSigner.TaggedHash("TapBranch", left.AsSpan().SequenceCompareTo(right) < 0
                                                    ? [.. left, .. right] : [.. right, .. left]);
    }

    public static byte[] FullTree(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count is 0 or > 128)
            throw new ArgumentException("full_tree must have 1..128 leaves");
        if (leaves.Count == 1) return leaves[0];
        var branches = new List<byte[]>();
        for (var i = 0; i < leaves.Count; i += 2)
        {
            if (i == leaves.Count - 1)
                branches[^1] = Branch(branches[^1], leaves[i]);
            else
                branches.Add(Branch(leaves[i], leaves[i + 1]));
        }
        var queue = new Queue<byte[]>(branches);
        while (queue.Count > 1) queue.Enqueue(Branch(queue.Dequeue(), queue.Dequeue()));
        return queue.Dequeue();
    }
}