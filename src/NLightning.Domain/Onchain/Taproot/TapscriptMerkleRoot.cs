using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Onchain.Taproot;

/// <summary>
/// The BIP 341 tapscript merkle root of a P2TR output, recomputed from one of its leaves and that leaf's control block
/// (simple taproot channels, NL-966): <c>k = TapLeaf(leaf_version || compact_size(leaf) || leaf)</c>, then for each
/// 32-byte node of the control block's inclusion proof <c>k = TapBranch(min(k, node) || max(k, node))</c>. A key-path
/// spend of the output tweaks its internal key with this root, so a resolver that recorded a leaf and its control block
/// (an HTLC output, a second-level output) can sign the revocation key path without the whole tree.
/// </summary>
public static class TapscriptMerkleRoot
{
    private const int ControlBlockBaseLength = 33;
    private const int NodeLength = 32;
    private const int MaxNodes = 128;
    private const byte LeafVersionMask = 0xfe;

    private static readonly byte[] s_tapLeafTag = SHA256.HashData(Encoding.ASCII.GetBytes("TapLeaf"));
    private static readonly byte[] s_tapBranchTag = SHA256.HashData(Encoding.ASCII.GetBytes("TapBranch"));

    /// <summary>The merkle root committed by <paramref name="controlBlock"/> for <paramref name="leafScript"/>.</summary>
    /// <param name="leafScript">The tapscript leaf.</param>
    /// <param name="controlBlock">Its control block: <c>(parity | leaf_version) || internal key || nodes</c>.</param>
    /// <exception cref="ArgumentException">The control block is malformed.</exception>
    public static byte[] Compute(byte[] leafScript, byte[] controlBlock)
    {
        ArgumentNullException.ThrowIfNull(leafScript);
        ArgumentNullException.ThrowIfNull(controlBlock);
        if (!IsControlBlock(controlBlock))
            throw new ArgumentException("Not a BIP 341 control block", nameof(controlBlock));

        var compactSize = CompactSize(leafScript.Length);
        var leafMessage = new byte[1 + compactSize.Length + leafScript.Length];
        leafMessage[0] = (byte)(controlBlock[0] & LeafVersionMask);
        compactSize.CopyTo(leafMessage, 1);
        leafScript.CopyTo(leafMessage, 1 + compactSize.Length);
        var k = TaggedHash(s_tapLeafTag, leafMessage);

        for (var offset = ControlBlockBaseLength; offset < controlBlock.Length; offset += NodeLength)
        {
            var node = controlBlock.AsSpan(offset, NodeLength);
            var branch = new byte[2 * NodeLength];
            var nodeFirst = node.SequenceCompareTo(k) < 0;
            (nodeFirst ? node : k).CopyTo(branch);
            (nodeFirst ? k : node).CopyTo(branch.AsSpan(NodeLength));
            k = TaggedHash(s_tapBranchTag, branch);
        }

        return k;
    }

    /// <summary>
    /// Whether <paramref name="item"/> has the shape of a BIP 341 control block with the tapscript leaf version
    /// <c>0xc0</c>: 33 + 32·m bytes (m ≤ 128).
    /// </summary>
    public static bool IsControlBlock(byte[]? item) =>
        item is { Length: >= ControlBlockBaseLength }
     && (item.Length - ControlBlockBaseLength) % NodeLength == 0
     && (item.Length - ControlBlockBaseLength) / NodeLength <= MaxNodes
     && (item[0] & LeafVersionMask) == 0xc0;

    private static byte[] TaggedHash(byte[] tagHash, ReadOnlySpan<byte> message)
    {
        var data = new byte[2 * tagHash.Length + message.Length];
        tagHash.CopyTo(data, 0);
        tagHash.CopyTo(data, tagHash.Length);
        message.CopyTo(data.AsSpan(2 * tagHash.Length));
        return SHA256.HashData(data);
    }

    private static byte[] CompactSize(int value) => value switch
    {
        < 0xfd => [(byte)value],
        <= 0xffff => [0xfd, (byte)value, (byte)(value >> 8)],
        _ => [0xfe, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]
    };
}